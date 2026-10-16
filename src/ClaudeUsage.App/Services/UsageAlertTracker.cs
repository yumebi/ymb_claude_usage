namespace ClaudeUsage.App.Services;

/// <summary>
/// 使用率がしきい値(80% / 95% / 100% など)を超えた瞬間を検知する。
///
/// 通知は「超えた瞬間に1回」だけにしたい。更新のたびに通知すると鬱陶しく、
/// かといって一度通知したきり二度と出さないと、リセット後の次の上昇に気づけない。
/// そこで枠(キー)ごとに「通知済みの段階」を覚え、使用率が十分に下がったら(リセット後など)
/// 次の上昇でまた通知できる状態に戻す。
///
/// UI や通知の仕組みから切り離した純粋なロジックにして、単体テストできるようにしてある。
/// </summary>
public sealed class UsageAlertTracker
{
    /// <summary>通知済みの段階。再通知できる状態は未登録(= まだ何も通知していない)。</summary>
    private readonly Dictionary<string, int> _notified = new();

    private readonly int[] _thresholds;

    /// <summary>最小のしきい値からこれだけ下がったら、再通知できる状態に戻す(ヒステリシス)。</summary>
    private const int RearmMargin = 20;

    /// <param name="thresholds">しきい値(%)。重複・範囲外・順不同でもよい。空なら通知しない。</param>
    public UsageAlertTracker(IEnumerable<int>? thresholds)
    {
        _thresholds = (thresholds ?? [])
            .Where(t => t is >= 1 and <= 100)
            .Distinct()
            .OrderBy(t => t)
            .ToArray();
    }

    /// <summary>設定されているしきい値(昇順)。</summary>
    public IReadOnlyList<int> Thresholds => _thresholds;

    /// <summary>
    /// 現在の使用率を渡す。新しいしきい値を超えたときだけ、そのしきい値(複数同時に超えたら最大のもの)を返す。
    /// </summary>
    /// <returns>通知すべきしきい値。通知不要なら null。</returns>
    public int? Evaluate(string key, double percent)
    {
        if (_thresholds.Length == 0 || double.IsNaN(percent))
            return null;

        var level = 0;
        foreach (var t in _thresholds)
        {
            if (percent >= t)
                level = t;
        }

        if (level == 0)
        {
            // 十分下がったら、次の上昇でまた通知できるようにする
            if (percent < _thresholds[0] - RearmMargin)
                _notified.Remove(key);
            return null;
        }

        if (_notified.TryGetValue(key, out var done) && level <= done)
            return null;

        _notified[key] = level;
        return level;
    }
}
