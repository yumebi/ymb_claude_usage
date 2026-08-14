using System.IO;
using System.Text.Json;
using ClaudeUsage.App.Models;

namespace ClaudeUsage.App.Services;

/// <summary>
/// 週間使用率(%)のスナップショットを %APPDATA%\YmbClaudeUsage\usage_history.json に
/// 保存し、推移グラフの元データを提供する。
///
/// 更新間隔(既定5分)のたびに全履歴を書き直すと無駄が多いため、
/// 直前と同じ値は記録しない(変化点のみ残す)。ただし値が変化し続けない期間が長くても
/// 途切れないよう、最長30分で1点は強制的に記録する。
/// 件数上限を超えたら古い順に切り捨てる。
/// </summary>
public sealed class UsageHistoryStore
{
    private const int MaxPoints = 90;
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(30);

    private readonly List<double> _points = [];

    /// <summary>記録済みの使用率(時系列順)。</summary>
    public IReadOnlyList<double> Points => _points;

    private static string FilePath => Path.Combine(AppSettings.Directory, "usage_history.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var values = JsonSerializer.Deserialize<List<double>>(File.ReadAllText(FilePath));
            if (values is null)
                return;
            _points.Clear();
            _points.AddRange(values);
            Trim();
        }
        catch
        {
            // 壊れた履歴は捨てて作り直す
            _points.Clear();
        }
    }

    /// <summary>使用率を記録する。直前と同じ値なら(30分以上空いていなければ)スキップ。</summary>
    public void Record(double percent)
    {
        var now = DateTimeOffset.Now;
        if (_points.Count > 0 && Math.Abs(_points[^1] - percent) < 0.05)
        {
            if (now - _lastRecordedAt < MaxGap)
                return;
        }

        _points.Add(percent);
        _lastRecordedAt = now;
        Trim();
        Save();
    }

    private DateTimeOffset _lastRecordedAt;

    private void Trim()
    {
        while (_points.Count > MaxPoints)
            _points.RemoveAt(0);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            // 書き込み途中のクラッシュで履歴全体が壊れないよう一時ファイル経由で置き換える
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_points));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // 保存失敗は致命的ではないので無視
        }
    }
}
