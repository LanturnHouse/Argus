using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Argus.Modules.Alerts;

/// <summary>설정 페이지의 '알림' 섹션: 알림음 선택.</summary>
internal sealed class AlertsSettingsView : UserControl
{
    public AlertsSettingsView(string soundPath, Action<string> onChanged, Action test)
    {
        var title = new TextBlock { Text = "알림음", FontWeight = FontWeights.SemiBold };
        var desc = new TextBlock
        {
            Text = "변화가 감지되면 재생됩니다. 비워두면 시스템 경고음을 씁니다.",
            Margin = new Thickness(0, 2, 0, 0),
        };
        desc.SetResourceReference(FrameworkElement.StyleProperty, "Dim");

        var path = new TextBox { Text = soundPath, IsReadOnly = true };
        var browse = new Button { Content = "wav 선택", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        var reset = new Button { Content = "기본음", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        var play = new Button { Content = "테스트", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };

        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "WAV|*.wav", Title = "알림음 선택" };
            if (dlg.ShowDialog() != true) return;
            path.Text = dlg.FileName;
            onChanged(dlg.FileName);
        };
        reset.Click += (_, _) => { path.Text = ""; onChanged(""); };
        play.Click += (_, _) => test();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(browse); buttons.Children.Add(reset); buttons.Children.Add(play);
        DockPanel.SetDock(buttons, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(buttons); row.Children.Add(path);

        var root = new StackPanel();
        root.Children.Add(title); root.Children.Add(desc); root.Children.Add(row);
        Content = root;
    }
}
