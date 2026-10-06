using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Argus.Modules.Preview;

/// <summary>이름 하나를 입력받는 작은 창 (프리셋 이름 등). 테마 스타일이 그대로 적용된다.</summary>
internal sealed class PromptWindow : Window
{
    private readonly TextBox _box = new() { MaxLength = 40 };

    private PromptWindow(string title, string label, string initial)
    {
        Title = title;
        Width = 420; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Argus.Ui.DarkTitleBar.Apply(this);
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");

        var text = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) };
        _box.Text = initial;
        _box.SelectAll();

        var ok = new Button { Content = "확인", Padding = new Thickness(22, 8, 22, 8), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "취소", Padding = new Thickness(18, 8, 18, 8), IsCancel = true };
        ok.Click += (_, _) => { if (_box.Text.Trim().Length > 0) DialogResult = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(text); root.Children.Add(_box); root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _box.Focus(); Keyboard.Focus(_box); };
    }

    /// <summary>입력한 이름을 반환한다. 취소하면 null.</summary>
    public static string? Ask(Window? owner, string title, string label, string initial = "")
    {
        var w = new PromptWindow(title, label, initial) { Owner = owner };
        return w.ShowDialog() == true ? w._box.Text.Trim() : null;
    }
}
