using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Argus.Core.Clients;
using Argus.Core.Dashboard;
using Argus.Core.Settings;
using Argus.Ui;

namespace Argus.App;

public sealed record NavItem(string Icon, string Title, UIElement Content, bool IsChild = false)
{
    /// <summary>하위 항목은 들여쓰고 └ 로 부모에 이어 보이게, 조금 작게 보여준다.</summary>
    public Thickness Indent => IsChild ? new Thickness(14, 0, 0, 0) : new Thickness(0);
    public string Connector => IsChild ? "└" : "";
    public double IconWidth => IsChild ? 0 : 24;
    public double TitleSize => IsChild ? 13 : 14;
}

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var app = (App)Application.Current;

        // 대시보드: 각 모듈이 내놓는 정보(IDashboardContributor)를 모아서 보여준다.
        var dashboard = new DashboardView(app.Registry, app.Host.Modules.OfType<IDashboardContributor>().Append(app.Host));
        var items = new List<NavItem> { new("", "대시보드", dashboard) };
        var views = app.Host.Modules.Select(m => (Module: m, View: app.Host.TryCreate(m, x => x.CreateView()) as UIElement)).Where(x => x.View != null).ToList();
        foreach (var (m, view) in views)
        {
            // 하위 항목은 부모 탭 바로 아래에서 보여준다 (부모 탭이 없으면 일반 항목).
            if (m.NavParentId is { } pid && views.Any(x => x.Module.Id == pid)) continue;
            items.Add(new NavItem(m.Icon, m.DisplayName, view!));
            foreach (var (child, childView) in views.Where(x => x.Module.NavParentId == m.Id))
                items.Add(new NavItem(child.Icon, child.DisplayName, childView!, IsChild: true));
        }
        // 모듈들의 전역 설정은 사이드바 '설정' 페이지 하나로 모은다.
        if (SettingsPage.Create(app.Host.Modules, app.Host) is { } settings)
            items.Add(new NavItem("", "설정", settings));

        Nav.ItemsSource = items;
        Nav.SelectedIndex = items.Count > 1 ? 1 : 0; // 캡처 화면을 기본으로

        _modeReady = false;
        (CombatMode.Load(app.Settings) ? ModeCombat : ModeIdle).IsChecked = true;
        _modeReady = true;

        UpdateClients(app.Registry.Current);
        app.Bus.Subscribe<ClientsChanged>(e => Dispatcher.Invoke(() => UpdateClients(e.Clients)));

        _wideItemsPanel = Nav.ItemsPanel;
        _wideItemTemplate = Nav.ItemTemplate;
        Root.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width < CompactBelow);

        SourceInitialized += (_, _) =>
        {
            // 제목 표시줄을 다크로
            var hwnd = new WindowInteropHelper(this).Handle;
            var on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
            FitToWorkArea(hwnd);
        };
    }

    // ---- 좁은 창 (세로 모니터 등) ----

    /// <summary>창 안쪽 너비가 이보다 좁으면 사이드바를 위쪽 가로 메뉴로 옮긴다 (사이드바를 둔 채로는 페이지가 잘리기 시작하는 너비).</summary>
    private const double CompactBelow = 1150;
    private bool _compact;
    private readonly ItemsPanelTemplate _wideItemsPanel;
    private readonly DataTemplate _wideItemTemplate;
    private static readonly ItemsPanelTemplate RowItemsPanel = new(new FrameworkElementFactory(typeof(WrapPanel)));

    /// <summary>가로 메뉴 항목: 사이드바와 같은 아이콘 · 이름이지만 하위 항목(└ 분석)을 들여쓰지 않는다.</summary>
    private static readonly DataTemplate RowItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding Connector}" FontSize="14" VerticalAlignment="Center" Foreground="{DynamicResource TextDim}" Margin="0,0,4,0"/>
                <TextBlock Text="{Binding Icon}" FontFamily="{DynamicResource IconFont}" FontSize="16" Width="{Binding IconWidth}" VerticalAlignment="Center" Foreground="{DynamicResource TextDim}"/>
                <TextBlock Text="{Binding Title}" FontSize="14" Margin="6,0,0,0" VerticalAlignment="Center"/>
            </StackPanel>
        </DataTemplate>
        """);

    /// <summary>
    /// 넓을 때: 왼쪽 사이드바(메뉴 → 아래쪽 모드 · 클라이언트 수). 좁을 때: 위쪽 한 줄(로고 · 메뉴 … 모드 · 클라이언트 수)로 옮기고 페이지가 창 너비를 다 쓴다.
    /// 메뉴가 한 줄에 다 안 들어가면 다음 줄로 넘어간다.
    /// </summary>
    private void ApplyLayout(bool compact)
    {
        if (compact == _compact) return;
        _compact = compact;

        Grid.SetRow(Sidebar, 0); Grid.SetRowSpan(Sidebar, compact ? 1 : 2);
        Grid.SetColumn(Sidebar, 0); Grid.SetColumnSpan(Sidebar, compact ? 2 : 1);
        Sidebar.BorderThickness = compact ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
        SidebarPanel.Margin = compact ? new Thickness(14, 10, 14, 10) : new Thickness(14, 18, 14, 14);

        DockPanel.SetDock(Brand, compact ? Dock.Left : Dock.Top);
        Brand.Margin = compact ? new Thickness(4, 0, 16, 0) : new Thickness(6, 0, 0, 22);
        Brand.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Stretch;

        // 클라이언트 수: 좁을 때는 '클라이언트 4' 한 줄
        DockPanel.SetDock(CountCard, compact ? Dock.Right : Dock.Bottom);
        CountCard.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        CountCard.Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(12, 10, 12, 10);
        CountCard.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        CountPanel.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        CountLabel.Text = compact ? "클라이언트" : "실행 중인 클라이언트";
        CountLabel.VerticalAlignment = VerticalAlignment.Center;
        ClientCount.FontSize = compact ? 16 : 22;
        ClientCount.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(0);

        // 모드: 좁을 때는 '모드' 글자 없이 전투 | 비전투 단추만
        DockPanel.SetDock(ModeCard, compact ? Dock.Right : Dock.Bottom);
        ModeCard.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(0, 0, 0, 10);
        ModeCard.Padding = compact ? new Thickness(6) : new Thickness(12, 10, 12, 10);
        ModeCard.Width = compact ? 150 : double.NaN;
        ModeCard.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        ModeLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ModeBar.Margin = compact ? new Thickness(0) : new Thickness(0, 6, 0, 0);

        // 메뉴: 가로로 늘어놓고(하위 항목도 들여쓰지 않는다), 넘치면 다음 줄
        Nav.ItemsPanel = compact ? RowItemsPanel : _wideItemsPanel;
        Nav.ItemContainerStyle = compact ? RowItemStyle() : null;
        Nav.ItemTemplate = compact ? RowItemTemplate : _wideItemTemplate;
        Nav.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Stretch;

        Grid.SetRow(Page, compact ? 1 : 0); Grid.SetRowSpan(Page, compact ? 1 : 2);
        Grid.SetColumn(Page, compact ? 0 : 1); Grid.SetColumnSpan(Page, compact ? 2 : 1);
        Page.Margin = compact ? new Thickness(20, 16, 20, 20) : new Thickness(24);
    }

    private Style? _rowItemStyle;

    /// <summary>가로 메뉴 항목: 기본 모양 그대로, 항목 사이에만 간격.</summary>
    private Style RowItemStyle()
    {
        if (_rowItemStyle != null) return _rowItemStyle;
        _rowItemStyle = new Style(typeof(ListBoxItem), (Style)FindResource(typeof(ListBoxItem)));
        _rowItemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 4, 0)));
        return _rowItemStyle;
    }

    /// <summary>
    /// 처음 열 때 창이 모니터 작업 영역(작업 표시줄 제외)보다 크면 그 안에 맞춘다. 기본 크기(1200×820)가 세로 FHD(너비 1080)보다 넓어 화면 밖으로 나가지 않게.
    /// 창이 열린 모니터 기준이며, 그 모니터의 배율(DPI)을 반영한다.
    /// </summary>
    private void FitToWorkArea(nint hwnd)
    {
        var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (mon == 0 || !GetMonitorInfo(mon, ref info)) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double left = info.Work.Left / dpi.DpiScaleX, top = info.Work.Top / dpi.DpiScaleY;
        double width = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, height = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
        if (width <= 0 || height <= 0) return;
        if (Width > width) Width = Math.Max(MinWidth, width);
        if (Height > height) Height = Math.Max(MinHeight, height);

        // 위치를 지정하지 않으면 Windows 가 정한 자리에 열리므로(Left/Top 이 NaN), 실제 창 위치를 읽어 작업 영역 안으로 옮긴다
        double x = Left, y = Top;
        if ((double.IsNaN(x) || double.IsNaN(y)) && GetWindowRect(hwnd, out var r)) { x = r.Left / dpi.DpiScaleX; y = r.Top / dpi.DpiScaleY; }
        if (double.IsNaN(x) || double.IsNaN(y)) return;
        var nx = Math.Max(left, Math.Min(x, left + width - Width));
        var ny = Math.Max(top, Math.Min(y, top + height - Height));
        if (nx != x || ny != y) { Left = nx; Top = ny; }
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect32 rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect32 Monitor; public Rect32 Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    private bool _modeReady;

    /// <summary>전투 / 비전투 모드를 바꾼다: 저장하고 전투 로그 · 프리뷰가 따르도록 알린다.</summary>
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_modeReady) return;
        var app = (App)Application.Current;
        CombatMode.Set(app.Settings, app.Bus, ModeCombat.IsChecked == true);
    }


    private void UpdateClients(IReadOnlyList<EveClient> clients) => ClientCount.Text = clients.Count.ToString();

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is NavItem n) Page.Content = n.Content;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
