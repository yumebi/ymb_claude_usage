using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClaudeUsage.App.Models;
using ClaudeUsage.App.Native;

namespace ClaudeUsage.App;

public partial class App : Application
{
    // トレイアイコンID・メニューコマンドIDはこのアプリ内だけで完結するので固定値でよい。
    private const int TrayIconId = 1;
    private const int CmdToggle = 1001;
    private const int CmdRefresh = 1002;
    private const int CmdTopmost = 1003;
    private const int CmdDesktopPin = 1004;
    private const int CmdModeTabs = 1010;
    private const int CmdModeVertical = 1011;
    private const int CmdModeHorizontal = 1012;
    private const int CmdExit = 1099;

    private static readonly int WM_TRAYICON = TrayInterop.WM_APP + 1;

    private static Mutex? _instanceMutex;

    private MainWindow? _window;
    private HwndSource? _hwndSource;
    private uint _taskbarCreatedMsg;
    private bool _iconAdded;
    private IntPtr _currentHIcon;
    private string _tooltipText = "YMB Claude使用量モニター";
    private double _lastPercent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "YmbClaudeUsage_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _window = new MainWindow();
        SetupTrayIcon();
        _window.UtilizationChanged += percent => UpdateTrayIcon(percent);
        _window.Show();
    }

    /// <summary>
    /// トレイメッセージ受信専用の非表示ウィンドウ(HwndSource, HWND_MESSAGE)を作り、
    /// Shell_NotifyIconでアイコンを登録する。
    /// </summary>
    private void SetupTrayIcon()
    {
        var parameters = new HwndSourceParameters("YmbClaudeUsage_TrayMessageWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = TrayInterop.HWND_MESSAGE,
            Width = 0,
            Height = 0,
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        // エクスプローラー再起動時にタスクトレイが再構築されたら、アイコンを再登録する
        _taskbarCreatedMsg = TrayInterop.RegisterWindowMessage("TaskbarCreated");

        AddTrayIcon();
        UpdateTrayIcon(0);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseMsg = unchecked((int)lParam.ToInt64());
            switch (mouseMsg)
            {
                case TrayInterop.WM_RBUTTONUP:
                    ShowContextMenu();
                    handled = true;
                    break;
                case TrayInterop.WM_LBUTTONDBLCLK:
                    ToggleWindow();
                    handled = true;
                    break;
            }
        }
        else if (msg == TrayInterop.WM_COMMAND)
        {
            var id = wParam.ToInt32() & 0xFFFF;
            HandleMenuCommand(id);
            handled = true;
        }
        else if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            AddTrayIcon();
            UpdateTrayIcon(_lastPercent);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void HandleMenuCommand(int id)
    {
        switch (id)
        {
            case CmdToggle:
                ToggleWindow();
                break;
            case CmdRefresh:
                _window?.RefreshNow();
                break;
            case CmdTopmost:
                _window?.SetTopmost(!(_window?.IsTopmostEnabled ?? true));
                break;
            case CmdDesktopPin:
                _window?.SetDesktopPin(!(_window?.IsDesktopPinEnabled ?? false));
                break;
            case CmdModeTabs:
                _window?.SetDisplayMode(DisplayMode.Tabs);
                break;
            case CmdModeVertical:
                _window?.SetDisplayMode(DisplayMode.Vertical);
                break;
            case CmdModeHorizontal:
                _window?.SetDisplayMode(DisplayMode.Horizontal);
                break;
            case CmdExit:
                ExitApp();
                break;
        }
    }

    /// <summary>
    /// ネイティブHMENUを毎回組み立てて右クリックメニューを表示する。
    /// チェック状態は表示都度 _window の現在値から反映する。
    /// </summary>
    private void ShowContextMenu()
    {
        if (_hwndSource is null)
            return;

        var hMenu = TrayInterop.CreatePopupMenu();
        var hSubMenu = TrayInterop.CreatePopupMenu();
        try
        {
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_STRING, (UIntPtr)CmdToggle, "表示/非表示");
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_STRING, (UIntPtr)CmdRefresh, "今すぐ更新");
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_SEPARATOR, UIntPtr.Zero, null);

            var topmostFlags = TrayInterop.MF_STRING | (_window?.IsTopmostEnabled == true ? TrayInterop.MF_CHECKED : 0);
            TrayInterop.AppendMenu(hMenu, topmostFlags, (UIntPtr)CmdTopmost, "常に最前面");

            var pinFlags = TrayInterop.MF_STRING | (_window?.IsDesktopPinEnabled == true ? TrayInterop.MF_CHECKED : 0);
            TrayInterop.AppendMenu(hMenu, pinFlags, (UIntPtr)CmdDesktopPin, "壁紙に固定 (WorkerW)");

            var currentMode = _window?.CurrentDisplayMode ?? DisplayMode.Tabs;
            TrayInterop.AppendMenu(hSubMenu, TrayInterop.MF_STRING | (currentMode == DisplayMode.Tabs ? TrayInterop.MF_CHECKED : 0), (UIntPtr)CmdModeTabs, "タブ");
            TrayInterop.AppendMenu(hSubMenu, TrayInterop.MF_STRING | (currentMode == DisplayMode.Vertical ? TrayInterop.MF_CHECKED : 0), (UIntPtr)CmdModeVertical, "縦並び");
            TrayInterop.AppendMenu(hSubMenu, TrayInterop.MF_STRING | (currentMode == DisplayMode.Horizontal ? TrayInterop.MF_CHECKED : 0), (UIntPtr)CmdModeHorizontal, "横並び");
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_POPUP, (UIntPtr)(ulong)hSubMenu.ToInt64(), "表示モード");

            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_SEPARATOR, UIntPtr.Zero, null);
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_STRING, (UIntPtr)CmdExit, "終了");

            TrayInterop.GetCursorPos(out var pt);

            // ポップアップメニューを閉じる際に正しくフォーカスが戻るようにする定番の手順
            TrayInterop.SetForegroundWindow(_hwndSource.Handle);
            TrayInterop.TrackPopupMenuEx(
                hMenu,
                TrayInterop.TPM_RIGHTBUTTON | TrayInterop.TPM_LEFTALIGN,
                pt.X, pt.Y,
                _hwndSource.Handle,
                IntPtr.Zero);
            TrayInterop.PostMessage(_hwndSource.Handle, TrayInterop.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            // サブメニューを先に破棄してからメインメニューを破棄する
            TrayInterop.DestroyMenu(hSubMenu);
            TrayInterop.DestroyMenu(hMenu);
        }
    }

    private void AddTrayIcon()
    {
        if (_hwndSource is null)
            return;

        var data = CreateNotifyIconData();
        data.uFlags = TrayInterop.NIF_MESSAGE | TrayInterop.NIF_TIP | (_currentHIcon != IntPtr.Zero ? TrayInterop.NIF_ICON : 0);
        _iconAdded = TrayInterop.Shell_NotifyIcon(TrayInterop.NIM_ADD, ref data);
    }

    /// <summary>使用率に応じた色の円をトレイアイコンとして描画する。</summary>
    private void UpdateTrayIcon(double percent)
    {
        _lastPercent = percent;

        var color = percent switch
        {
            >= 80 => Color.FromRgb(239, 83, 80),   // 赤
            >= 50 => Color.FromRgb(255, 179, 0),   // 黄
            _ => Color.FromRgb(102, 187, 106),     // 緑
        };

        var newIcon = TrayIconRenderer.CreateHIcon(color, percent);
        var oldIcon = _currentHIcon;
        _currentHIcon = newIcon;
        _tooltipText = $"Claude使用量: 週間 {percent:0}%";

        if (_hwndSource is not null)
        {
            var data = CreateNotifyIconData();
            data.uFlags = TrayInterop.NIF_ICON | TrayInterop.NIF_TIP;
            TrayInterop.Shell_NotifyIcon(TrayInterop.NIM_MODIFY, ref data);
        }

        if (oldIcon != IntPtr.Zero)
            TrayInterop.DestroyIcon(oldIcon);
    }

    private TrayInterop.NOTIFYICONDATA CreateNotifyIconData() => new()
    {
        cbSize = Marshal.SizeOf<TrayInterop.NOTIFYICONDATA>(),
        hWnd = _hwndSource!.Handle,
        uID = TrayIconId,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _currentHIcon,
        szTip = _tooltipText,
    };

    private void ToggleWindow()
    {
        if (_window is null)
            return;
        if (_window.IsVisible)
            _window.Hide();
        else
        {
            _window.Show();
            _window.Activate();
        }
    }

    private void ExitApp()
    {
        RemoveTrayIcon();
        _window?.SaveSettings();
        Shutdown();
    }

    private void RemoveTrayIcon()
    {
        if (_iconAdded && _hwndSource is not null)
        {
            var data = CreateNotifyIconData();
            TrayInterop.Shell_NotifyIcon(TrayInterop.NIM_DELETE, ref data);
            _iconAdded = false;
        }

        if (_currentHIcon != IntPtr.Zero)
        {
            TrayInterop.DestroyIcon(_currentHIcon);
            _currentHIcon = IntPtr.Zero;
        }

        _hwndSource?.Dispose();
        _hwndSource = null;
    }
}
