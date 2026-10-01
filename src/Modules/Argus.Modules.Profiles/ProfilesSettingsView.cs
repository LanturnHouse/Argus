using System.Windows;
using System.Windows.Controls;

namespace Argus.Modules.Profiles;

/// <summary>설정 페이지의 '설정 프리셋' 섹션: 프리셋을 적용할 EVE 설정 폴더.</summary>
internal sealed class ProfilesSettingsView : UserControl
{
    private readonly ProfilesService _svc;
    private readonly ComboBox _combo = new();
    private readonly TextBlock _path = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private bool _loading;

    public ProfilesSettingsView(ProfilesService svc)
    {
        _svc = svc;

        var title = new TextBlock { Text = "EVE 설정 폴더", FontWeight = FontWeights.SemiBold };
        var desc = new TextBlock
        {
            Text = "설정파일을 가져오고 프리셋을 적용할 EVE 설정 폴더입니다. 보통 자동으로 찾은 폴더를 그대로 씁니다.",
            Margin = new Thickness(0, 2, 0, 0),
        };
        desc.SetResourceReference(FrameworkElement.StyleProperty, "Dim");
        _path.SetResourceReference(FrameworkElement.StyleProperty, "Dim");

        var browse = new Button { Content = "폴더 지정", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        var refresh = new Button { Content = "새로고침", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        _combo.SelectionChanged += Folder_Changed;
        browse.Click += Browse_Click;
        refresh.Click += (_, _) => Reload();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(browse); buttons.Children.Add(refresh);
        DockPanel.SetDock(buttons, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(buttons); row.Children.Add(_combo);

        var root = new StackPanel();
        root.Children.Add(title); root.Children.Add(desc); root.Children.Add(row); root.Children.Add(_path);
        Content = root;

        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _loading = true;
        _combo.Items.Clear();
        var current = _svc.SettingsFolder;
        foreach (var f in _svc.DiscoverFolders())
        {
            var item = new ComboBoxItem { Content = f.Label, Tag = f.Path };
            _combo.Items.Add(item);
            if (string.Equals(f.Path, current, StringComparison.OrdinalIgnoreCase)) _combo.SelectedItem = item;
        }
        _path.Text = current ?? "EVE 설정 폴더를 찾지 못했습니다. '폴더 지정'으로 직접 선택하세요.";
        _loading = false;
    }

    private void Folder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _combo.SelectedItem is not ComboBoxItem { Tag: string path }) return;
        _svc.SetSettingsFolder(path);
        _path.Text = path;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "EVE 설정 폴더 (settings_...)",
            InitialDirectory = _svc.SettingsFolder ?? EveSettingsLocator.DefaultRoot,
        };
        if (dlg.ShowDialog() != true) return;
        _svc.SetSettingsFolder(dlg.FolderName);
        Reload();
    }
}
