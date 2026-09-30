using System.Windows;
using System.Windows.Controls;

namespace Argus.Modules.Cctv;

/// <summary>설정 > CCTV: 웹앱 폴더, node.exe 경로, Argus 시작 시 자동 실행.</summary>
internal sealed class CctvSettingsView : UserControl
{
    private readonly CctvService _svc;
    private readonly CheckBox _auto = new() { Content = "Argus 를 켤 때 CCTV 웹앱도 같이 시작" };
    private readonly TextBox _app = new() { IsReadOnly = true };
    private readonly TextBox _node = new() { IsReadOnly = true };
    private readonly TextBlock _appUsed = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _nodeUsed = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public CctvSettingsView(CctvService svc)
    {
        _svc = svc;
        _appUsed.SetResourceReference(StyleProperty, "Dim");
        _nodeUsed.SetResourceReference(StyleProperty, "Dim");

        var root = new StackPanel();
        root.Children.Add(Section("자동 시작", "꺼 두면 CCTV 탭에서 '시작'을 눌러야 켜집니다. 켜 두면 Argus 를 켤 때마다 Node 프로세스 두 개가 같이 뜹니다.", _auto));

        var appBox = new StackPanel();
        appBox.Children.Add(PathRow(_app, "선택", "자동", PickApp, () => { _svc.Settings.AppFolder = ""; Save(); }));
        appBox.Children.Add(_appUsed);
        root.Children.Add(Section("웹앱 폴더", "EVE CCTV 웹앱 폴더(local-service\\server.mjs 가 있는 곳)입니다. '자동'이면 알려진 위치(D:\\eve cctv web v2)에서 찾습니다. 바꾸면 다음에 시작할 때부터 적용됩니다.", appBox));

        var nodeBox = new StackPanel();
        nodeBox.Children.Add(PathRow(_node, "선택", "자동", PickNode, () => { _svc.Settings.NodePath = ""; Save(); }));
        nodeBox.Children.Add(_nodeUsed);
        root.Children.Add(Section("node.exe", "웹앱을 실행할 Node.js 입니다 (22.13 이상 필요). '자동'이면 Argus 옆 runtime\\node.exe 를 먼저 쓰고, 없으면 PC 에 설치된 node 를 씁니다.", nodeBox));

        Content = root;
        Loaded += (_, _) => Load();
        _auto.Click += (_, _) => { _svc.Settings.AutoStart = _auto.IsChecked == true; Save(); };
    }

    private void Save() { _svc.SaveSettings(); Load(); }

    private void Load()
    {
        var s = _svc.Settings;
        _auto.IsChecked = s.AutoStart;
        _app.Text = s.AppFolder.Length > 0 ? s.AppFolder : "(자동)";
        _node.Text = s.NodePath.Length > 0 ? s.NodePath : "(자동)";
        _appUsed.Text = s.ResolveAppFolder() is { } a ? $"지금 사용: {a}" : "웹앱 폴더를 찾지 못했습니다. 위에서 선택하세요.";
        _nodeUsed.Text = s.ResolveNode() is { } n ? $"지금 사용: {n}" : "node.exe 를 찾지 못했습니다. Node.js 를 설치하거나 위에서 선택하세요.";
    }

    private void PickApp()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "EVE CCTV 웹앱 폴더" };
        if (dlg.ShowDialog() != true) return;
        if (!System.IO.File.Exists(System.IO.Path.Combine(dlg.FolderName, "local-service", "server.mjs")))
        {
            MessageBox.Show("이 폴더에는 local-service\\server.mjs 가 없습니다. EVE CCTV 웹앱 폴더를 선택하세요.", "웹앱 폴더", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _svc.Settings.AppFolder = dlg.FolderName; Save();
    }

    private void PickNode()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "node.exe", Filter = "node.exe|node.exe|실행 파일|*.exe" };
        if (dlg.ShowDialog() != true) return;
        _svc.Settings.NodePath = dlg.FileName; Save();
    }

    private static UIElement PathRow(TextBox box, string pick, string auto, Action onPick, Action onAuto)
    {
        var b1 = new Button { Content = pick, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        var b2 = new Button { Content = auto, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        b1.Click += (_, _) => onPick(); b2.Click += (_, _) => onAuto();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(b1); buttons.Children.Add(b2);
        DockPanel.SetDock(buttons, Dock.Right);
        var row = new DockPanel();
        row.Children.Add(buttons); row.Children.Add(box);
        return row;
    }

    private static UIElement Section(string title, string desc, UIElement control)
    {
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
        var d = new TextBlock { Text = desc, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        d.SetResourceReference(StyleProperty, "Dim");
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        sp.Children.Add(t); sp.Children.Add(d);
        if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(control);
        return sp;
    }
}
