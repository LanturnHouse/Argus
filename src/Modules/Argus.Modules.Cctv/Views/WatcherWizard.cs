using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Argus.Modules.Cctv;

/// <summary>스크린샷 위에 인식 영역(오버뷰 · 프로빙 창 · 도킹 숫자)을 마우스로 끌어서 그리는 편집기. 좌표는 이미지에 대한 퍼센트로 다룬다.</summary>
internal sealed class RegionEditorControl : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private BitmapImage? _bitmap;
    private Point? _start;
    private RegionDef? _draft;

    public List<RegionDef> Regions { get; } = [];
    public RegionKind CurrentKind { get; set; } = RegionKind.Overview;
    public event Action? Changed;

    public RegionEditorControl()
    {
        Background = Brushes.Black;
        MinHeight = 260;
        Children.Add(_image); Children.Add(_canvas);
        SizeChanged += (_, _) => Redraw();
        _canvas.MouseLeftButtonDown += OnDown; _canvas.MouseMove += OnMove; _canvas.MouseLeftButtonUp += OnUp;
    }

    public void SetImage(BitmapImage? bitmap) { _bitmap = bitmap; _image.Source = bitmap; Redraw(); }

    public static string ColorOf(RegionKind k) => k switch { RegionKind.Overview => "#65A9FF", RegionKind.Probe => "#9B8CFF", _ => "#FF78B9" };

    /// <summary>이미지가 실제로 그려지는 영역(Uniform 으로 줄어든 크기와 위치).</summary>
    private (double X, double Y, double W, double H)? Content()
    {
        if (_bitmap == null || ActualWidth <= 0 || ActualHeight <= 0) return null;
        var scale = Math.Min(ActualWidth / _bitmap.PixelWidth, ActualHeight / _bitmap.PixelHeight);
        double w = _bitmap.PixelWidth * scale, h = _bitmap.PixelHeight * scale;
        return ((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }

    private Point ToPercent(Point p)
    {
        var c = Content()!.Value;
        return new Point(Math.Clamp((p.X - c.X) / c.W * 100, 0, 100), Math.Clamp((p.Y - c.Y) / c.H * 100, 0, 100));
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (Content() == null) return;
        _canvas.CaptureMouse();
        _start = ToPercent(e.GetPosition(_canvas));
        _draft = new RegionDef(CurrentKind, _start.Value.X, _start.Value.Y, 0, 0);
        Redraw();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } s || Content() == null) return;
        var p = ToPercent(e.GetPosition(_canvas));
        _draft = new RegionDef(CurrentKind, Math.Min(s.X, p.X), Math.Min(s.Y, p.Y), Math.Abs(p.X - s.X), Math.Abs(p.Y - s.Y));
        Redraw();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        if (_draft is { W: > 0.5, H: > 0.5 } d) { Regions.Add(d); Changed?.Invoke(); }   // 너무 작은 드래그는 무시
        _draft = null; _start = null;
        Redraw();
    }

    public void Redraw()
    {
        _canvas.Children.Clear();
        if (Content() is not { } c) return;
        foreach (var r in _draft != null ? [.. Regions, _draft] : Regions)
        {
            var color = UiKit.Hex(ColorOf(r.Kind));
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, c.W * r.W / 100), Height = Math.Max(1, c.H * r.H / 100), Stroke = color, StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0x26, ((SolidColorBrush)color).Color.R, ((SolidColorBrush)color).Color.G, ((SolidColorBrush)color).Color.B)), IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, c.X + c.W * r.X / 100); Canvas.SetTop(rect, c.Y + c.H * r.Y / 100);
            _canvas.Children.Add(rect);
        }
    }
}

