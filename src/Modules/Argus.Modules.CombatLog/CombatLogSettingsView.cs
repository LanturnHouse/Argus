using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Argus.Ui;

namespace Argus.Modules.CombatLog;

/// <summary>설정 > 프리뷰 > '전투 로그': 수치 계산 구간, 태클 표시 시간, 로그 폴더, 지금 읽고 있는 로그.</summary>
internal sealed class CombatLogSettingsView : UserControl
{
    private readonly CombatLogService _svc;
    // 수치는 모두 직접 입력 칸(위·아래 화살표): 정수는 1 단위, 배수는 0.1 단위
    private readonly NumberBox _window = new() { Minimum = CombatLogSettings.MinWindow, Maximum = CombatLogSettings.MaxWindow, Step = 1, Decimals = 0, Unit = "초" };
    private readonly NumberBox _hold = new() { Minimum = CombatLogSettings.MinHold, Maximum = CombatLogSettings.MaxHold, Step = 1, Decimals = 0, Unit = "초" };
    private readonly NumberBox _surgeDps = new() { Minimum = CombatLogSettings.MinSurgeDps, Maximum = CombatLogSettings.MaxSurgeDps, Step = 1, Decimals = 0, Unit = "DPS" };
    private readonly NumberBox _surgeRatio = new() { Minimum = CombatLogSettings.MinSurgeRatio, Maximum = CombatLogSettings.MaxSurgeRatio, Step = 0.1, Decimals = 1, Unit = "배" };
    private readonly TextBox _folder = new() { IsReadOnly = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;

    public CombatLogSettingsView(CombatLogService svc)
    {
        _svc = svc;
        _status.SetResourceReference(StyleProperty, "Dim");

        var root = new StackPanel();
        root.Children.Add(Section("수치 계산 구간", "받는 DPS · 받는 LOGI · 받는 뉴트는 최근 이 시간 동안의 합을 초로 나눈 평균입니다. 받는 뉴트는 이펙티브 값으로, 받은 뉴트와 노스 피해에서, 내가 노스페라투로 빤 양과 원격 캐패시터 전송으로 받은 양을 뺀 순 캡 감소량(GJ/초)입니다. 짧을수록 빨리 반응하고 들쭉날쭉하며, 길수록 부드럽습니다.", _window));
        root.Children.Add(Section("태클 표시 유지 시간", "태클(스크램블·디스럽트·HIC) 시도가 로그에 마지막으로 나온 뒤 이 시간 동안 걸려 있는 것으로 표시합니다. 로그에는 시도만 남고 풀림은 기록되지 않아서 시간으로 판단합니다.", _hold));

        root.Children.Add(Section("레드박싱: 최소 받는 DPS", "최근 3초의 받는 DPS 가 이 값 이상이어야 레드박싱으로 봅니다. 너무 작은 피해에 반응하지 않게 하는 기준입니다.", _surgeDps));
        root.Children.Add(Section("레드박싱: 배수", "최근 3초의 받는 DPS 가 그 직전 30초 평균의 이 배수 이상이어야 레드박싱으로 봅니다. 클수록 갑작스러운 증가만 잡습니다.", _surgeRatio));

        var change = new Button { Content = "변경", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        var auto = new Button { Content = "자동", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0), ToolTip = "문서 폴더의 EVE/logs/Gamelogs 로 되돌립니다" };
        var open = new Button { Content = "열기", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(change); buttons.Children.Add(auto); buttons.Children.Add(open);
        DockPanel.SetDock(buttons, Dock.Right);
        var folderRow = new DockPanel();
        folderRow.Children.Add(buttons); folderRow.Children.Add(_folder);
        root.Children.Add(Section("전투 로그 폴더", "EVE 가 전투 로그를 저장하는 폴더입니다. 읽기만 하며 파일을 수정하지 않습니다.", folderRow));

        root.Children.Add(Section("지금 읽고 있는 로그", "실행 중인 클라이언트마다 그 캐릭터의 가장 최근 로그를 읽습니다. 전투 사건이 0 으로 계속 남아 있으면 로그가 그 캐릭터의 것이 아니거나 아직 전투가 없는 것입니다.", _status));

        Content = root;
        Loaded += (_, _) => { Load(); _refresh.Start(); RefreshStatus(); };
        Unloaded += (_, _) => _refresh.Stop();
        _refresh.Tick += (_, _) => RefreshStatus();

        _window.ValueChanged += (_, _) => { if (!_loading) _svc.Update(windowSeconds: (int)_window.Value); };
        _hold.ValueChanged += (_, _) => { if (!_loading) _svc.Update(tackleHoldSeconds: (int)_hold.Value); };
        _surgeDps.ValueChanged += (_, _) => { if (!_loading) _svc.Update(surgeMinDps: (int)_surgeDps.Value); };
        _surgeRatio.ValueChanged += (_, _) => { if (!_loading) _svc.Update(surgeRatio: _surgeRatio.Value); };
        change.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "전투 로그 폴더", InitialDirectory = _svc.FolderPath };
            if (dlg.ShowDialog() != true) return;
            _svc.Update(logFolder: dlg.FolderName);
            _folder.Text = _svc.FolderPath;
        };
        auto.Click += (_, _) => { _svc.Update(logFolder: ""); _folder.Text = _svc.FolderPath; };
        open.Click += (_, _) =>
        {
            if (Directory.Exists(_svc.FolderPath)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_svc.FolderPath}\"") { UseShellExecute = true });
        };
    }

    private void Load()
    {
        _loading = true;
        _window.Value = _svc.Settings.WindowSeconds;
        _hold.Value = _svc.Settings.TackleHoldSeconds;
        _surgeDps.Value = _svc.Settings.SurgeMinDps;
        _surgeRatio.Value = _svc.Settings.SurgeRatio;
        _folder.Text = _svc.FolderPath;
        _loading = false;
    }

    private void RefreshStatus()
    {
        if (!Directory.Exists(_svc.FolderPath)) { _status.Text = "로그 폴더를 찾을 수 없습니다. 위에서 폴더를 지정하세요."; return; }
        var rows = _svc.Status();
        _status.Text = rows.Count == 0
            ? "읽고 있는 로그가 없습니다. (실행 중인 클라이언트가 없거나, 그 캐릭터의 로그 파일을 찾지 못했습니다.)"
            : string.Join("\n", rows.Select(r => $"{r.Character}  ·  {r.File}  ·  읽은 줄 {r.Lines:N0}  ·  전투 사건 {r.Events:N0}" +
                (r.LastEvent is { } t ? $"  ·  마지막 {Math.Max(0, (int)(DateTime.Now - t).TotalSeconds)}초 전" : "")));
    }

    private static UIElement Section(string title, string desc, UIElement control)
    {
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
        var d = new TextBlock { Text = desc, Margin = new Thickness(0, 2, 0, 0) };
        d.SetResourceReference(StyleProperty, "Dim");
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        sp.Children.Add(t); sp.Children.Add(d);
        if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(control);
        return sp;
    }
}
