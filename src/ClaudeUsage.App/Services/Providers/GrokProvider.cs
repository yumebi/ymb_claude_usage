namespace ClaudeUsage.App.Services.Providers;

/// <summary>
/// Grok Build CLI(~/.grok/)のローカルログ集計を1プロバイダーとしてまとめたもの。
/// ネットワークアクセス・認証は一切行わない(~/.grok/auth.json は読まない)。
/// SuperGrok定額の利用制限(%)を返す公開APIが無いため、ゲージは画像生成の残数のみを
/// パーセントなしのテキスト表示で出す。
/// </summary>
public sealed class GrokProvider : IUsageProvider
{
    /// <summary>SuperGrokの週間枠の周期。</summary>
    private static readonly TimeSpan WeeklyCycle = TimeSpan.FromDays(7);

    private readonly GrokLocalScanner _scanner = new();
    private readonly Func<DateTimeOffset?> _weeklyResetAnchor;

    public GrokProvider(Func<DateTimeOffset?> weeklyResetAnchor) =>
        _weeklyResetAnchor = weeklyResetAnchor;

    public string Name => "Grok";

    public async Task<ProviderPanelData> FetchAsync(bool force, CancellationToken ct)
    {
        // ローカル走査のみ(軽い。ファイルキャッシュあり)なので force に関わらず毎回行う
        var local = await Task.Run(() => _scanner.Scan(), ct);

        var gauges = new List<GaugeRow>();
        // 残高切れのときに一番知りたいのは「いつ戻るか」なので先頭に置く
        if (NextWeeklyReset(_weeklyResetAnchor()) is { } nextReset)
            gauges.Add(new GaugeRow("週間リセット(推定)", FormatReset(nextReset), null, null));
        if (local.ImagesRemaining is { } images)
            gauges.Add(new GaugeRow("画像生成の残り", $"{images:N0}", null, null));

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

        string? error = local.BalanceExhaustedAt is { } exhaustedAt
            ? $"Grok Build: 残高切れ ({exhaustedAt.ToLocalTime():M/d H:mm} 以降)"
            : null;

        return new ProviderPanelData(
            gauges,
            "トークン使用量(ローカル集計)",
            "モデル", "今日", "7日間",
            rows,
            "直近7日間の記録なし",
            error);
    }

    /// <summary>
    /// 基準点から7日周期で、今より後にくる直近のリセット時刻を求める。
    /// 基準点が未設定なら null(リセット行を出さない)。
    /// </summary>
    private static DateTimeOffset? NextWeeklyReset(DateTimeOffset? anchor)
    {
        if (anchor is not { } a)
            return null;

        var now = DateTimeOffset.Now;
        if (a > now)
            return a;

        // 経過した周期数+1 を足せば、必ず「今より後の最初のリセット」になる
        var cycles = (long)Math.Floor((now - a) / WeeklyCycle) + 1;
        return a + TimeSpan.FromTicks(WeeklyCycle.Ticks * cycles);
    }

    private static string FormatReset(DateTimeOffset resetsAt)
    {
        var local = resetsAt.ToLocalTime();
        var remain = local - DateTimeOffset.Now;
        if (remain < TimeSpan.Zero)
            remain = TimeSpan.Zero;
        var remainText = remain.TotalDays >= 1
            ? $"あと{(int)remain.TotalDays}日{remain.Hours}時間"
            : remain.TotalHours >= 1
                ? $"あと{(int)remain.TotalHours}時間{remain.Minutes}分"
                : $"あと{remain.Minutes}分";
        return $"{local:M/d(ddd) H:mm} ({remainText})";
    }

    private static string PrettyModelName(string model)
    {
        if (model == "Grok")
            return model;
        var m = model.ToLowerInvariant();
        var version = m.Replace("grok-", "").Replace("-build", "");
        return $"Grok {version}";
    }

    /// <summary>1234567 → "1.23M" のような短縮表記。</summary>
    private static string FormatTokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.##}M",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };
}
