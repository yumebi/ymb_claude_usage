using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace ClaudeUsage.App.Models;

/// <summary>複数プロバイダー表示時のパネル配置。</summary>
public enum DisplayMode
{
    Tabs,
    Vertical,
    Horizontal,
}

/// <summary>
/// アプリ設定。%APPDATA%\YmbClaudeUsage\settings.json に保存する。
/// </summary>
public sealed class AppSettings
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;

    /// <summary>常に最前面に表示するか。</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>壁紙の上(WorkerW層)に固定するか。有効時はAlwaysOnTopより優先。</summary>
    public bool DesktopPin { get; set; }

    /// <summary>使用量APIの自動更新間隔(分)。</summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>ウィンドウの不透明度(0.3〜1.0)。</summary>
    public double Opacity { get; set; } = 0.95;

    /// <summary>セッション枠・週間枠のリセット時にベルを鳴らすか。</summary>
    public bool ResetBell { get; set; } = true;

    /// <summary>複数プロバイダー時のパネル配置(タブ/縦並び/横並び)。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Tabs;

    /// <summary>
    /// SuperGrokの週間枠がリセットされる時刻の基準点。
    /// grok.com の使用量画面に出る「◯年◯月◯日 ◯:◯ にリセット」を1度だけ書けば、
    /// 以後は7日周期で次回リセットを計算し続けるので再入力は要らない。
    /// 週間枠の使用率(%)を返す公開APIが無いためリセット時刻だけの推定表示になる。
    /// 未設定(null)ならGrokパネルにリセット行を出さない。
    /// </summary>
    public DateTimeOffset? GrokWeeklyResetAnchor { get; set; }

    /// <summary>
    /// 直近の <see cref="Load"/> が(リトライしても)失敗し、初期値インスタンスを返した理由。
    /// 成功時・ファイル未作成(初回起動)時は null。設定ファイルには保存しない。
    /// </summary>
    [JsonIgnore]
    public string? LoadError { get; private set; }

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YmbClaudeUsage");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>読み取り・解析失敗時のリトライ回数。</summary>
    private const int MaxLoadAttempts = 3;

    /// <summary>リトライ間の待機時間。起動時の合計待ちが0.5秒程度に収まるよう短めにする。</summary>
    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromMilliseconds(200);

    public static AppSettings Load()
    {
        // ファイル未作成は異常ではない(初回起動)。初期値をLoadError無しで返す
        if (!File.Exists(FilePath))
            return new AppSettings();

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxLoadAttempts; attempt++)
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                    return settings;
                lastError = new InvalidDataException("設定ファイルの内容を復元できませんでした");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            // 一過性のロック等を想定し、最後の試行以外は短く待って再試行する
            if (attempt < MaxLoadAttempts)
                Thread.Sleep(LoadRetryDelay);
        }

        // 数回リトライしても失敗した場合のみ、理由付きの初期値インスタンスを返す
        return new AppSettings { LoadError = lastError?.Message ?? "不明なエラー" };
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 保存失敗は致命的ではないので無視
        }
    }
}
