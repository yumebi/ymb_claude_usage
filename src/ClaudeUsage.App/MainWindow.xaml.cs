using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeUsage.App.Models;
using ClaudeUsage.App.Native;
using ClaudeUsage.App.Services;
using ClaudeUsage.App.Services.Providers;

namespace ClaudeUsage.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };

    private readonly ClaudeProvider _claude;
    private readonly List<IUsageProvider> _providers = [];
    private readonly List<ProviderPanelVM> _panels = [];
    private bool _refreshing;

    /// <summary>週間使用率(トレイアイコン色用)。Claude の全モデル週間バケットの値を通知する。</summary>
    public event Action<double>? UtilizationChanged;

    public bool IsTopmostEnabled => _settings.AlwaysOnTop;
    public bool IsDesktopPinEnabled => _settings.DesktopPin;
    public DisplayMode CurrentDisplayMode => _settings.DisplayMode;

    public MainWindow()
    {
        InitializeComponent();

        // プロバイダー構築: Claude は常時、それ以外は providers.json から
        _claude = new ClaudeProvider(_http, () => _settings.RefreshMinutes);
        _providers.Add(_claude);
        _providers.AddRange(GenericRestProvider.LoadAll(_http));
        foreach (var p in _providers)
            _panels.Add(new ProviderPanelVM(p.Name));

        HeaderTitle.Text = _providers.Count == 1 ? "Claude Code 使用量" : "AI使用量モニター";
        VersionText.Text = AppVersionInfo.Display;
        BuildLayout();

        Opacity = Math.Clamp(_settings.Opacity, 0.3, 1.0);
        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - 320 - 20;
            Top = area.Top + 20;
        }
        Topmost = _settings.AlwaysOnTop && !_settings.DesktopPin;

        Loaded += (_, _) =>
        {
            if (_settings.DesktopPin)
                ApplyDesktopPin(true);
            RefreshNow();
        };
        _timer.Tick += (_, _) => _ = RefreshAsync(force: false);
        _timer.Start();
    }

    public void RefreshNow() => _ = RefreshAsync(force: true);

    private async Task RefreshAsync(bool force)
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            // 全プロバイダー並列取得。1つの失敗が他に波及しないよう個別にガード
            var tasks = _providers.Select(async p =>
            {
                try
                {
                    return await p.FetchAsync(force, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    return ProviderPanelData.FromError($"{p.Name}: {ex.Message}");
                }
            }).ToList();
            var results = await Task.WhenAll(tasks);

            for (var i = 0; i < _panels.Count; i++)
                ApplyData(_panels[i], results[i]);

            if (_claude.WeeklyPercent is { } weekly)
                UtilizationChanged?.Invoke(weekly);

            StatusText.Text = $"更新 {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ---- レイアウト構築 ----

    private void BuildLayout()
    {
        var panelTemplate = (DataTemplate)FindResource("ProviderPanelTemplate");

        // プロバイダー1つのときはタブ等を出さず従来と同じ見た目
        if (_panels.Count == 1)
        {
            PanelHost.Content = new ContentControl
            {
                ContentTemplate = panelTemplate,
                Content = _panels[0],
                Focusable = false,
            };
            return;
        }

        switch (_settings.DisplayMode)
        {
            case DisplayMode.Tabs:
                PanelHost.Content = new TabControl
                {
                    ItemsSource = _panels,
                    ItemTemplate = (DataTemplate)FindResource("TabHeaderTemplate"),
                    ContentTemplate = panelTemplate,
                    Style = (Style)FindResource("DarkTabControl"),
                    ItemContainerStyle = (Style)FindResource("DarkTabItem"),
                    SelectedIndex = 0,
                };
                break;

            case DisplayMode.Vertical:
            case DisplayMode.Horizontal:
                var items = new ItemsControl
                {
                    ItemsSource = _panels,
                    ItemTemplate = (DataTemplate)FindResource("ProviderCardTemplate"),
                    Focusable = false,
                };
                if (_settings.DisplayMode == DisplayMode.Horizontal)
                    items.ItemsPanel = (ItemsPanelTemplate)FindResource("HorizontalItemsPanel");
                PanelHost.Content = items;
                break;
        }
    }

    public void SetDisplayMode(DisplayMode mode)
    {
        _settings.DisplayMode = mode;
        BuildLayout();
        SaveSettings();
    }

    // ---- 表示更新 ----

    private void ApplyData(ProviderPanelVM vm, ProviderPanelData data)
    {
        const double barMaxWidth = 292;
        var buckets = new List<BucketRowVM>();
        foreach (var g in data.Gauges)
        {
            if (g.Percent is { } percent)
            {
                var pct = Math.Clamp(percent, 0, 100);
                var brush = new SolidColorBrush(pct switch
                {
                    >= 80 => Color.FromRgb(0xEF, 0x53, 0x50),
                    >= 50 => Color.FromRgb(0xFF, 0xB3, 0x00),
                    _ => Color.FromRgb(0x66, 0xBB, 0x6A),
                });
                buckets.Add(new BucketRowVM(g.Label, g.ValueText, brush, brush,
                    barMaxWidth * pct / 100, Visibility.Visible,
                    FormatReset(g.ResetsAt),
                    g.ResetsAt is null ? Visibility.Collapsed : Visibility.Visible));
            }
            else
            {
                // %化できない値はテキストのみ(バー・リセット行なし)
                buckets.Add(new BucketRowVM(g.Label, g.ValueText,
                    (Brush)FindResource("Fg"), Brushes.Transparent,
                    0, Visibility.Collapsed, "", Visibility.Collapsed));
            }
        }

        vm.Buckets = buckets;
        vm.TableTitle = data.TableTitle;
        vm.Header1 = data.Header1;
        vm.Header2 = data.Header2;
        vm.Header3 = data.Header3;
        vm.TableRows = data.TableRows;
        vm.EmptyText = data.EmptyText;
        vm.Error = data.Error;
    }

    private static string FormatReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
            return "";
        var local = resetsAt.Value.ToLocalTime();
        var remain = local - DateTimeOffset.Now;
        if (remain < TimeSpan.Zero)
            remain = TimeSpan.Zero;
        var remainText = remain.TotalDays >= 1
            ? $"あと{(int)remain.TotalDays}日{remain.Hours}時間"
            : remain.TotalHours >= 1
                ? $"あと{(int)remain.TotalHours}時間{remain.Minutes}分"
                : $"あと{remain.Minutes}分";
        return $"{local:M/d(ddd) HH:mm} リセット({remainText})";
    }

    // ---- ウィンドウ操作 ----

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* ピン留め中は失敗することがある */ }
            SaveSettings();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshNow();

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    public void SetTopmost(bool enabled)
    {
        _settings.AlwaysOnTop = enabled;
        if (!_settings.DesktopPin)
            Topmost = enabled;
        SaveSettings();
    }

    public void SetDesktopPin(bool enabled)
    {
        _settings.DesktopPin = enabled;
        ApplyDesktopPin(enabled);
        SaveSettings();
    }

    private void ApplyDesktopPin(bool enabled)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (enabled)
        {
            Topmost = false;
            if (!DesktopPin.Pin(handle))
            {
                // WorkerW検出失敗 → 通常表示にフォールバック
                _settings.DesktopPin = false;
                Topmost = _settings.AlwaysOnTop;
            }
        }
        else
        {
            DesktopPin.Unpin(handle);
            Topmost = _settings.AlwaysOnTop;
        }
    }

    public void SaveSettings()
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        SaveSettings();
        base.OnClosed(e);
    }
}

