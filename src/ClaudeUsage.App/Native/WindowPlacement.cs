using System.Runtime.InteropServices;

namespace ClaudeUsage.App.Native;

/// <summary>ピクセル単位の矩形(物理ピクセル。DPIによる換算を含まない)。</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>2つの矩形が重なる部分の面積(重ならなければ0)。</summary>
    public static long OverlapArea(PxRect a, PxRect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? (long)w * h : 0;
    }
}

/// <summary>
/// 保存しておいたウインドウ位置を、現在のモニタ構成に合わせて補正する純粋関数。
/// 保存時とモニタ構成が変わっている(取り外し、解像度やDPIの変更)と、そのまま復元した
/// ウインドウが画面外に出て操作不能になるため、必ず現在の作業領域に収まる位置へ直す。
/// </summary>
public static class WindowPlacementMath
{
    /// <summary>タイトルバー付近でつかめるとみなす、画面内に見えている最小の領域。</summary>
    public const int GrabWidth = 160;
    public const int GrabHeight = 40;

    /// <param name="desired">復元したい矩形(物理ピクセル、スクリーン座標)。</param>
    /// <param name="workAreas">各モニタの作業領域(タスクバーを除く)。</param>
    public static PxRect Fit(PxRect desired, IReadOnlyList<PxRect> workAreas, int minWidth, int minHeight)
    {
        if (workAreas.Count == 0) return desired;

        // 全モニタを含む外接矩形。複数モニタにまたがる配置はこの範囲までを許す。
        var left = workAreas.Min(a => a.X);
        var top = workAreas.Min(a => a.Y);
        var right = workAreas.Max(a => a.Right);
        var bottom = workAreas.Max(a => a.Bottom);
        var maxWidth = right - left;
        var maxHeight = bottom - top;

        var width = Math.Clamp(desired.Width, Math.Min(minWidth, maxWidth), maxWidth);
        var height = Math.Clamp(desired.Height, Math.Min(minHeight, maxHeight), maxHeight);
        var rect = new PxRect(desired.X, desired.Y, width, height);

        // タイトルバー付近が十分に見えているなら、モニタにまたがる配置も含めてそのまま使う。
        var strip = new PxRect(rect.X, rect.Y, rect.Width, Math.Min(GrabHeight, rect.Height));
        var need = (long)Math.Min(GrabWidth, rect.Width) * Math.Min(GrabHeight, rect.Height);
        if (workAreas.Any(a => PxRect.OverlapArea(strip, a) >= need)) return rect;

        // つかめない場合は、最も重なりが大きい(無ければ最も近い)モニタの中へ移す。
        var best = workAreas
            .OrderByDescending(a => PxRect.OverlapArea(rect, a))
            .ThenBy(a => DistanceSquared(Center(rect), Center(a)))
            .First();

        var w = Math.Min(rect.Width, best.Width);
        var h = Math.Min(rect.Height, best.Height);
        var x = Math.Clamp(rect.X, best.X, best.Right - w);
        var y = Math.Clamp(rect.Y, best.Y, best.Bottom - h);
        return new PxRect(x, y, w, h);
    }

    private static (double X, double Y) Center(PxRect r) => (r.X + r.Width / 2.0, r.Y + r.Height / 2.0);

    private static double DistanceSquared((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}

/// <summary>各モニタの作業領域(タスクバーを除く領域)を、物理ピクセルで取得する。</summary>
public static class MonitorWorkAreas
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const int MONITORINFOF_PRIMARY = 1;

    /// <summary>全モニタの作業領域。先頭がプライマリ。取得できなければ空。</summary>
    public static List<PxRect> Enumerate()
    {
        var list = new List<PxRect>();
        PxRect? primary = null;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var r = new PxRect(
                    info.rcWork.Left, info.rcWork.Top,
                    info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
                if ((info.dwFlags & MONITORINFOF_PRIMARY) != 0) primary = r;
                else list.Add(r);
            }
            return true;
        }, IntPtr.Zero);

        if (primary is { } p) list.Insert(0, p);
        return list;
    }
}
