using System.Windows;
using System.Windows.Controls;

namespace Argus.Modules.Capture;

/// <summary>설정 페이지의 'CCTV' 섹션: 스크린샷 저장 폴더.</summary>
internal sealed class CaptureSettingsView : UserControl
{
    public CaptureSettingsView(CaptureService service)
    {
        var title = new TextBlock { Text = "스크린샷 저장 폴더", FontWeight = FontWeights.SemiBold };
        var desc = new TextBlock
        {
            Text = "변화가 감지되면 CCTV{시각}_{캐릭터}.png 로 이 폴더에 저장되고, 분석이 이 폴더의 파일을 읽습니다.",
            Margin = new Thickness(0, 2, 0, 0),
        };
        desc.SetResourceReference(FrameworkElement.StyleProperty, "Dim");

        var box = new TextBox { Text = service.OutputFolder, IsReadOnly = true };
        var change = new Button { Content = "변경", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        var open = new Button { Content = "열기", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };

        change.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "스크린샷 저장 폴더", InitialDirectory = service.OutputFolder };
            if (dlg.ShowDialog() != true) return;
            service.OutputFolder = dlg.FolderName;
            box.Text = dlg.FolderName;
        };
        open.Click += (_, _) => service.OpenOutputFolder();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(change); buttons.Children.Add(open);
        DockPanel.SetDock(buttons, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(buttons); row.Children.Add(box);

        var root = new StackPanel();
        root.Children.Add(title); root.Children.Add(desc); root.Children.Add(row);
        Content = root;
    }
}
