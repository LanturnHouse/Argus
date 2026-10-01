using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Argus.Ui;

namespace Argus.Modules.Capture.StatusIcons;

/// <summary>설정 > 프리뷰 > '상태이상 인식': 사용 여부, 판정 기준, 읽는 영역, 지금 화면에서 어떻게 읽히는지 확인.</summary>
internal sealed class StatusIconSettingsView : UserControl
{
    private readonly StatusIconService _svc;
    private readonly CheckBox _enabled = new() { Content = "화면의 상태이상 아이콘으로 태클(스크램블·디스럽터·HIC 포인팅) 판정" };
    // %로 조절하는 것은 바(슬라이더), 그 밖의 수치는 직접 입력 칸(위·아래 화살표)
    private readonly Slider _shape = new() { Minimum = 30, Maximum = 95, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _shapeText = new() { Width = 44, TextAlignment = TextAlignment.Right };
    private readonly NumberBox _hz = new() { Minimum = StatusIconSettings.MinHz, Maximum = StatusIconSettings.MaxHz, Step = 1, Decimals = 0, Unit = "회 / 초" };
    private readonly NumberBox _on = new() { Minimum = 1, Maximum = 10, Step = 1, Decimals = 0, Unit = "번 연속" };
    private readonly NumberBox _off = new() { Minimum = 1, Maximum = 20, Step = 1, Decimals = 0, Unit = "번 연속" };
    private readonly NumberBox _bottom = new() { Minimum = 0, Maximum = 2000, Step = 1, Decimals = 0, Unit = "px" };
    private readonly NumberBox _height = new() { Minimum = 40, Maximum = 800, Step = 1, Decimals = 0, Unit = "px" };
    private readonly NumberBox _half = new() { Minimum = 100, Maximum = 2000, Step = 1, Decimals = 0, Unit = "px" };
    private readonly NumberBox _minR = new() { Minimum = 4, Maximum = 60, Step = 1, Decimals = 0, Unit = "px" };
    private readonly NumberBox _maxR = new() { Minimum = 5, Maximum = 100, Step = 1, Decimals = 0, Unit = "px" };
    private readonly ComboBox _client = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Image _image = new() { Stretch = Stretch.None, SnapsToDevicePixels = true };
    private readonly Canvas _overlay = new();
    private readonly TextBlock _probeText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loading;

    public StatusIconSettingsView(StatusIconService svc)
    {
        _svc = svc;
        _probeText.SetResourceReference(StyleProperty, "Dim");

        var root = new StackPanel();
        root.Children.Add(Section("상태이상 인식", "EVE 화면 아래 가운데의 상태이상 아이콘을 읽어 스크램블·디스럽터·HIC 포인팅을 판정합니다. 프리뷰 태클 리본은 이 기능으로만 나옵니다.", _enabled));

        var shapeRow = new DockPanel();
        DockPanel.SetDock(_shapeText, Dock.Right);
        shapeRow.Children.Add(_shapeText); shapeRow.Children.Add(_shape);
        root.Children.Add(Section("모양 판정 기준", "아이콘 모양이 태클과 이 정도 이상 닮아야 태클로 봅니다. 낮추면 다른 상태이상을 오인하고, 높이면 태클을 놓칩니다.", shapeRow));

        root.Children.Add(Section("읽는 횟수", "1초에 이만큼 화면을 읽습니다. 많을수록 빨리 반응하지만 조금 더 무겁습니다.", _hz));
        root.Children.Add(Section("켜짐 판정", "아이콘이 이만큼 연속으로 보여야 리본을 켭니다. 순간적인 오인식을 막습니다.", _on));
        root.Children.Add(Section("꺼짐 판정", "아이콘이 이만큼 연속으로 안 보여야 리본을 끕니다. 깜빡여도 리본이 흔들리지 않게 합니다.", _off));

        root.Children.Add(Section("읽는 영역: 화면 아래에서", "읽는 영역의 아래쪽 끝이 클라이언트 화면 아래에서 몇 px 위인지입니다. 아이콘 줄은 HUD 바로 위에 뜹니다.", _bottom));
        root.Children.Add(Section("읽는 영역: 높이", "읽는 영역의 세로 길이입니다. 아이콘 줄이 이 안에 들어와야 합니다.", _height));
        root.Children.Add(Section("읽는 영역: 좌우 폭", "화면 가운데에서 좌우로 각각 이만큼 읽습니다. 아이콘이 많이 떠도 다 들어오게 넉넉히 두세요.", _half));
        var radius = new StackPanel { Orientation = Orientation.Horizontal };
        radius.Children.Add(_minR);
        radius.Children.Add(new TextBlock { Text = "  ~  ", VerticalAlignment = VerticalAlignment.Center });
        radius.Children.Add(_maxR);
        root.Children.Add(Section("찾는 아이콘 크기(반지름)", "UI 배율에 따라 달라집니다 (100%에서 약 17px). 아이콘이 안 잡히면 범위를 넓혀 보세요.", radius));

        // ---- 지금 읽히는 모습 ----
        var view = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var stage = new Grid();
        stage.Children.Add(_image); stage.Children.Add(_overlay);
        view.Child = stage;
        var frameBox = new Border { Child = view, Background = Brushes.Black, Padding = new Thickness(2), Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        var probe = new StackPanel();
        _client.Margin = new Thickness(0, 8, 0, 0);
        probe.Children.Add(_client); probe.Children.Add(frameBox); probe.Children.Add(_probeText);
        root.Children.Add(Section("지금 읽히는 모습", "초록 원은 태클로 판정한 아이콘, 회색 원은 다른 상태이상입니다.", probe));

        Content = root;
        Loaded += (_, _) => { Load(); _refresh.Start(); Refresh(); };
        Unloaded += (_, _) => _refresh.Stop();
        _refresh.Tick += (_, _) => Refresh();

        _enabled.Click += (_, _) => Apply(s => s.Enabled = _enabled.IsChecked == true);
        _shape.ValueChanged += (_, _) => { _shapeText.Text = $"{(int)_shape.Value}%"; if (!_loading) Apply(s => s.ShapeThreshold = _shape.Value / 100.0); };
        _hz.ValueChanged += (_, _) => { if (!_loading) Apply(s => s.Hz = (int)_hz.Value); };
        _on.ValueChanged += (_, _) => { if (!_loading) Apply(s => s.OnFrames = (int)_on.Value); };
        _off.ValueChanged += (_, _) => { if (!_loading) Apply(s => s.OffFrames = (int)_off.Value); };
        _bottom.ValueChanged += (_, _) => { if (!_loading) Apply(s => s.BottomOffset = (int)_bottom.Value); };
        _height.ValueChanged += (_, _) => { if (!_loading) Apply(s => s.RegionHeight = (int)_height.Value); };
        _half.ValueChanged += (_, _) => { if (!_loading) Apply(s => s.HalfWidth = (int)_half.Value); };
        _minR.ValueChanged += (_, _) => { if (!_loading) Apply(s => s.MinRadius = (int)_minR.Value); };
        _maxR.ValueChanged += (_, _) => { if (!_loading) Apply(s => s.MaxRadius = (int)_maxR.Value); };
    }

    private void Apply(Action<StatusIconSettings> change)
    {
        change(_svc.Settings);
        _svc.Settings.Normalize();
        _svc.Save();
    }

    private void Load()
    {
        _loading = true;
        var s = _svc.Settings;
        _enabled.IsChecked = s.Enabled;
        _shape.Value = Math.Round(s.ShapeThreshold * 100);
        _shapeText.Text = $"{(int)_shape.Value}%";
        _hz.Value = s.Hz; _on.Value = s.OnFrames; _off.Value = s.OffFrames;
        _bottom.Value = s.BottomOffset; _height.Value = s.RegionHeight; _half.Value = s.HalfWidth;
        _minR.Value = s.MinRadius; _maxR.Value = s.MaxRadius;
        _loading = false;
    }

    private void Refresh()
    {
        var names = _svc.Characters;
        var selected = _client.SelectedItem as string;
        if (!names.SequenceEqual(_client.Items.Cast<string>()))
        {
            _client.Items.Clear();
            foreach (var n in names) _client.Items.Add(n);
            selected = names.FirstOrDefault(n => n == selected) ?? names.FirstOrDefault();
            _client.SelectedItem = selected;
        }
        else if (selected == null && names.Count > 0) _client.SelectedIndex = 0;

        if (_client.SelectedItem is not string who) { _probeText.Text = "실행 중인 클라이언트가 없습니다."; _image.Source = null; _overlay.Children.Clear(); return; }
        var probe = _svc.Probe(who);
        if (probe == null) return;

        var lines = new List<string> { probe.Status };
        if (probe.State is { } st) lines.Add($"판정: 디스럽터 {(st.Disrupt ? "●" : "○")}  스크램블 {(st.Scram ? "●" : "○")}  HIC 포인팅 {(st.Hic ? "●" : "○")}");
        foreach (var i in probe.Icons)
            lines.Add($"  원 ({i.X},{i.Y}) 반지름 {i.Radius} · 태클 모양 {i.Shape * 100:0}% · 고리 채도 {i.Ring} → " + (i.Kind switch { TackleKind.Disrupt => "디스럽터", TackleKind.Scram => "스크램블", TackleKind.Hic => "HIC 포인팅", _ => "다른 상태이상(무시)" }));
        _probeText.Text = string.Join("\n", lines);

        if (probe.Frame is { } f)
        {
            _image.Source = BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Bgr32, null, f.Bgra, f.Width * 4);
            _overlay.Width = f.Width; _overlay.Height = f.Height;
            _overlay.Children.Clear();
            foreach (var i in probe.Icons)
            {
                var color = i.Kind != null ? Color.FromRgb(0x2E, 0xE5, 0x7A) : Color.FromRgb(0x9A, 0x9A, 0x9A);
                var ring = new Ellipse { Width = i.Radius * 2 + 4, Height = i.Radius * 2 + 4, Stroke = new SolidColorBrush(color), StrokeThickness = 2 };
                Canvas.SetLeft(ring, i.X - i.Radius - 2); Canvas.SetTop(ring, i.Y - i.Radius - 2);
                _overlay.Children.Add(ring);
            }
        }
        else { _image.Source = null; _overlay.Children.Clear(); }
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
