namespace ClaudeUsage.App.Services.Providers;

/// <summary>
/// Grok Build CLI(~/.grok/)のローカルログ集計を1プロバイダーとしてまとめたもの。
/// ネットワークアクセス・認証は一切行わない(~/.grok/auth.json は読まない)。
/// SuperGrok定額の利用制限(%)を返す公開APIが無いため、ゲージは画像生成の残数のみを
/// パーセントなしのテキスト表示で出す。
/// </summary>
public sealed class GrokProvider : IUsageProvider
{
    private readonly GrokLocalScanner _scanner = new();

    public string Name => "Grok";

    public async Task<ProviderPanelData> FetchAsync(bool force, CancellationToken ct)
    {
        // ローカル走査のみ(軽い。ファイルキャッシュあり)なので force に関わらず毎回行う
        var local = await Task.Run(() => _scanner.Scan(), ct);

        var gauges = new List<GaugeRow>();
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

        return new ProviderPanelData(
            gauges,
            "トークン使用量(ローカル集計)",
            "モデル", "今日", "7日間",
            rows,
            "直近7日間の記録なし",
            null);
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
