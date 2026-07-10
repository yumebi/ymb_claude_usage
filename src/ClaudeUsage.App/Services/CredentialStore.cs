using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsage.App.Services;

/// <summary>
/// Claude Code の OAuth 認証情報(~/.claude/.credentials.json)の読み込みと、
/// 期限切れ時の refresh_token による自動更新を担当する。
/// 更新に成功したら同ファイルへ書き戻す(Claude Code CLI と同じ形式を維持)。
/// </summary>
public sealed class CredentialStore
{
    // Claude Code の公開OAuthクライアントID
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";

    private static string CredentialsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private readonly HttpClient _http;
    private DateTimeOffset _refreshBackoffUntil = DateTimeOffset.MinValue;

    public CredentialStore(HttpClient http) => _http = http;

    /// <summary>直近のエラー内容(UI表示用)。正常時はnull。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 有効なアクセストークンを返す。期限切れなら更新を試みる。
    /// 取得できない場合はnull(LastErrorに理由)。
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        JsonNode? root;
        try
        {
            if (!File.Exists(CredentialsPath))
            {
                LastError = "認証ファイルなし (~/.claude/.credentials.json)";
                return null;
            }
            root = JsonNode.Parse(await File.ReadAllTextAsync(CredentialsPath, ct));
        }
        catch (Exception ex)
        {
            LastError = $"認証ファイル読込失敗: {ex.Message}";
            return null;
        }

        var oauth = root?["claudeAiOauth"];
        var accessToken = oauth?["accessToken"]?.GetValue<string>();
        var refreshToken = oauth?["refreshToken"]?.GetValue<string>();
        var expiresAt = oauth?["expiresAt"]?.GetValue<long>() ?? 0;

        if (string.IsNullOrEmpty(accessToken))
        {
            LastError = "アクセストークンなし。Claude Codeで再ログインしてください";
            return null;
        }

        // 期限まで5分以上あればそのまま使う
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (expiresAt - now > 5 * 60 * 1000)
        {
            LastError = null;
            return accessToken;
        }

        // 期限切れ → リフレッシュ(429バックオフ中はスキップして古いトークンで試す)
        if (string.IsNullOrEmpty(refreshToken))
        {
            LastError = "リフレッシュトークンなし。Claude Codeで再ログインしてください";
            return accessToken;
        }
        if (DateTimeOffset.UtcNow < _refreshBackoffUntil)
            return accessToken;

        return await RefreshAsync(root!, refreshToken, ct) ?? accessToken;
    }

    private async Task<string?> RefreshAsync(JsonNode root, string refreshToken, CancellationToken ct)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync(TokenEndpoint, new
            {
                grant_type = "refresh_token",
                refresh_token = refreshToken,
                client_id = ClientId,
            }, ct);

            if ((int)res.StatusCode == 429)
            {
                _refreshBackoffUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                LastError = "トークン更新がレート制限中(自動再試行します)";
                return null;
            }
            if (!res.IsSuccessStatusCode)
            {
                LastError = $"トークン更新失敗 (HTTP {(int)res.StatusCode})。Claude Codeで再ログインしてください";
                return null;
            }

            var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            var newAccess = body?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(newAccess))
            {
                LastError = "トークン更新応答が不正";
                return null;
            }

            var oauth = root["claudeAiOauth"]!;
            oauth["accessToken"] = newAccess;
            var newRefresh = body?["refresh_token"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(newRefresh))
                oauth["refreshToken"] = newRefresh;
            var expiresIn = body?["expires_in"]?.GetValue<long>() ?? 3600;
            oauth["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000;

            // 他のキーは保持したまま書き戻す
            await File.WriteAllTextAsync(CredentialsPath, root.ToJsonString(new() { WriteIndented = true }), ct);

            LastError = null;
            return newAccess;
        }
        catch (Exception ex)
        {
            LastError = $"トークン更新エラー: {ex.Message}";
            return null;
        }
    }
}