/// <summary>감시 눈깔 등록/수정: 한 화면에서 캐릭터 · 이름 · 감시 타입을 정하고, 스크린샷 위에 인식 영역을 그린다.</summary>
internal sealed class WatcherWizard
{
    private readonly CctvService _svc;
    private readonly Watcher? _editing;
    private readonly Window _window;
    private readonly RegionEditorControl _editor = new();
    private readonly ComboBox _character = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _label = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _regionList = new();
    private readonly TextBlock _error = new() { Foreground = UiKit.Bad, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _help = new() { FontSize = 12, Foreground = UiKit.DimBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _typeDesc = new() { FontSize = 12, Foreground = UiKit.DimBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly WrapPanel _kindBar = new();
    private readonly List<(string Name, int Images, string Latest)> _known;
    private WatchType _type = WatchType.Structure;
    private bool _filling;

    public static void ShowFor(Window? owner, CctvService svc, Watcher? editing) => new WatcherWizard(owner, svc, editing)._window.ShowDialog();

    private WatcherWizard(Window? owner, CctvService svc, Watcher? editing)
    {
        _svc = svc; _editing = editing;
        _known = svc.KnownCharacters();
        if (editing != null && !_known.Any(k => k.Name == editing.Character)) _known.Add((editing.Character, 0, ""));
        if (editing != null) { _label.Text = editing.Label; _type = editing.WatchType; _editor.Regions.AddRange(editing.Regions); }

        // 위: 캐릭터 · 이름 · 타입 (한 줄)
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var c0 = Field("캐릭터", _character); c0.Margin = new Thickness(0, 0, 16, 0);
        var c1 = Field("감지 이름 (예: 1번 스트럭쳐, 동쪽 웜홀)", _label); c1.Margin = new Thickness(0, 0, 16, 0);
        var types = new WrapPanel();
        var structure = new FilterChip("스트럭쳐 감시", _type == WatchType.Structure); var gate = new FilterChip("웜홀 / 게이트 감시", _type == WatchType.Gate);
        void ApplyType() { structure.SetActive(_type == WatchType.Structure); gate.SetActive(_type == WatchType.Gate); _typeDesc.Text = _type == WatchType.Structure ? "도킹 카운터와 오버뷰 이탈을 함께 비교해 도킹 여부를 판정합니다." : "웜홀·게이트 랜딩 후 오버뷰 이탈을 점프아웃으로, 신규 출현을 점프인으로 판정합니다."; }
        structure.Toggled += _ => { _type = WatchType.Structure; ApplyType(); }; gate.Toggled += _ => { _type = WatchType.Gate; ApplyType(); };
        types.Children.Add(structure); types.Children.Add(gate);
        var typeBox = new StackPanel(); typeBox.Children.Add(types); typeBox.Children.Add(_typeDesc);
        var c2 = Field("감시 타입", typeBox);
        Grid.SetColumn(c0, 0); Grid.SetColumn(c1, 1); Grid.SetColumn(c2, 2);
        top.Children.Add(c0); top.Children.Add(c1); top.Children.Add(c2);
        ApplyType();

        // 영역 종류 고르기
        foreach (var kind in new[] { RegionKind.Overview, RegionKind.Probe, RegionKind.Dock })
        {
            var chip = new FilterChip(kind.Label(), _editor.CurrentKind == kind, kind switch { RegionKind.Overview => "함선 행의 이름·종류·코퍼레이션·속도가 모두 들어오게", RegionKind.Probe => "ID·이름 헤더부터 시그니처 행까지", _ => "[0] 처럼 도킹 숫자가 보이는 작은 영역" });
            var captured = kind;
            chip.Toggled += _ => { _editor.CurrentKind = captured; foreach (var c in _kindBar.Children.OfType<FilterChip>().Take(3)) c.SetActive(false); chip.SetActive(true); };
            _kindBar.Children.Add(chip);
        }
        var clear = new FilterChip("모두 지우기", false);
        clear.Toggled += _ => { _editor.Regions.Clear(); _editor.Redraw(); RefreshRegionList(); clear.SetActive(false); };
        _kindBar.Children.Add(clear);
        var regionHead = new StackPanel { Margin = new Thickness(0, 14, 0, 6) };
        regionHead.Children.Add(UiKit.Text("인식 영역", 13, FontWeights.SemiBold, null, new Thickness(0, 0, 0, 6)));
        regionHead.Children.Add(_kindBar);

        var save = UiKit.Button("설정 저장", OnSave, "PrimaryButton"); save.Margin = new Thickness(0);
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(save); footer.Children.Add(_error);

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        var head = new StackPanel();
        head.Children.Add(UiKit.Text(editing == null ? "감시 눈깔 등록" : "감시 눈깔 수정", 18, FontWeights.Bold));
        head.Children.Add(UiKit.Dim("캐릭터가 보는 화면에서 오버뷰 · 프로빙 창 · 도킹 숫자 영역을 지정합니다.", 12, new Thickness(0, 2, 0, 14)));
        head.Children.Add(top);
        head.Children.Add(regionHead);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_regionList, Dock.Bottom);
        DockPanel.SetDock(_help, Dock.Bottom);
        _regionList.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(head); root.Children.Add(footer); root.Children.Add(_regionList); root.Children.Add(_help); root.Children.Add(_editor);

        _window = DialogKit.Create(owner, editing == null ? "감시 눈깔 등록" : "감시 눈깔 수정", 1040, 800, root);
        _editor.Changed += RefreshRegionList;
        FillCharacters(editing?.Character);
        _character.SelectionChanged += (_, _) => { if (!_filling) LoadImage(); };
        LoadImage();
        RefreshRegionList();
    }

    private static FrameworkElement Field(string label, UIElement control)
    {
        var sp = new StackPanel();
        sp.Children.Add(UiKit.Dim(label, 11.5, new Thickness(0, 0, 0, 4), false));
        sp.Children.Add(control);
        return sp;
    }

    private void FillCharacters(string? select)
    {
        _filling = true;
        _character.Items.Clear();
        foreach (var (name, images, _) in _known) _character.Items.Add(images > 0 ? $"{name} · 스크린샷 {images}장" : $"{name} · 스크린샷 없음");
        var index = select != null ? _known.FindIndex(k => k.Name == select) : 0;
        _character.SelectedIndex = Math.Max(0, index);
        _filling = false;
    }

    private string CurrentCharacter => _character.SelectedIndex >= 0 && _character.SelectedIndex < _known.Count ? _known[_character.SelectedIndex].Name : "";

    private void LoadImage()
    {
        var name = CurrentCharacter;
        var id = name.Length > 0 ? _svc.LatestImageId(name) : null;
        var latest = _known.FirstOrDefault(k => k.Name == name).Latest;
        _editor.SetImage(id is { } i ? DialogKit.LoadImage(_svc.Store.ImagePath(i)) : null);
        _help.Text = id != null
            ? $"{name} 의 가장 최근 스크린샷을 사용합니다{(latest?.Length >= 19 ? $" ({latest.Substring(11, 8)})" : "")}. 영역 종류를 고른 뒤 스크린샷 위를 마우스로 끌어 그립니다. 화면 감시 캡처가 '선택 영역'만 저장한 것이면 그 범위 안에서 지정합니다."
            : name.Length == 0 ? "캐릭터를 선택하세요." : "이 캐릭터의 스크린샷이 아직 없어 영역을 그릴 수 없습니다. 화면 감시 캡처가 스크린샷을 저장하면 다시 열어 주세요.";
    }

    private void OnSave()
    {
        _error.Text = "";
        if (CurrentCharacter.Length == 0) { _error.Text = "캐릭터를 선택해주세요."; return; }
        if (string.IsNullOrWhiteSpace(_label.Text)) { _error.Text = "감지 이름을 입력해주세요."; return; }
        if (_editor.Regions.Count == 0) { _error.Text = "인식 영역을 하나 이상 지정해주세요. 스크린샷 위에서 마우스로 끌어 영역을 그립니다."; return; }
        var id = _editing?.Id ?? $"watcher-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
        _svc.SaveWatcher(new Watcher(id, _label.Text.Trim(), CurrentCharacter, _type, true, 2, [.. _editor.Regions]));
        _window.Close();
    }

    private void RefreshRegionList()
    {
        _regionList.Children.Clear();
        for (int i = 0; i < _editor.Regions.Count; i++)
        {
            var r = _editor.Regions[i]; var index = i;
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var del = UiKit.Button("삭제", () => { _editor.Regions.RemoveAt(index); _editor.Redraw(); RefreshRegionList(); }, "GhostButton"); del.Padding = new Thickness(8, 1, 8, 1); del.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = UiKit.Hex(RegionEditorControl.ColorOf(r.Kind)), Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(UiKit.Dim($"{r.Kind.Label()} · 왼쪽 {r.X:0.#}% 위 {r.Y:0.#}% · 크기 {r.W:0.#}×{r.H:0.#}%", 12, null, false));
            _regionList.Children.Add(row);
        }
    }
}
