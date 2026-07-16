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

    /// <summary>
    /// 時間ベースのフォールバック閾値。アクセストークンの期限切れからこの時間が経過しても
    /// 一度も更新できていない(=何度もリフレッシュを試みたはずなのに失敗し続けている)場合、
    /// invalid_grant を検知できないケースでも再ログイン推奨のシグナルを出す。
    /// </summary>
    private const long ReLoginThresholdMs = 30 * 60 * 1000;

    private static string CredentialsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private readonly HttpClient _http;
    private DateTimeOffset _refreshBackoffUntil = DateTimeOffset.MinValue;

    public CredentialStore(HttpClient http) => _http = http;

    /// <summary>直近のエラー内容(UI表示用)。正常時はnull。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 再ログイン(ターミナルで `claude` を実行してのOAuth再認証)が必要と判断された場合に true。
    /// 正常に取得・回復できた時点で false に戻る。
    /// </summary>
    public bool NeedsReLogin { get; private set; }

    /// <summary>
    /// 有効なアクセストークンを返す。期限切れなら更新を試みる。
    /// 取得できない場合はnull(LastErrorに理由)。
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var loaded = await LoadCredentialsAsync(ct);
        if (loaded is null)
            return null;
        var (root, accessToken, refreshToken, expiresAt) = loaded.Value;

        if (string.IsNullOrEmpty(accessToken))
        {
            NeedsReLogin = true;
            LastError = "アクセストークンなし。Claude Codeで再ログインしてください";
            return null;
        }

        // 期限まで5分以上あればそのまま使う
        if (IsFarFromExpiry(expiresAt))
        {
            NeedsReLogin = false;
            LastError = null;
            return accessToken;
        }

        // 期限切れ → リフレッシュ(429バックオフ中はスキップして古いトークンで試す)
        if (string.IsNullOrEmpty(refreshToken))
        {
            NeedsReLogin = true;
            LastError = "リフレッシュトークンなし。Claude Codeで再ログインしてください";
            return accessToken;
        }

        // 時間ベースのフォールバック(判定2): invalid_grant 等のエラーコードで確定判定できない場合でも、
        // 期限切れから長時間(30分以上)経過してなお一度も更新できていないなら再ログイン推奨とする。
        var elapsedSinceExpiry = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - expiresAt;
        if (elapsedSinceExpiry > ReLoginThresholdMs)
            NeedsReLogin = true;

        if (DateTimeOffset.UtcNow < _refreshBackoffUntil)
            return accessToken;

        return await RefreshAsync(root!, refreshToken, ct) ?? accessToken;
    }

    /// <summary>
    /// 認証ファイルを読み込み、(root, accessToken, refreshToken, expiresAt) を返す。
    /// 読み込み失敗時はLastErrorを設定してnullを返す。
    /// </summary>
    private async Task<(JsonNode? Root, string? AccessToken, string? RefreshToken, long ExpiresAt)?> LoadCredentialsAsync(CancellationToken ct)
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

        return (root, accessToken, refreshToken, expiresAt);
    }

    private static bool IsFarFromExpiry(long expiresAt)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return expiresAt - now > 5 * 60 * 1000;
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

            var responseText = await res.Content.ReadAsStringAsync(ct);

            if ((int)res.StatusCode == 429)
            {
                // 他プロセス(Claude Code CLI等)が既にリフレッシュ済みの可能性があるため、
                // バックオフに入る前にファイルを再読込みして新しいアクセストークンがないか確認する。
                var recovered = await TryRecoverFromOtherProcessAsync(ct);
                if (recovered is not null)
                    return recovered;

                // レート制限は一時的な問題であり、refresh_token自体の失効(invalid_grant)とは
                // 明確に区別する。ここでは NeedsReLogin を立てない。
                _refreshBackoffUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                LastError = "トークン更新がレート制限中(自動再試行します)";
                return null;
            }
            if (!res.IsSuccessStatusCode)
            {
                // 429以外の失敗でも、他プロセスが既に更新済みなら回復できる可能性がある。
                var recovered = await TryRecoverFromOtherProcessAsync(ct);
                if (recovered is not null)
                    return recovered;

                // 判定1(確定的): OAuth標準のエラーコードで invalid_grant なら、
                // refresh_token が無効・失効・使用済みであることが確定しているので再ログイン必須とする。
                if (TryGetOAuthErrorCode(responseText) == "invalid_grant")
                {
                    NeedsReLogin = true;
                    LastError = "ログインの有効期限が切れています。ターミナルで `claude` を実行して再ログインしてください";
                    return null;
                }

                LastError = $"トークン更新失敗 (HTTP {(int)res.StatusCode})。Claude Codeで再ログインしてください";
                return null;
            }

            var body = JsonNode.Parse(responseText);
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

            NeedsReLogin = false;
            LastError = null;
            return newAccess;
        }
        catch (Exception ex)
        {
            LastError = $"トークン更新エラー: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// OAuthトークンエンドポイントのエラーレスポンスから標準の `error` コード
    /// (例: "invalid_grant")を取り出す。パース不能な場合は null。
    /// レスポンス形は `{"error": "invalid_grant", ...}` の他、
    /// `{"error": {"type": "invalid_grant", ...}}` のようにネストされる場合も考慮する。
    /// </summary>
    private static string? TryGetOAuthErrorCode(string responseText)
    {
        try
        {
            var node = JsonNode.Parse(responseText)?["error"];
            return node switch
            {
                JsonValue v => v.GetValue<string>(),
                JsonObject o => o["type"]?.GetValue<string>() ?? o["code"]?.GetValue<string>(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// リフレッシュ失敗時に、他プロセス(Claude Code CLI等)が同じ認証ファイルを
    /// 既に更新済みでないかを確認する。ファイル上のexpiresAtが十分先(5分以上)なら、
    /// そのアクセストークンを「回復成功」として返す。古いままなら本当に失敗しているのでnull。
    /// </summary>
    private async Task<string?> TryRecoverFromOtherProcessAsync(CancellationToken ct)
    {
        var reloaded = await LoadCredentialsAsync(ct);
        if (reloaded is null)
            return null;

        var (_, accessToken, _, expiresAt) = reloaded.Value;
        if (string.IsNullOrEmpty(accessToken) || !IsFarFromExpiry(expiresAt))
            return null;

        // 他プロセスが既にリフレッシュ済み。バックオフに入らず、そのトークンをそのまま使う。
        NeedsReLogin = false;
        LastError = null;
        return accessToken;
    }
}
