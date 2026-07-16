using System.Net.Http;
using System.Text.Json;

namespace ClaudeUsage.App.Services;

/// <summary>使用量APIの1バケット(5時間セッション、週間など)。</summary>
public sealed record UsageBucket(string Key, string Label, double UtilizationPercent, DateTimeOffset? ResetsAt);

public sealed record UsageResult(IReadOnlyList<UsageBucket> Buckets, string? Error);

/// <summary>
/// Claude の OAuth 使用量API(/api/oauth/usage)から制限の使用率を取得する。
/// レスポンスのキー名は固定せず、{utilization, resets_at} 形のオブジェクトを
/// すべてバケットとして拾う(将来モデル枠が増えても表示できるように)。
/// </summary>
public sealed class UsageApiClient
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly HttpClient _http;
    private readonly CredentialStore _credentials;

    public UsageApiClient(HttpClient http, CredentialStore credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    public async Task<UsageResult> FetchAsync(CancellationToken ct)
    {
        var token = await _credentials.GetAccessTokenAsync(ct);
        if (token is null)
            return new UsageResult([], _credentials.LastError ?? "認証情報を取得できません");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                var hint = (int)res.StatusCode == 401
                    ? _credentials.NeedsReLogin
                        ? "(ログインの有効期限が切れています。ターミナルで `claude` を実行して再ログインしてください)"
                        : "(トークン更新待ち。しばらくして自動回復します)"
                    : "";
                return new UsageResult([], $"使用量API HTTP {(int)res.StatusCode} {hint}");
            }

            return new UsageResult(ParseBuckets(body), null);
        }
        catch (Exception ex)
        {
            return new UsageResult([], $"使用量API接続失敗: {ex.Message}");
        }
    }

    private static List<UsageBucket> ParseBuckets(string json)
    {
        var buckets = new List<UsageBucket>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return buckets;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (!prop.Value.TryGetProperty("utilization", out var util) ||
                util.ValueKind != JsonValueKind.Number)
                continue;

            DateTimeOffset? resetsAt = null;
            if (prop.Value.TryGetProperty("resets_at", out var reset) &&
                reset.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(reset.GetString(), out var parsed))
                resetsAt = parsed;

            buckets.Add(new UsageBucket(prop.Name, LabelFor(prop.Name), util.GetDouble(), resetsAt));
        }

        // limits 配列のモデルスコープ付き枠(例: 週間Fable)を追加で拾う
        ParseScopedLimits(doc.RootElement, buckets);

        // 表示順: セッション → 週間全体 → モデル別枠 → その他
        return buckets
            .OrderBy(b => b.Key switch
            {
                "five_hour" => 0,
                "seven_day" => 1,
                _ => b.Key.StartsWith("seven_day", StringComparison.Ordinal) ? 2 : 3,
            })
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// limits 配列(要素形: {kind, group, percent, severity, resets_at, scope, is_active})から
    /// モデルスコープ付きの枠だけをバケットとして追加する。
    /// scope が null の要素(session / weekly_all)はトップレベルの five_hour / seven_day と
    /// 同じ枠なのでスキップし、二重表示を防ぐ。
    /// </summary>
    private static void ParseScopedLimits(JsonElement root, List<UsageBucket> buckets)
    {
        if (!root.TryGetProperty("limits", out var limits) ||
            limits.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in limits.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            // モデルスコープ付きのみ対象(display_name 必須)
            if (!item.TryGetProperty("scope", out var scope) ||
                scope.ValueKind != JsonValueKind.Object ||
                !scope.TryGetProperty("model", out var model) ||
                model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("display_name", out var dn) ||
                dn.ValueKind != JsonValueKind.String)
                continue;
            var name = dn.GetString();
            if (string.IsNullOrEmpty(name))
                continue;

            if (!item.TryGetProperty("percent", out var pct) ||
                pct.ValueKind != JsonValueKind.Number)
                continue;

            var isWeekly = item.TryGetProperty("group", out var group) &&
                group.ValueKind == JsonValueKind.String &&
                group.GetString() == "weekly";

            // 週間枠は "seven_day_<model>" キーにして既存ソート(モデル別枠=2番手)に載せる
            var key = (isWeekly ? "seven_day_" : "limit_") + name.ToLowerInvariant().Replace(' ', '_');
            // トップレベル(seven_day_opus 等)が実データを返すようになった場合は重複させない
            if (buckets.Any(b => b.Key == key))
                continue;

            DateTimeOffset? resetsAt = null;
            if (item.TryGetProperty("resets_at", out var reset) &&
                reset.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(reset.GetString(), out var parsed))
                resetsAt = parsed;

            var label = isWeekly ? $"週間 ({name})" : name;
            buckets.Add(new UsageBucket(key, label, pct.GetDouble(), resetsAt));
        }
    }

    private static string LabelFor(string key) => key switch
    {
        "five_hour" => "セッション (5時間)",
        "seven_day" => "週間 (全モデル)",
        "seven_day_opus" => "週間 (Opus)",
        "seven_day_fable" => "週間 (Fable)",
        "seven_day_sonnet" => "週間 (Sonnet)",
        "seven_day_oauth_apps" => "週間 (連携アプリ)",
        _ when key.Contains("fable") => "週間 (Fable)",
        _ when key.Contains("opus") => "週間 (Opus)",
        _ => key,
    };
}
