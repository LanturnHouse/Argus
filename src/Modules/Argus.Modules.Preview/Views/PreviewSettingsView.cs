using System.Windows;
using System.Windows.Controls;
using Argus.Ui;

namespace Argus.Modules.Preview;

/// <summary>설정 페이지의 '프리뷰' 섹션: 기본 크기, 불투명도, 활성 클라이언트 숨기기.</summary>
internal sealed class PreviewSettingsView : UserControl
{
    private static readonly (string Label, int Width)[] Sizes =
        [("작게 (160)", 160), ("기본 (210)", 210), ("보통 (280)", 280), ("크게 (360)", 360), ("아주 크게 (480)", 480)];

    private readonly PreviewService _svc;
    private readonly ComboBox _size = new();
    private readonly Slider _opacity = new() { Minimum = 30, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly TextBlock _opacityText = new() { Width = 44, TextAlignment = TextAlignment.Right };
    private readonly CheckBox _hideActive = new() { Content = "지금 활성인 클라이언트의 프리뷰 숨기기" };
    private readonly CheckBox _onlyEve = new() { Content = "EVE 를 플레이 중일 때만 프리뷰 표시" };
    // %로 조절하는 것은 바(슬라이더), 그 밖의 수치는 직접 입력 칸(위·아래 화살표)
    private readonly Slider _barOpacity = new() { Minimum = 20, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly TextBlock _barOpacityText = new() { Width = 44, TextAlignment = TextAlignment.Right };
    private readonly NumberBox _surge = new() { Minimum = 2, Maximum = 60, Step = 1, Decimals = 0, Unit = "초" };
    private readonly NumberBox _surgeFlash = new() { Minimum = 0.2, Maximum = 2.0, Step = 0.1, Decimals = 1, Unit = "초 (짧을수록 빠름)" };
    private readonly CheckBox _surgeHint = new() { Content = "레드박싱이 난 클라이언트 프리뷰에 레드박싱 전환 단축키를 밝은 회색으로 표시" };
    private readonly CheckBox _hkEnabled = new() { Content = "단축키 사용" };
    private readonly CheckBox _hkOnlyEve = new() { Content = "EVE 클라이언트나 Argus 가 맨 앞일 때만 단축키 동작" };
    private readonly CheckBox _hkExtraMods = new() { Content = "보조키(Ctrl·Shift·Alt)를 누른 채로도 동작" };
    private bool _loading;

    public PreviewSettingsView(PreviewService svc)
    {
        _svc = svc;

        foreach (var (label, w) in Sizes) _size.Items.Add(new ComboBoxItem { Content = label, Tag = w });

        var root = new StackPanel();
        root.Children.Add(Section("표시 조건", "Argus 창이 앞에 있을 때도 보입니다.", _onlyEve));
        root.Children.Add(Section("기본 프리뷰 크기", "새 클라이언트의 프리뷰 가로 크기입니다. 세로는 화면 비율로 정해집니다.", _size));

        var opacityRow = new DockPanel();
        DockPanel.SetDock(_opacityText, Dock.Right);
        opacityRow.Children.Add(_opacityText); opacityRow.Children.Add(_opacity);
        root.Children.Add(Section("불투명도", "낮추면 뒤의 창이 비쳐 보입니다.", opacityRow));

        root.Children.Add(Section("HUD 수치 바 투명도", "받는 DPS · LOGI · 뉴트 표시", Row(_barOpacity, _barOpacityText)));

        root.Children.Add(Section("활성 클라이언트", null, _hideActive));

        root.Children.Add(Section("레드박싱: 유지 시간", "레드박싱 후 이 시간 동안 붉은 색조와 전환 키 안내가 보이고, 레드박싱 난 클라이언트로 전환하는 단축키가 동작합니다. 여러 곳이면 먼저 감지된 쪽부터 갑니다.", _surge));
        root.Children.Add(Section("레드박싱: 깜빡임 주기", null, _surgeFlash));
        root.Children.Add(Section("레드박싱: 전환 키 안내", null, _surgeHint));

        var hkBox = new StackPanel();
        _hkOnlyEve.Margin = new Thickness(0, 8, 0, 0);
        _hkExtraMods.Margin = new Thickness(0, 8, 0, 0);
        hkBox.Children.Add(_hkEnabled); hkBox.Children.Add(_hkOnlyEve); hkBox.Children.Add(_hkExtraMods);
        root.Children.Add(Section("단축키 동작 범위", "범위 제한을 켜면 다른 프로그램에서는 단축키가 그대로 전달됩니다. 보조키 옵션을 켜면 Ctrl 등을 누른 채로도 동작합니다. 단축키는 프리뷰 화면에서 프리셋별로 지정합니다.", hkBox));

        Content = root;
        Loaded += (_, _) => Load();
        _size.SelectionChanged += (_, _) =>
        {
            if (!_loading && _size.SelectedItem is ComboBoxItem { Tag: int w }) _svc.UpdateGlobalSettings(defaultWidth: w);
        };
        _opacity.ValueChanged += (_, _) =>
        {
            _opacityText.Text = $"{(int)_opacity.Value}%";
            if (!_loading) _svc.UpdateGlobalSettings(opacity: _opacity.Value / 100.0);
        };
        _hideActive.Click += (_, _) => _svc.UpdateGlobalSettings(hideActive: _hideActive.IsChecked == true);
        _onlyEve.Click += (_, _) => _svc.UpdateGlobalSettings(onlyWhenEveActive: _onlyEve.IsChecked == true);
        _barOpacity.ValueChanged += (_, _) => { _barOpacityText.Text = $"{(int)_barOpacity.Value}%"; if (!_loading) _svc.UpdateGlobalSettings(hudBarOpacity: _barOpacity.Value / 100.0); };
        _surge.ValueChanged += (_, _) => { if (!_loading) _svc.UpdateGlobalSettings(surgeSeconds: (int)_surge.Value); };
        _surgeFlash.ValueChanged += (_, _) => { if (!_loading) _svc.UpdateGlobalSettings(surgeFlashMs: (int)Math.Round(_surgeFlash.Value * 1000)); };
        _surgeHint.Click += (_, _) => _svc.UpdateGlobalSettings(showSurgeKeyHint: _surgeHint.IsChecked == true);
        _hkEnabled.Click += (_, _) => _svc.UpdateGlobalSettings(hotkeysEnabled: _hkEnabled.IsChecked == true);
        _hkOnlyEve.Click += (_, _) => _svc.UpdateGlobalSettings(hotkeysOnlyWhenEveActive: _hkOnlyEve.IsChecked == true);
        _hkExtraMods.Click += (_, _) => _svc.UpdateGlobalSettings(hotkeysAllowExtraMods: _hkExtraMods.IsChecked == true);
    }

    private void Load()
    {
        _loading = true;
        var w = _svc.Settings.DefaultWidth;
        _size.SelectedItem = _size.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == w)
                             ?? _size.Items.Cast<ComboBoxItem>().OrderBy(i => Math.Abs((int)i.Tag - w)).First();
        _opacity.Value = Math.Round(_svc.Settings.Opacity * 100);
        _opacityText.Text = $"{(int)_opacity.Value}%";
        _hideActive.IsChecked = _svc.Settings.HideActive;
        _onlyEve.IsChecked = _svc.Settings.OnlyWhenEveActive;
        _barOpacity.Value = Math.Round(_svc.Settings.HudBarOpacity * 100);
        _barOpacityText.Text = $"{(int)_barOpacity.Value}%";
        _surge.Value = _svc.Settings.SurgeSeconds;
        _surgeFlash.Value = _svc.Settings.SurgeFlashMs / 1000.0;
        _surgeHint.IsChecked = _svc.Settings.ShowSurgeKeyHint;
        _hkEnabled.IsChecked = _svc.Settings.HotkeysEnabled;
        _hkOnlyEve.IsChecked = _svc.Settings.HotkeysOnlyWhenEveActive;
        _hkExtraMods.IsChecked = _svc.Settings.HotkeysAllowExtraMods;
        _loading = false;
    }

    private static UIElement Row(Slider slider, TextBlock text)
    {
        var row = new DockPanel();
        DockPanel.SetDock(text, Dock.Right);
        row.Children.Add(text); row.Children.Add(slider);
        return row;
    }

    private static UIElement Section(string title, string? desc, UIElement control)
    {
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
        var d = new TextBlock { Text = desc ?? "", Margin = new Thickness(0, 2, 0, 0) };
        d.SetResourceReference(FrameworkElement.StyleProperty, "Dim");
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        sp.Children.Add(t);
        if (!string.IsNullOrEmpty(desc)) sp.Children.Add(d);
        if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(control);
        return sp;
    }
}
