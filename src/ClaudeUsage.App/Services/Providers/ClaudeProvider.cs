using System.Net.Http;

namespace ClaudeUsage.App.Services.Providers;

/// <summary>
/// 既存の Claude 表示(OAuth使用量API + ローカルJSONL集計)を1プロバイダーとしてまとめたもの。
/// ローカル集計は毎回、APIは設定間隔ごと(手動更新は即時)という従来の間隔ロジックを維持する。
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private readonly UsageApiClient _api;
    private readonly LocalUsageScanner _scanner = new();
    private readonly Func<int> _refreshMinutes;

    private DateTimeOffset _lastApiFetch = DateTimeOffset.MinValue;
    private IReadOnlyList<UsageBucket> _buckets = [];
    private string? _apiError;

    public string Name => "Claude";

    /// <summary>週間(全モデル)の使用率。トレイアイコンの色に使う。未取得ならnull。</summary>
    public double? WeeklyPercent { get; private set; }

    public ClaudeProvider(HttpClient http, Func<int> refreshMinutes)
    {
        _api = new UsageApiClient(http, new CredentialStore(http));
        _refreshMinutes = refreshMinutes;
    }

    public async Task<ProviderPanelData> FetchAsync(bool force, CancellationToken ct)
    {
        // ローカル集計は毎回(軽い。ファイルキャッシュあり)
        var local = await Task.Run(() => _scanner.Scan(), ct);

        // APIは設定間隔ごと(手動更新は即時)
        var interval = TimeSpan.FromMinutes(Math.Max(1, _refreshMinutes()));
        if (force || DateTimeOffset.Now - _lastApiFetch >= interval)
        {
            var result = await _api.FetchAsync(ct);
            _lastApiFetch = DateTimeOffset.Now;
            if (result.Buckets.Count > 0)
                _buckets = result.Buckets;
            _apiError = result.Error;
        }

        WeeklyPercent = (_buckets.FirstOrDefault(b => b.Key == "seven_day")
                      ?? _buckets.FirstOrDefault())?.UtilizationPercent;

        var gauges = _buckets
            .Select(b => new GaugeRow(b.Label, $"{b.UtilizationPercent:0.#}%", b.UtilizationPercent, b.ResetsAt))
            .ToList();

        var rows = local.Week
            .OrderByDescending(kv => kv.Value.Input + kv.Value.Output)
            .Select(kv =>
            {
                var week = kv.Value;
                local.Today.TryGetValue(kv.Key, out var today);
                var tooltip = $"{kv.Key}\n" +
                    $"今日: 入力 {today?.Input ?? 0:N0} / 出力 {today?.Output ?? 0:N0} / キャッシュ読取 {today?.CacheRead ?? 0:N0}\n" +
                    $"7日間: 入力 {week.Input:N0} / 出力 {week.Output:N0} / キャッシュ読取 {week.CacheRead:N0}\n" +
                    $"リクエスト数(7日): {week.Requests:N0}";
                return new TableRow(
                    PrettyModelName(kv.Key),
                    FormatTokens((today?.Input ?? 0) + (today?.Output ?? 0)),
                    FormatTokens(week.Input + week.Output),
                    tooltip);
            })
            .ToList();

        return new ProviderPanelData(
            gauges,
            "トークン使用量(ローカル集計)",
            "モデル", "今日", "7日間",
            rows,
            "直近7日間の記録なし",
            _apiError);
    }

    private static string PrettyModelName(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("fable")) return "Fable";
        if (m.Contains("opus")) return "Opus";
        if (m.Contains("sonnet")) return "Sonnet";
        if (m.Contains("haiku")) return "Haiku";
        return model;
    }

    /// <summary>1234567 → "1.23M" のような短縮表記。</summary>
    private static string FormatTokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.##}M",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };
}
