using System.Windows;
using ClaudeUsage.App.Models;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ClaudeUsage.App;

public partial class App : Application
{
    private static Mutex? _instanceMutex;

    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _window;
    private IntPtr _lastIconHandle;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

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

    private void SetupTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("表示/非表示", null, (_, _) => ToggleWindow());
        menu.Items.Add("今すぐ更新", null, (_, _) => _window?.RefreshNow());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var topmostItem = new Forms.ToolStripMenuItem("常に最前面") { CheckOnClick = true };
        topmostItem.Checked = _window?.IsTopmostEnabled ?? true;
        topmostItem.CheckedChanged += (_, _) => _window?.SetTopmost(topmostItem.Checked);
        menu.Items.Add(topmostItem);

        var pinItem = new Forms.ToolStripMenuItem("壁紙に固定 (WorkerW)") { CheckOnClick = true };
        pinItem.Checked = _window?.IsDesktopPinEnabled ?? false;
        pinItem.CheckedChanged += (_, _) => _window?.SetDesktopPin(pinItem.Checked);
        menu.Items.Add(pinItem);

        // 表示モード(タブ/縦並び/横並び)。ラジオ的に1つだけチェック
        var displayMenu = new Forms.ToolStripMenuItem("表示モード");
        (string Label, DisplayMode Mode)[] modes =
        [
            ("タブ", DisplayMode.Tabs),
            ("縦並び", DisplayMode.Vertical),
            ("横並び", DisplayMode.Horizontal),
        ];
        foreach (var (label, mode) in modes)
        {
            var item = new Forms.ToolStripMenuItem(label)
            {
                Checked = (_window?.CurrentDisplayMode ?? DisplayMode.Tabs) == mode,
                Tag = mode,
            };
            item.Click += (_, _) =>
            {
                _window?.SetDisplayMode(mode);
                foreach (Forms.ToolStripMenuItem mi in displayMenu.DropDownItems)
                    mi.Checked = Equals(mi.Tag, mode);
            };
            displayMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(displayMenu);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApp());

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "YMB Claude使用量モニター",
            ContextMenuStrip = menu,
            Visible = true,
        };
        UpdateTrayIcon(0);
        _trayIcon.DoubleClick += (_, _) => ToggleWindow();
    }

    /// <summary>使用率に応じた色の円をトレイアイコンとして描画する。</summary>
    private void UpdateTrayIcon(double percent)
    {
        var color = percent switch
        {
            >= 80 => Drawing.Color.FromArgb(239, 83, 80),   // 赤
            >= 50 => Drawing.Color.FromArgb(255, 179, 0),   // 黄
            _ => Drawing.Color.FromArgb(102, 187, 106),     // 緑
        };

        using var bmp = new Drawing.Bitmap(16, 16);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var back = new Drawing.SolidBrush(Drawing.Color.FromArgb(58, 58, 74));
            g.FillEllipse(back, 1, 1, 14, 14);
            // 使用率ぶんだけ扇形に塗る(12時起点・時計回り)
            using var fore = new Drawing.SolidBrush(color);
            var sweep = (float)(Math.Clamp(percent, 0, 100) * 3.6);
            if (sweep >= 359.9f)
                g.FillEllipse(fore, 1, 1, 14, 14);
            else if (sweep > 0)
                g.FillPie(fore, 1, 1, 14, 14, -90, sweep);
            using var pen = new Drawing.Pen(color, 1.5f);
            g.DrawEllipse(pen, 1, 1, 13, 13);
        }

        var handle = bmp.GetHicon();
        var old = _trayIcon!.Icon;
        _trayIcon.Icon = Drawing.Icon.FromHandle(handle);
        _trayIcon.Text = $"Claude使用量: 週間 {percent:0}%";
        old?.Dispose();
        if (_lastIconHandle != IntPtr.Zero)
            DestroyIcon(_lastIconHandle);
        _lastIconHandle = handle;
    }

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
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _window?.SaveSettings();
        Shutdown();
    }
}
