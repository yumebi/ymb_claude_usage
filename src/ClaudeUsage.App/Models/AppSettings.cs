using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>複数プロバイダー時のパネル配置(タブ/縦並び/横並び)。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Tabs;

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YmbClaudeUsage");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // 壊れた設定ファイルは初期値で作り直す
        }
        return new AppSettings();
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
