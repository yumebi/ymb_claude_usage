using System.Net.Http;

namespace ClaudeUsage.App.Services.Providers;

/// <summary>
/// 既存の Claude 表示(OAuth使用量API + ローカルJSONL集計)を1プロバイダーとしてまとめたもの。
/// ローカル集計は毎回、APIは設定間隔ごと(手動更新は即時)という従来の間隔ロジックを維持する。
/// パネルには ゲージ + モデル別テーブル(費用行付き) + プロジェクト別テーブル + 推移グラフ を出す。
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private readonly UsageApiClient _api;
    private readonly LocalUsageScanner _scanner = new();
    private readonly Func<int> _refreshMinutes;
    private readonly UsageHistoryStore? _history;

    private DateTimeOffset _lastApiFetch = DateTimeOffset.MinValue;
    private IReadOnlyList<UsageBucket> _buckets = [];
    private string? _apiError;

    public string Name => "Claude";

    /// <summary>週間(全モデル)の使用率。トレイアイコンの色に使う。未取得ならnull。</summary>
    public double? WeeklyPercent { get; private set; }

    /// <summary>直近に取得した使用量枠。リセット時刻の監視に使う。</summary>
    public IReadOnlyList<UsageBucket> Buckets => _buckets;

    public ClaudeProvider(HttpClient http, Func<int> refreshMinutes, UsageHistoryStore? history = null)
    {
        _api = new UsageApiClient(http, new CredentialStore(http));
        _refreshMinutes = refreshMinutes;
        _history = history;
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
                var cost = CostEstimator.Estimate(kv.Key, week);
                var tooltip = $"{kv.Key}\n" +
                    $"今日: 入力 {today?.Input ?? 0:N0} / 出力 {today?.Output ?? 0:N0} / キャッシュ読取 {today?.CacheRead ?? 0:N0}\n" +
                    $"7日間: 入力 {week.Input:N0} / 出力 {week.Output:N0} / キャッシュ読取 {week.CacheRead:N0}\n" +
                    $"リクエスト数(7日): {week.Requests:N0}\n" +
                    $"推定費用(7日): ${cost:0.##}";
                return new TableRow(
                    PrettyModelName(kv.Key),
                    FormatTokens((today?.Input ?? 0) + (today?.Output ?? 0)),
                    FormatTokens(week.Input + week.Output),
                    tooltip);
            })
            .ToList();

        // モデル別テーブルの末尾に推定費用の合計行を足す
        var weeklyCost = CostEstimator.EstimateAll(local.Week);
        if (weeklyCost > 0)
            rows.Add(new TableRow("推定費用 (7日間)", "", $"${weeklyCost:0.##}"));

        // プロジェクト別テーブル(使用量の多い順・上位10件)
        var projectRows = local.ProjectWeek
            .OrderByDescending(kv => kv.Value.Input + kv.Value.Output)
            .Take(10)
            .Select(kv =>
            {
                var week = kv.Value;
                local.ProjectToday.TryGetValue(kv.Key, out var today);
                var tooltip = $"{kv.Key}\n" +
                    $"今日: 入力 {today?.Input ?? 0:N0} / 出力 {today?.Output ?? 0:N0}\n" +
                    $"7日間: 入力 {week.Input:N0} / 出力 {week.Output:N0} / キャッシュ読取 {week.CacheRead:N0}\n" +
                    $"リクエスト数(7日): {week.Requests:N0}";
                return new TableRow(
                    kv.Key,
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
            _apiError)
        {
            Trend = _history?.Points,
            SecondaryTitle = "プロジェクト別(ローカル集計)",
            SecondaryRows = projectRows,
            SecondaryEmptyText = "直近7日間のプロジェクト記録なし",
        };
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
