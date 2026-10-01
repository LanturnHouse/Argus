using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Argus.Core.Clients;
using Argus.Core.Dashboard;
using Argus.Core.Settings;
using Argus.Ui;

namespace Argus.App;

public sealed record NavItem(string Icon, string Title, UIElement Content);

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var app = (App)Application.Current;

        // 대시보드: 각 모듈이 내놓는 정보(IDashboardContributor)를 모아서 보여준다.
        var dashboard = new DashboardView(app.Registry, app.Host.Modules.OfType<IDashboardContributor>());
        var items = new List<NavItem> { new("", "대시보드", dashboard) };
        foreach (var m in app.Host.Modules)
        {
            if (m.CreateView() is UIElement view)
                items.Add(new NavItem(m.Icon, m.DisplayName, view));
        }
        // 모듈들의 전역 설정은 사이드바 '설정' 페이지 하나로 모은다.
        if (SettingsPage.Create(app.Host.Modules) is { } settings)
            items.Add(new NavItem("", "설정", settings));

        Nav.ItemsSource = items;
        Nav.SelectedIndex = items.Count > 1 ? 1 : 0; // 캡처 화면을 기본으로

        _modeReady = false;
        (CombatMode.Load(app.Settings) ? ModeCombat : ModeIdle).IsChecked = true;
        ShowModeNote();
        _modeReady = true;

        UpdateClients(app.Registry.Current);
        app.Bus.Subscribe<ClientsChanged>(e => Dispatcher.Invoke(() => UpdateClients(e.Clients)));

        SourceInitialized += (_, _) =>
        {
            // 제목 표시줄을 다크로
            var hwnd = new WindowInteropHelper(this).Handle;
            var on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        };
    }

    private bool _modeReady;

    /// <summary>전투 / 비전투 모드를 바꾼다: 저장하고 전투 로그 · 상태이상 인식 · 프리뷰가 따르도록 알린다.</summary>
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_modeReady) return;
        var app = (App)Application.Current;
        CombatMode.Set(app.Settings, app.Bus, ModeCombat.IsChecked == true);
        ShowModeNote();
    }

    private void ShowModeNote() => ModeNote.Text = ModeCombat.IsChecked == true
        ? "전투 로그 · 태클 · 레드박싱 작동 중"
        : "전투 기능 정지 (리소스 절약)";

    private void UpdateClients(IReadOnlyList<EveClient> clients) => ClientCount.Text = clients.Count.ToString();

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is NavItem n) Page.Content = n.Content;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
