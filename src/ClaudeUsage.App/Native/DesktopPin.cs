using System.Runtime.InteropServices;

namespace ClaudeUsage.App.Native;

/// <summary>
/// デスクトップアイコン層にウィンドウを固定する(Rainmeter等と同じWorkerWトリック)。
/// Progmanへ0x052Cを送るとWorkerWが生成されるので、そこに自ウィンドウをSetParentする。
/// </summary>
public static class DesktopPin
{
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// 指定ウィンドウをデスクトップ壁紙の直上(WorkerW)に貼り付ける。
    /// WorkerWが見つからない場合は何もしない(Progmanへの貼り付けは壁紙より下になり
    /// ウィンドウが完全に見えなくなるため、フォールバックとして行わない)。
    /// </summary>
    /// <returns>貼り付けに成功したらtrue。</returns>
    public static bool Pin(IntPtr windowHandle)
    {
        var progman = FindWindow("Progman", null);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        IntPtr workerW = IntPtr.Zero;
        for (var attempt = 0; attempt < 10 && workerW == IntPtr.Zero; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(50);

            EnumWindows((hWnd, _) =>
            {
                var shellView = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                    workerW = FindWindowEx(IntPtr.Zero, hWnd, "WorkerW", null);
                return true;
            }, IntPtr.Zero);
        }

        if (workerW == IntPtr.Zero)
            return false;

        SetParent(windowHandle, workerW);
        return true;
    }

    /// <summary>通常ウィンドウ(トップレベル)へ戻す。</summary>
    public static void Unpin(IntPtr windowHandle) => SetParent(windowHandle, IntPtr.Zero);
}
