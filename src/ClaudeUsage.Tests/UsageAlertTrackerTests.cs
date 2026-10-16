using ClaudeUsage.App.Services;

namespace ClaudeUsage.Tests;

public class UsageAlertTrackerTests
{
    private static UsageAlertTracker Default() => new([80, 95, 100]);

    [Fact]
    public void BelowFirstThreshold_NoAlert()
    {
        var t = Default();
        Assert.Null(t.Evaluate("seven_day", 0));
        Assert.Null(t.Evaluate("seven_day", 79.9));
    }

    [Fact]
    public void CrossingAThreshold_AlertsOnce()
    {
        var t = Default();
        Assert.Equal(80, t.Evaluate("seven_day", 82));
        Assert.Null(t.Evaluate("seven_day", 83));   // 同じ段階では再通知しない
        Assert.Null(t.Evaluate("seven_day", 94.9));
    }

    [Fact]
    public void RisingToNextThreshold_AlertsAgain()
    {
        var t = Default();
        Assert.Equal(80, t.Evaluate("seven_day", 81));
        Assert.Equal(95, t.Evaluate("seven_day", 96));
        Assert.Equal(100, t.Evaluate("seven_day", 100));
        Assert.Null(t.Evaluate("seven_day", 100));
    }

    [Fact]
    public void JumpingOverSeveralThresholds_ReportsTheHighestOnly()
    {
        var t = Default();
        Assert.Equal(95, t.Evaluate("seven_day", 97));
        Assert.Null(t.Evaluate("seven_day", 97));
    }

    [Fact]
    public void AfterReset_AlertsAgainOnNextRise()
    {
        var t = Default();
        Assert.Equal(95, t.Evaluate("five_hour", 96));
        Assert.Null(t.Evaluate("five_hour", 5));      // リセットされて下がった(再通知できる状態に戻る)
        Assert.Equal(80, t.Evaluate("five_hour", 85));
    }

    [Fact]
    public void SmallDipBelowThreshold_DoesNotRearm()
    {
        // 80% を少し下回っただけでは「リセット」とみなさない(境界で通知が連発しないように)
        var t = Default();
        Assert.Equal(80, t.Evaluate("seven_day", 81));
        Assert.Null(t.Evaluate("seven_day", 78));
        Assert.Null(t.Evaluate("seven_day", 81));
    }

    [Fact]
    public void KeysAreIndependent()
    {
        var t = Default();
        Assert.Equal(80, t.Evaluate("five_hour", 85));
        Assert.Equal(80, t.Evaluate("seven_day", 85));
    }

    [Fact]
    public void CustomThresholds_AreSortedAndSanitized()
    {
        var t = new UsageAlertTracker([90, 50, 90, 0, 150, -3]);
        Assert.Equal([50, 90], t.Thresholds);
        Assert.Equal(50, t.Evaluate("k", 60));
    }

    [Fact]
    public void NoThresholds_NeverAlerts()
    {
        var t = new UsageAlertTracker([]);
        Assert.Null(t.Evaluate("k", 100));
        Assert.Null(new UsageAlertTracker(null).Evaluate("k", 100));
    }

    [Fact]
    public void NaN_IsIgnored()
    {
        Assert.Null(Default().Evaluate("k", double.NaN));
    }
}

public class ResetTextFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NullReset_IsEmpty()
    {
        Assert.Equal("", ResetTextFormatter.Format(null, Now));
    }

    [Fact]
    public void MoreThanADay_ShowsDaysAndHours()
    {
        var text = ResetTextFormatter.Format(Now.AddDays(2).AddHours(3).AddMinutes(10), Now);
        Assert.Contains("あと2日3時間", text);
        Assert.Contains("リセット", text);
    }

    [Fact]
    public void MoreThanAnHour_ShowsHoursAndMinutes()
    {
        Assert.Contains("あと2時間30分", ResetTextFormatter.Format(Now.AddHours(2).AddMinutes(30), Now));
    }

    [Fact]
    public void UnderAnHour_ShowsMinutes()
    {
        Assert.Contains("あと5分", ResetTextFormatter.Format(Now.AddMinutes(5).AddSeconds(30), Now));
    }

    [Fact]
    public void AlreadyPassed_ClampsToZero()
    {
        Assert.Contains("あと0分", ResetTextFormatter.Format(Now.AddMinutes(-10), Now));
    }

    [Fact]
    public void RemainingTime_ChangesAsNowAdvances()
    {
        // 画面を定期的に作り直すと、残り時間が減っていく(以前は5分ごとの更新まで固定だった)
        var reset = Now.AddHours(1).AddMinutes(30);
        var before = ResetTextFormatter.Format(reset, Now);
        var after = ResetTextFormatter.Format(reset, Now.AddMinutes(20));
        Assert.Contains("あと1時間30分", before);
        Assert.Contains("あと1時間10分", after);
    }
}
