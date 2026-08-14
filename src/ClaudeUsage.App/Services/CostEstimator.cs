namespace ClaudeUsage.App.Services;

/// <summary>1Mトークンあたりの価格(USD)。単価は Anthropic の公表レート。</summary>
public sealed record ModelRates(double Input, double Output, double CacheWrite)
{
    /// <summary>キャッシュ読取は入力の1割の料金。</summary>
    public double CacheRead => Input * 0.1;

    public double Estimate(ModelTotals t) =>
        t.Input * Input / 1_000_000 +
        t.Output * Output / 1_000_000 +
        t.CacheRead * CacheRead / 1_000_000 +
        t.CacheCreate * CacheWrite / 1_000_000;
}

/// <summary>
/// モデル別トークン使用量から概算費用(USD)を計算する。
/// 料金は変更されるため、ここだけを直せばよい(下の表を1箇所で管理)。
/// 未知のモデルは Sonnet 相当で計算する(単価が中間的なため)。
/// </summary>
public static class CostEstimator
{
    // 1MトークンあたりUSD。公表レート(2026-08時点)で、キャッシュ書込は入力の1.25倍。
    private static readonly ModelRates Opus = new(15.0, 75.0, 18.75);
    private static readonly ModelRates Sonnet = new(3.0, 15.0, 3.75);
    private static readonly ModelRates Haiku = new(0.8, 4.0, 1.0);
    private static readonly ModelRates Fable = new(3.0, 15.0, 3.75);

    public static ModelRates RatesFor(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("fable")) return Fable;
        if (m.Contains("opus")) return Opus;
        if (m.Contains("sonnet")) return Sonnet;
        if (m.Contains("haiku")) return Haiku;
        return Sonnet;
    }

    /// <summary>1モデルの7日間分の概算費用(USD)。</summary>
    public static double Estimate(string model, ModelTotals totals) =>
        RatesFor(model).Estimate(totals);

    /// <summary>モデル別集計全体の概算費用(USD)。</summary>
    public static double EstimateAll(IReadOnlyDictionary<string, ModelTotals> week)
    {
        var total = 0.0;
        foreach (var (model, totals) in week)
            total += Estimate(model, totals);
        return total;
    }
}
