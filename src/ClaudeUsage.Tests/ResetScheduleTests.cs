using ClaudeUsage.App.Services;

namespace ClaudeUsage.Tests;

/// <summary>
/// リセット判定の境界を固定する。
/// 実運用では5時間・7日に1度しか発火せず、動かして確かめるのが難しいため、
/// 時計を渡す形にしてここで検証する。
/// </summary>
public class ResetScheduleTests
{
    private static readonly DateTimeOffset Base =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.FromHours(9));

    private const string Session = "five_hour";
    private const string Weekly = "seven_day";

    [Fact]
    public void 予定時刻前は何も起きない()
    {
        var s = new ResetSchedule();
        s.Update(Session, Base.AddMinutes(30));

        Assert.Empty(s.Collect(Base));
        Assert.Equal(1, s.PendingCount);
    }

    [Fact]
    public void 予定時刻に達したら鳴らす()
    {
        var s = new ResetSchedule();
        s.Update(Session, Base);

        var events = s.Collect(Base);

        var e = Assert.Single(events);
        Assert.Equal(Session, e.Key);
        Assert.True(e.ShouldRing);
    }

    [Fact]
    public void 取り出した枠は控えから外れる()
    {
        var s = new ResetSchedule();
        s.Update(Session, Base);

        s.Collect(Base);

        Assert.Equal(0, s.PendingCount);
        Assert.Empty(s.Collect(Base.AddMinutes(1)));
    }

    [Fact]
    public void 大きく過ぎた予定は鳴らさない()
    {
        // スリープ中にリセット時刻を過ぎ、復帰した状況
        var s = new ResetSchedule();
        s.Update(Session, Base);

        var e = Assert.Single(s.Collect(Base + ResetSchedule.StaleAfter + TimeSpan.FromSeconds(1)));

        Assert.Equal(Session, e.Key);
        Assert.False(e.ShouldRing);
    }

    [Fact]
    public void 猶予ちょうどまでは鳴らす()
    {
        var s = new ResetSchedule();
        s.Update(Session, Base);

        var e = Assert.Single(s.Collect(Base + ResetSchedule.StaleAfter));

        Assert.True(e.ShouldRing);
    }

    [Fact]
    public void 発火済みと同じ時刻を渡されても鳴り直さない()
    {
        // 鳴らした直後の再取得で、APIがまだ古い resets_at を返す状況
        var s = new ResetSchedule();
        s.Update(Session, Base);
        s.Collect(Base);

        s.Update(Session, Base);

        Assert.Equal(0, s.PendingCount);
        Assert.Empty(s.Collect(Base.AddMinutes(1)));
    }

    [Fact]
    public void 次の予定を渡されれば改めて鳴る()
    {
        var s = new ResetSchedule();
        s.Update(Session, Base);
        s.Collect(Base);

        var next = Base.AddHours(5);
        s.Update(Session, next);

        Assert.Empty(s.Collect(next.AddSeconds(-1)));
        Assert.True(Assert.Single(s.Collect(next)).ShouldRing);
    }

    [Fact]
    public void セッションと週間は別々に管理される()
    {
        var s = new ResetSchedule();
        s.Update(Session, Base);
        s.Update(Weekly, Base.AddDays(3));

        var e = Assert.Single(s.Collect(Base));

        Assert.Equal(Session, e.Key);
        Assert.Equal(1, s.PendingCount);
    }

    [Fact]
    public void 同時に達した枠はまとめて返る()
    {
        var s = new ResetSchedule();
        s.Update(Session, Base);
        s.Update(Weekly, Base);

        var events = s.Collect(Base);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, x => x.Key == Session);
        Assert.Contains(events, x => x.Key == Weekly);
    }

    [Fact]
    public void リセット時刻がnullなら控えを外す()
    {
        var s = new ResetSchedule();
        s.Update(Session, Base);
        s.Update(Session, null);

        Assert.Equal(0, s.PendingCount);
        Assert.Empty(s.Collect(Base.AddHours(1)));
    }

    [Fact]
    public void 初回に未来の時刻を控えただけでは鳴らない()
    {
        // 起動直後、既に進行中の枠を初めて見た状況
        var s = new ResetSchedule();
        s.Update(Session, Base.AddHours(4));

        Assert.Empty(s.Collect(Base));
    }
}
