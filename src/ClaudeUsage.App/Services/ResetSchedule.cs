namespace ClaudeUsage.App.Services;

/// <summary>リセットに到達した枠。</summary>
/// <param name="Key">枠のキー(five_hour / seven_day)。</param>
/// <param name="ShouldRing">
/// 鳴らしてよいか。予定時刻を大きく過ぎている場合は false になる。
/// </param>
public readonly record struct ResetEvent(string Key, bool ShouldRing);

/// <summary>
/// リセット時刻の管理。「いつ鳴らすか」の判断だけを持ち、
/// タイマーや音の再生は <see cref="ResetBellService"/> 側に置く。
///
/// 5時間・7日という周期のため実運用では滅多に発火せず、動かして確かめるのが難しい。
/// 時計に依存しない形にして、境界の挙動をテストで固定している。
/// </summary>
public sealed class ResetSchedule
{
    /// <summary>
    /// 予定時刻をこれ以上過ぎていたら鳴らさない。
    /// スリープや電源断の間に過ぎたリセットで、復帰した瞬間に鳴るのを防ぐ。
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, DateTimeOffset> _deadlines = [];
    private readonly Dictionary<string, DateTimeOffset> _lastFired = [];

    /// <summary>
    /// 枠の次回リセット時刻を控える。
    ///
    /// 既に発火済みの時刻以前を渡された場合は無視する。鳴らした直後の再取得で
    /// APIがまだ古い時刻を返すことがあり、そのまま控え直すと同じリセットで
    /// 繰り返し鳴ってしまう。
    /// </summary>
    public void Update(string key, DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } at)
        {
            _deadlines.Remove(key);
            return;
        }

        if (_lastFired.TryGetValue(key, out var fired) && at <= fired)
            return;

        _deadlines[key] = at;
    }

    /// <summary>
    /// 到達した枠を取り出す。取り出した枠は控えから外れるので、
    /// 次回ぶんは <see cref="Update"/> で改めて登録する必要がある。
    /// </summary>
    public IReadOnlyList<ResetEvent> Collect(DateTimeOffset now)
    {
        List<ResetEvent>? events = null;

        foreach (var (key, deadline) in _deadlines)
        {
            if (now < deadline)
                continue;
            (events ??= []).Add(new ResetEvent(key, now - deadline <= StaleAfter));
        }

        if (events is null)
            return [];

        foreach (var e in events)
        {
            _lastFired[e.Key] = _deadlines[e.Key];
            _deadlines.Remove(e.Key);
        }

        return events;
    }

    /// <summary>控えている枠の数。テストと診断用。</summary>
    public int PendingCount => _deadlines.Count;
}
