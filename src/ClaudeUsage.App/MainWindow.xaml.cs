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
    private readonly ResetBellService _bells;
    private readonly UsageHistoryStore _history = new();
    private bool _refreshing;

    /// <summary>週間使用率(トレイアイコン色用)。Claude の全モデル週間バケットの値を通知する。</summary>
    public event Action<double>? UtilizationChanged;

    public bool IsTopmostEnabled => _settings.AlwaysOnTop;
    public bool IsDesktopPinEnabled => _settings.DesktopPin;
    public bool IsResetBellEnabled => _settings.ResetBell;
    public DisplayMode CurrentDisplayMode => _settings.DisplayMode;

    public MainWindow()
    {
        InitializeComponent();

        // プロバイダー構築: Claude は常時、Grok は ~/.grok/logs/unified.jsonl がある時だけ、
        // それ以外は providers.json から
        _history.Load();
        _claude = new ClaudeProvider(_http, () => _settings.RefreshMinutes, _history);
        _providers.Add(_claude);
        if (GrokLocalScanner.IsAvailable)
            _providers.Add(new GrokProvider(() => _settings.GrokWeeklyResetAnchor));
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
        // リセット時刻に鳴らす。鳴った直後は新しいリセット時刻を取りに行かせる
        _bells = new ResetBellService(() => _settings.ResetBell, RefreshNow);

        _timer.Tick += (_, _) => _ = RefreshAsync(force: false);
        _timer.Start();
    }

    /// <summary>リセット音の有無を切り替える。トレイメニューから呼ばれる。</summary>
    public void ToggleResetBell()
    {
        _settings.ResetBell = !_settings.ResetBell;
        _settings.Save();
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
            {
                UtilizationChanged?.Invoke(weekly);
                _history.Record(weekly);
            }

            // 次回リセット時刻を控え直す
            _bells.UpdateSchedule(_claude.Buckets);

            StatusText.Text = $"更新 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            // ここに来るのは各プロバイダーの個別ガードを抜けた異常(ApplyData中の例外等)。
            // fire-and-forget 呼び出し(`_ = RefreshAsync(...)`)なので握り潰すと
            // 「読込中...」のまま無言で固まって見えるため、必ず可視化する。
            StatusText.Text = $"更新失敗 {DateTime.Now:HH:mm:ss} ({ex.Message})";
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
                // リソースキー欠落でリフレッシュ全体を落とさないよう TryFindResource + フォールバックにする
                var fg = (Brush?)TryFindResource("YmbFgBrush") ?? Brushes.White;
                buckets.Add(new BucketRowVM(g.Label, g.ValueText,
                    fg, Brushes.Transparent,
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
        vm.Trend = data.Trend;
        vm.SecondaryTitle = data.SecondaryTitle;
        vm.SecondaryRows = data.SecondaryRows;
        vm.SecondaryEmptyText = data.SecondaryEmptyText;
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

    private IReadOnlyList<TableRow> _secondaryRows = [];
    public IReadOnlyList<TableRow> SecondaryRows
    {
        get => _secondaryRows;
        set { Set(ref _secondaryRows, value); Raise(nameof(SecondaryVisibility)); Raise(nameof(SecondaryEmptyVisibility)); }
    }

    private string? _secondaryTitle;
    public string? SecondaryTitle
    {
        get => _secondaryTitle;
        set { Set(ref _secondaryTitle, value); Raise(nameof(SecondaryVisibility)); Raise(nameof(SecondaryTitleVisibility)); }
    }

    private string? _secondaryEmptyText;
    public string? SecondaryEmptyText
    {
        get => _secondaryEmptyText;
        set { Set(ref _secondaryEmptyText, value); Raise(nameof(SecondaryEmptyVisibility)); }
    }

    private IReadOnlyList<double>? _trend;
    public IReadOnlyList<double>? Trend
    {
        get => _trend;
        set
        {
            Set(ref _trend, value);
            Raise(nameof(TrendVisibility));
            BuildTrend();
        }
    }

    /// <summary>推移グラフのタイトル(直近のポイント数付き)。</summary>
    public string? TrendTitle => Trend is { Count: > 1 } points
        ? $"週間使用率の推移 (直近{points.Count}回の更新)"
        : null;

    /// <summary>推移グラフの折れ線データ。プロット内座標(0..292 × 0..26)。</summary>
    public PointCollection? TrendPoints { get; private set; }

    /// <summary>推移グラフの折れ線色(現在値に応じた警告色)。</summary>
    public Brush? TrendBrush { get; private set; }

    public Visibility TrendVisibility => Trend is { Count: > 1 } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>プロット領域の幅。折れ線のx座標を決めるのに使う。</summary>
    public double TrendWidth => 292;

    public Visibility SecondaryVisibility =>
        SecondaryTitle is not null || SecondaryRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SecondaryTitleVisibility =>
        string.IsNullOrEmpty(SecondaryTitle) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SecondaryEmptyVisibility =>
        SecondaryRows.Count == 0 && !string.IsNullOrEmpty(SecondaryEmptyText) ? Visibility.Visible : Visibility.Collapsed;

    private string? _error;
    public string? Error
    {
        get => _error;
        set { Set(ref _error, value); Raise(nameof(ErrorVisibility)); Raise(nameof(ErrorBrush)); Raise(nameof(ErrorFontWeight)); }
    }

    public Visibility ErrorVisibility =>
        string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>再ログインが必要なエラーや残高切れは通常の警告色より目立つ色にする。</summary>
    public Brush ErrorBrush => IsHighEmphasisError
        ? new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50))
        : new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));

    public FontWeight ErrorFontWeight => IsHighEmphasisError ? FontWeights.Bold : FontWeights.Normal;

    private bool IsHighEmphasisError => !string.IsNullOrEmpty(Error) &&
        (Error.Contains("再ログイン", StringComparison.Ordinal) || Error.Contains("残高切れ", StringComparison.Ordinal));

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

    /// <summary>
    /// 履歴の使用率(%)を 292×26 のプロット領域に投影して折れ線ポイントを作る。
    /// 0〜100% を縦方向の全幅に割り当て、左右に数pxの余白を取る。
    /// </summary>
    private void BuildTrend()
    {
        var points = Trend;
        TrendPoints = null;
        TrendBrush = null;
        Raise(nameof(TrendTitle));

        if (points is null || points.Count < 2)
            return;

        const double plotWidth = 292;
        const double plotHeight = 26;
        const double pad = 2.5;

        var last = points[^1];
        var brush = new SolidColorBrush(last switch
        {
            >= 80 => Color.FromRgb(0xEF, 0x53, 0x50),
            >= 50 => Color.FromRgb(0xFF, 0xB3, 0x00),
            _ => Color.FromRgb(0x66, 0xBB, 0x6A),
        });
        TrendBrush = brush;

        var col = new PointCollection(points.Count);
        var xStep = (plotWidth - pad * 2) / (points.Count - 1);
        for (var i = 0; i < points.Count; i++)
        {
            var pct = Math.Clamp(points[i], 0, 100);
            var x = pad + i * xStep;
            var y = plotHeight - pad - (plotHeight - pad * 2) * pct / 100.0;
            col.Add(new Point(x, y));
        }
        TrendPoints = col;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        Raise(name);
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
