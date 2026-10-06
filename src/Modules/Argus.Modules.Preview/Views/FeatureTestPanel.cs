using System.Windows;
using System.Windows.Controls;
using Argus.Ui;

namespace Argus.Modules.Preview;

/// <summary>설정 > 프리뷰 맨 아래 '기능 테스트': 실제 전투 없이 레드박싱 · 수치 표시를 프리뷰 HUD 에서 시험한다.</summary>
internal sealed class FeatureTestPanel : UserControl
{
    private const string AllClients = "전체 클라이언트";

    private readonly PreviewService _svc;
    private readonly ComboBox _target = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };

    // 수치
    private readonly CheckBox _dpsIn = new() { Content = "받는 DPS ▼", IsChecked = true };
    private readonly CheckBox _logi = new() { Content = "받는 LOGI ✚", IsChecked = true };
    private readonly CheckBox _neut = new() { Content = "받는 뉴트 ⚡", IsChecked = true };
    private readonly NumberBox _initial = new() { Minimum = -99999, Maximum = 999999, Step = 1, Decimals = 0, Value = 100, TextWidth = 90 };
    private readonly NumberBox _peak = new() { Minimum = -99999, Maximum = 999999, Step = 1, Decimals = 0, Value = 3000, TextWidth = 90 };
    private readonly NumberBox _rise = new() { Minimum = 1, Maximum = 600, Step = 1, Decimals = 0, Unit = "초", Value = 10 };
    private readonly NumberBox _hold = new() { Minimum = 0, Maximum = 600, Step = 1, Decimals = 0, Unit = "초", Value = 3 };
    private readonly NumberBox _fall = new() { Minimum = 1, Maximum = 600, Step = 1, Decimals = 0, Unit = "초", Value = 10 };

    public FeatureTestPanel(PreviewService svc)
    {
        _svc = svc;
        _target.DropDownOpened += (_, _) => RefreshTargets();
        Loaded += (_, _) => RefreshTargets();

        var root = new StackPanel();
        var intro = new TextBlock { Text = "실제 전투 없이 프리뷰 HUD 의 레드박싱 · 수치 표시를 시험합니다. 시험 값은 실제 전투 값보다 우선하고, 끝나면 저절로 실제 값으로 돌아갑니다.", Margin = new Thickness(0, 0, 0, 8) };
        intro.SetResourceReference(StyleProperty, "Dim");
        root.Children.Add(intro);
        root.Children.Add(Field("테스트 대상", _target));

        root.Children.Add(Header("레드박싱"));
        var surge = Btn("레드박싱 발생", () => _svc.TestSurge(Target()), primary: true);
        root.Children.Add(Field("레드박싱", Wrap(surge), "붉은 깜빡임, 전환 키 안내, 레드박싱 전환 단축키(지정했다면)가 실제 레드박싱처럼 동작합니다. 유지 시간은 위의 레드박싱 설정을 따릅니다."));

        root.Children.Add(Header("수치"));
        var items = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new[] { _dpsIn, _logi, _neut }) { c.Margin = new Thickness(0, 0, 18, 0); items.Children.Add(c); }
        root.Children.Add(Field("시험할 항목", items, "고른 항목에 같은 값이 표시됩니다."));
        root.Children.Add(Field("초기 수치", _initial, "시작할 때와 끝난 뒤의 값입니다."));
        root.Children.Add(Field("피크 수치", _peak, "가장 높이 오르는 값입니다. 받는 뉴트는 음수로 두면 노스로 빠는 상황(+ 표시)을 시험할 수 있습니다."));
        root.Children.Add(Field("상승 시간", _rise));
        root.Children.Add(Field("피크 유지 시간", _hold));
        root.Children.Add(Field("하강 시간", _fall));

        var run = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        run.Children.Add(Btn("수치 테스트 시작", StartRamp, primary: true));
        run.Children.Add(Btn("모든 테스트 중지", () => _svc.StopFeatureTests()));
        root.Children.Add(run);

        Content = root;
    }

    private void StartRamp()
    {
        if (_dpsIn.IsChecked != true && _logi.IsChecked != true && _neut.IsChecked != true) return;
        _svc.TestRamp(Target(), new TestRampSpec(_dpsIn.IsChecked == true, _logi.IsChecked == true, _neut.IsChecked == true,
            _initial.Value, _peak.Value, _rise.Value, _hold.Value, _fall.Value));
    }

    private string? Target() => (_target.SelectedItem as ComboBoxItem)?.Tag as string;

    private void RefreshTargets()
    {
        var current = Target();
        _target.Items.Clear();
        _target.Items.Add(new ComboBoxItem { Content = AllClients, Tag = null });
        foreach (var name in _svc.RunningCharacters()) _target.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        _target.SelectedItem = _target.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == current) ?? _target.Items[0];
    }

    private static Button Btn(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
        b.Click += (_, _) => click();
        return b;
    }

    private static UIElement Wrap(UIElement e) { var p = new StackPanel { Orientation = Orientation.Horizontal }; p.Children.Add(e); return p; }

    private static UIElement Header(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) };
        return t;
    }

    /// <summary>왼쪽 이름 + 오른쪽 입력 한 줄. 설명이 있으면 그 아래에 작게.</summary>
    private static UIElement Field(string label, UIElement control, string? note = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel();
        right.Children.Add(control);
        if (note != null)
        {
            var n = new TextBlock { Text = note, Margin = new Thickness(0, 3, 0, 0) };
            n.SetResourceReference(StyleProperty, "Dim");
            right.Children.Add(n);
        }
        Grid.SetColumn(right, 1);
        grid.Children.Add(name); grid.Children.Add(right);
        return grid;
    }
}
