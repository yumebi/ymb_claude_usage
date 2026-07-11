using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeUsage.App.Native;

/// <summary>
/// 使用率に応じた色の円(扇形の進捗表示)を、System.Drawing を使わず
/// WPF(DrawingVisual + RenderTargetBitmap)だけで描画し、HICONへ変換する。
/// </summary>
internal static class TrayIconRenderer
{
    private const int Size = 16;

    /// <summary>使用率(0-100)と色から16x16のHICONを生成する。呼び出し側はDestroyIconで破棄すること。</summary>
    public static IntPtr CreateHIcon(Color color, double percent)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var center = new Point(Size / 2.0, Size / 2.0);

            var back = new SolidColorBrush(Color.FromArgb(255, 58, 58, 74));
            back.Freeze();
            dc.DrawEllipse(back, null, center, 7, 7);

            var sweep = Math.Clamp(percent, 0, 100) * 3.6;
            if (sweep > 0)
            {
                var fore = new SolidColorBrush(color);
                fore.Freeze();
                if (sweep >= 359.9)
                {
                    dc.DrawEllipse(fore, null, center, 7, 7);
                }
                else
                {
                    var geometry = BuildPieGeometry(center, 7, sweep);
                    dc.DrawGeometry(fore, null, geometry);
                }
            }

            var pen = new Pen(new SolidColorBrush(color), 1.5);
            pen.Freeze();
            dc.DrawEllipse(null, pen, center, 6.5, 6.5);
        }

        var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var stride = Size * 4;
        var pixels = new byte[Size * stride];
        rtb.CopyPixels(pixels, stride, 0);

        return CreateHIconFromBgraPixels(pixels, Size, Size);
    }

    /// <summary>12時起点(真上)から時計回りに sweepDegrees ぶんの扇形ジオメトリを作る。</summary>
    private static PathGeometry BuildPieGeometry(Point center, double radius, double sweepDegrees)
    {
        const double startAngle = -90;
        var endAngle = startAngle + sweepDegrees;

        Point PointOnCircle(double angleDeg)
        {
            var rad = angleDeg * Math.PI / 180.0;
            return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
        }

        var arcStart = PointOnCircle(startAngle);
        var arcEnd = PointOnCircle(endAngle);
        var isLargeArc = sweepDegrees > 180;

        var figure = new PathFigure { StartPoint = center, IsClosed = true };
        figure.Segments.Add(new LineSegment(arcStart, true));
        figure.Segments.Add(new ArcSegment(arcEnd, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// BGRA(事前乗算済みアルファ, Pbgra32相当)のピクセル配列から、
    /// GDIのDIBSection(32bpp)とマスクビットマップを自前構築してHICON化する。
    /// </summary>
    private static IntPtr CreateHIconFromBgraPixels(byte[] bgraPixels, int width, int height)
    {
        var bmi = new TrayInterop.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<TrayInterop.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // 負値でトップダウンDIBを指定
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var screenDc = TrayInterop.GetDC(IntPtr.Zero);
        IntPtr hBitmap;
        IntPtr ppvBits;
        try
        {
            hBitmap = TrayInterop.CreateDIBSection(screenDc, ref bmi, 0 /* DIB_RGB_COLORS */, out ppvBits, IntPtr.Zero, 0);
        }
        finally
        {
            TrayInterop.ReleaseDC(IntPtr.Zero, screenDc);
        }

        if (hBitmap == IntPtr.Zero || ppvBits == IntPtr.Zero)
            return IntPtr.Zero;

        Marshal.Copy(bgraPixels, 0, ppvBits, bgraPixels.Length);

        // ANDマスクは全0(色ビットマップのアルファチャンネルだけで透過を表現する)
        var maskStride = ((width + 15) / 16) * 2;
        var maskBits = new byte[maskStride * height];
        var maskHandle = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
        IntPtr hMask;
        try
        {
            hMask = TrayInterop.CreateBitmap(width, height, 1, 1, maskHandle.AddrOfPinnedObject());
        }
        finally
        {
            maskHandle.Free();
        }

        var iconInfo = new TrayInterop.ICONINFO
        {
            fIcon = true,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = hMask,
            hbmColor = hBitmap,
        };

        var hIcon = TrayInterop.CreateIconIndirect(ref iconInfo);

        if (hMask != IntPtr.Zero)
            TrayInterop.DeleteObject(hMask);
        if (hBitmap != IntPtr.Zero)
            TrayInterop.DeleteObject(hBitmap);

        return hIcon;
    }
}
