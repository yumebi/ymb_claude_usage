namespace ClaudeUsage.App.Services.Providers;

/// <summary>
/// ゲージ1行分。Percent があればバー表示、null なら値テキストのみ表示。
/// </summary>
public sealed record GaugeRow(string Label, string ValueText, double? Percent, DateTimeOffset? ResetsAt);

/// <summary>テーブル1行分(ラベル + 最大2値)。</summary>
public sealed record TableRow(string Label, string Value, string Value2 = "", string? Tooltip = null);

/// <summary>
/// 1プロバイダーの表示パネルに必要なデータ一式。
/// TableTitle が null かつ TableRows が空ならテーブル自体を表示しない。
/// </summary>
public sealed record ProviderPanelData(
    IReadOnlyList<GaugeRow> Gauges,
    string? TableTitle,
    string Header1,
    string Header2,
    string Header3,
    IReadOnlyList<TableRow> TableRows,
    string? EmptyText,
    string? Error)
{
    /// <summary>週間使用率(%)の推移(時系列順)。null なら推移グラフを表示しない。</summary>
    public IReadOnlyList<double>? Trend { get; init; }

    /// <summary>2つ目のテーブル(プロジェクト別集計など)。null なら表示しない。</summary>
    public string? SecondaryTitle { get; init; }

    public IReadOnlyList<TableRow> SecondaryRows { get; init; } = [];

    /// <summary>2つ目のテーブルの空メッセージ。</summary>
    public string? SecondaryEmptyText { get; init; }

    public static ProviderPanelData FromError(string error) =>
        new([], null, "", "", "", [], null, error);
}

/// <summary>
/// 使用量データ源の抽象化。Claude(OAuth API + ローカルJSONL)も
/// providers.json で定義した汎用RESTサービスも同じ形でパネル化する。
/// </summary>
public interface IUsageProvider
{
    string Name { get; }

    /// <summary>
    /// パネルデータを取得する。呼び出しはUIタイマー(毎分)から来るため、
    /// 各プロバイダーは自身の更新間隔を内部で守る(force=trueなら即時再取得)。
    /// 失敗しても例外を投げず、Error に理由を入れて返すこと。
    /// </summary>
    Task<ProviderPanelData> FetchAsync(bool force, CancellationToken ct);
}
