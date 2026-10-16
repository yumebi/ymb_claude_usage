using ClaudeUsage.App.Native;

namespace ClaudeUsage.Tests;

public class WindowPlacementMathTests
{
    private static readonly PxRect Main = new(0, 0, 1920, 1040);      // プライマリ(タスクバーを除く)
    private static readonly PxRect Right = new(1920, 0, 2560, 1400);   // 右隣の大きいモニタ
    private static readonly PxRect Left = new(-1280, 100, 1280, 900);  // 左隣(座標が負)

    private static PxRect Fit(PxRect desired, params PxRect[] areas) =>
        WindowPlacementMath.Fit(desired, areas, minWidth: 520, minHeight: 360);

    [Fact]
    public void OnScreen_IsKeptAsIs()
    {
        var desired = new PxRect(200, 100, 1200, 800);
        Assert.Equal(desired, Fit(desired, Main));
    }

    [Fact]
    public void NoWorkAreas_ReturnsDesired()
    {
        var desired = new PxRect(5000, 5000, 800, 600);
        Assert.Equal(desired, Fit(desired));
    }

    [Fact]
    public void MonitorRemoved_MovesWindowBackOntoRemainingScreen()
    {
        // 右のモニタに置いていたが、取り外されて今は1枚だけ。
        var fitted = Fit(new PxRect(2500, 200, 1200, 800), Main);

        Assert.True(fitted.X >= Main.X && fitted.Right <= Main.Right);
        Assert.True(fitted.Y >= Main.Y && fitted.Bottom <= Main.Bottom);
        Assert.Equal(1200, fitted.Width);
        Assert.Equal(800, fitted.Height);
    }

    [Fact]
    public void WindowLargerThanScreen_IsShrunkToFit()
    {
        var fitted = Fit(new PxRect(0, 0, 4000, 3000), Main);

        Assert.Equal(Main, fitted);
    }

    [Fact]
    public void TooSmall_IsEnlargedToMinimum()
    {
        var fitted = Fit(new PxRect(100, 100, 100, 100), Main);

        Assert.Equal(520, fitted.Width);
        Assert.Equal(360, fitted.Height);
    }

    [Fact]
    public void SpanningTwoMonitors_IsKept()
    {
        // 2枚のモニタにまたがって置いている。タイトルバーは見えているので動かさない。
        var desired = new PxRect(1400, 200, 1600, 900);
        Assert.Equal(desired, Fit(desired, Main, Right));
    }

    [Fact]
    public void MostlyOffLeft_ButTitleBarGrabbable_IsKept()
    {
        // 左端から大きくはみ出しているが、右側の200px分は見えている。
        var desired = new PxRect(-1000, 100, 1200, 800);
        Assert.Equal(desired, Fit(desired, Main));
    }

    [Fact]
    public void CompletelyOffLeft_IsPulledBack()
    {
        var fitted = Fit(new PxRect(-3000, 100, 1200, 800), Main);

        Assert.True(fitted.X >= Main.X);
        Assert.True(fitted.Right <= Main.Right);
    }

    [Fact]
    public void BelowTheBottomEdge_IsPulledBack()
    {
        var fitted = Fit(new PxRect(300, 2000, 1200, 800), Main);

        Assert.True(fitted.Bottom <= Main.Bottom);
        Assert.True(fitted.Y >= Main.Y);
    }

    [Fact]
    public void MonitorWithNegativeCoordinates_IsHandled()
    {
        var desired = new PxRect(-1100, 200, 900, 600);
        Assert.Equal(desired, Fit(desired, Left, Main));
    }

    [Fact]
    public void WhenSavedMonitorIsGone_PrefersTheNearestRemainingOne()
    {
        // 左のモニタ上にあったが、今は Main と Right だけ。Main の方が近い。
        var fitted = Fit(new PxRect(-1200, 200, 900, 600), Main, Right);

        Assert.True(fitted.X >= Main.X);
        Assert.True(fitted.Right <= Main.Right);
    }

    [Fact]
    public void OverlapArea_IsZeroWhenApart()
    {
        Assert.Equal(0, PxRect.OverlapArea(new PxRect(0, 0, 10, 10), new PxRect(20, 20, 5, 5)));
        Assert.Equal(25, PxRect.OverlapArea(new PxRect(0, 0, 10, 10), new PxRect(5, 5, 20, 20)));
    }
}


public class MonitorWorkAreasTests
{
    [Fact]
    public void Enumerate_ReturnsAtLeastOneArea_PrimaryFirst()
    {
        var areas = MonitorWorkAreas.Enumerate();

        Assert.NotEmpty(areas);
        Assert.All(areas, a => Assert.True(a.Width > 0 && a.Height > 0));
        // プライマリの作業領域は、(0,0)から始まるモニタ上にある。
        Assert.True(areas[0].X <= 0 && areas[0].Right > 0 && areas[0].Y <= 0 && areas[0].Bottom > 0);
    }
}