// ---- パネル用ViewModel ----

/// <summary>ゲージ1行の表示用データ。</summary>
public sealed record BucketRowVM(string Label, string ValueText, Brush ValueBrush, Brush BarBrush,
    double BarWidth, Visibility BarVisibility, string ResetText, Visibility ResetVisibility);

/// <summary>
/// 1プロバイダー分のパネル状態。タブ切替時も選択状態を保てるよう
/// インスタンスを維持したままプロパティ更新で反映する。
/// </summary>
public sealed class ProviderPanelVM : INotifyPropertyChanged
{
    public string Name { get; }

    public ProviderPanelVM(string name) => Name = name;

    private IReadOnlyList<BucketRowVM> _buckets = [];
    public IReadOnlyList<BucketRowVM> Buckets
    {
        get => _buckets;
        set => Set(ref _buckets, value);
    }

    private IReadOnlyList<TableRow> _tableRows = [];
    public IReadOnlyList<TableRow> TableRows
    {
        get => _tableRows;
        set { Set(ref _tableRows, value); Raise(nameof(TableVisibility)); Raise(nameof(EmptyVisibility)); }
    }

    private string? _tableTitle;
    public string? TableTitle
    {
        get => _tableTitle;
        set { Set(ref _tableTitle, value); Raise(nameof(TableVisibility)); Raise(nameof(TableTitleVisibility)); }
    }

    private string _header1 = "";
    public string Header1 { get => _header1; set { Set(ref _header1, value); Raise(nameof(TableHeaderVisibility)); } }

    private string _header2 = "";
    public string Header2 { get => _header2; set => Set(ref _header2, value); }

    private string _header3 = "";
    public string Header3 { get => _header3; set => Set(ref _header3, value); }

    private string? _emptyText;
    public string? EmptyText
    {
        get => _emptyText;
        set { Set(ref _emptyText, value); Raise(nameof(EmptyVisibility)); }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { Set(ref _error, value); Raise(nameof(ErrorVisibility)); }
    }

    public Visibility ErrorVisibility =>
        string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TableVisibility =>
        TableTitle is not null || TableRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TableTitleVisibility =>
        string.IsNullOrEmpty(TableTitle) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TableHeaderVisibility =>
        string.IsNullOrEmpty(Header1) && string.IsNullOrEmpty(Header2) && string.IsNullOrEmpty(Header3)
            ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EmptyVisibility =>
        TableRows.Count == 0 && !string.IsNullOrEmpty(EmptyText) ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        Raise(name);
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
