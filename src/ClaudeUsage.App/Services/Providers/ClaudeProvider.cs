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

        // API失敗中は、デスクトップアプリが残した履歴からの概算に差し替える。
        // それも取れなければ従来どおり直近のAPI値(古い可能性あり)を出す。
        List<GaugeRow> gauges = _apiError is not null ? BuildFallbackGauges() : [];
        if (gauges.Count == 0)
            gauges = _buckets
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

    /// <summary>
    /// OAuth使用量APIが使えない時の代替表示。デスクトップアプリの履歴ファイルにある最新サンプルから、
    /// 5時間枠と週間(全モデル)の使用率を「概算」として出す。
    /// 採取は数時間おきで古いことがあるので、値には採取時刻を添え、すでに枠が入れ替わっている値は出さない。
    /// </summary>
    private List<GaugeRow> BuildFallbackGauges()
    {
        var rows = new List<GaugeRow>();
        var latest = DesktopPlanUsageReader.ReadLatest();
        if (latest is null)
            return rows;

        var now = DateTimeOffset.Now;
        var age = now - latest.SampledAt;
        var at = latest.SampledAt.ToLocalTime();
        var atText = $"{at:M/d H:mm}時点";

        // 5時間枠: 採取から5時間以上経っていれば別の枠なので、古い値は出さない
        if (age < TimeSpan.FromHours(5))
        {
            rows.Add(new GaugeRow("セッション (5時間・概算)",
                $"{latest.FiveHourPercent:0.#}% ({atText})", latest.FiveHourPercent, null));
        }

        // 週間: 直近に取れたAPI値のリセット時刻があれば7日周期で先送りして次回を推定する。
        // 採取がその直前のリセットより前なら、すでに週が入れ替わっているので出さない。
        var nextReset = EstimateNextWeeklyReset(now);
        var weekStillValid = nextReset is { } next
            ? latest.SampledAt >= next - TimeSpan.FromDays(7)
            : age < TimeSpan.FromDays(7);
        if (weekStillValid)
        {
            rows.Add(new GaugeRow("週間 (全モデル・概算)",
                $"{latest.WeeklyPercent:0.#}% ({atText})", latest.WeeklyPercent, nextReset));
        }

        return rows;
    }

    /// <summary>
    /// 直近に取れた週間枠のリセット時刻から、7日周期で今より後の最初のリセットを推定する。
    /// 一度もAPIが成功していない(基準が無い)場合は null。
    /// </summary>
    private DateTimeOffset? EstimateNextWeeklyReset(DateTimeOffset now)
    {
        var known = _buckets.FirstOrDefault(b => b.Key == "seven_day")?.ResetsAt;
        if (known is not { } anchor)
            return null;

        var cycle = TimeSpan.FromDays(7);
        if (anchor > now)
            return anchor;
        var cycles = (long)Math.Floor((now - anchor) / cycle) + 1;
        return anchor + TimeSpan.FromTicks(cycle.Ticks * cycles);
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
