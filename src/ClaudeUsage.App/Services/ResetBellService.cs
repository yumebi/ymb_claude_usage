using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClaudeUsage.App.Native;

namespace ClaudeUsage.App.Services;

/// <summary>
/// 使用量枠のリセット時刻にベルを鳴らす。
///
/// APIは各枠の次回リセット時刻(resets_at)を返すので、その時刻を控えておき、
/// 到達したら鳴らす。次の自動更新を待って気付く方式だと更新間隔(既定5分)ぶん
/// 遅れるため、時刻そのものを見る。
///
/// 7日先まで走る単一の長時間タイマーはスリープ復帰で挙動が崩れるため、
/// 短い間隔で時計を見る方式にしている。APIを叩く回数は増えない。
///
/// 「いつ鳴らすか」の判断は <see cref="ResetSchedule"/> に分けてある。
/// </summary>
public sealed class ResetBellService : IDisposable
{
    /// <summary>セッション枠(5時間)。</summary>
    public const string SessionKey = "five_hour";

    /// <summary>週間枠(全モデル)。</summary>
    public const string WeeklyKey = "seven_day";

    private readonly ResetSchedule _schedule = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(10) };

    private readonly Func<bool> _enabled;
    private readonly Action _onReset;

    private readonly SoundInterop.WaveBuffer? _sessionBell;
    private readonly SoundInterop.WaveBuffer? _weeklyBell;

    /// <param name="enabled">鳴らすかどうか。設定で切れるようにするため都度問い合わせる。</param>
    /// <param name="onReset">リセット検知後に呼ぶ。次のリセット時刻を取り直すのに使う。</param>
    public ResetBellService(Func<bool> enabled, Action onReset)
    {
        _enabled = enabled;
        _onReset = onReset;

        _sessionBell = LoadBell("Assets/bell-session.wav");
        _weeklyBell = LoadBell("Assets/bell-weekly.wav");

        _ticker.Tick += (_, _) => Tick();
        _ticker.Start();
    }

    /// <summary>
    /// 取得した枠から次回リセット時刻を控える。更新のたびに呼ぶ。
    ///
    /// モデル別の週間枠(seven_day_opus など)は全モデル枠と同時にリセットされるため
    /// 対象にしない。全部鳴らすと同じ瞬間に何度も鳴る。
    /// </summary>
    public void UpdateSchedule(IEnumerable<UsageBucket> buckets)
    {
        foreach (var b in buckets)
        {
            if (b.Key is SessionKey or WeeklyKey)
                _schedule.Update(b.Key, b.ResetsAt);
        }
    }

    private void Tick()
    {
        var events = _schedule.Collect(DateTimeOffset.Now);
        if (events.Count == 0)
            return;

        if (_enabled())
        {
            foreach (var e in events)
            {
                if (e.ShouldRing)
                    Ring(e.Key);
            }
        }

        // 新しいリセット時刻を取りに行く。これが次回ぶんの控えになる。
        _onReset();
    }

    private void Ring(string key)
    {
        var bell = key == SessionKey ? _sessionBell : _weeklyBell;
        if (bell is not null)
            SoundInterop.Play(bell.Pointer);
    }

    private static SoundInterop.WaveBuffer? LoadBell(string relativeUri)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri(relativeUri, UriKind.Relative));
            if (info is null)
                return null;

            using var stream = info.Stream;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return new SoundInterop.WaveBuffer(ms.ToArray());
        }
        catch
        {
            // 音が出ないだけで監視自体は続けたいので握りつぶす
            return null;
        }
    }

    public void Dispose()
    {
        _ticker.Stop();
        _sessionBell?.Dispose();
        _weeklyBell?.Dispose();
    }
}
