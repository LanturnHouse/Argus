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

/// <summary>감시 눈깔 등록/수정: 1) 캐릭터 2) 이름과 감시 타입 3) 인식 영역 지정.</summary>
internal sealed class WatcherWizard
{
    private readonly CctvService _svc;
    private readonly Watcher? _editing;
    private readonly Window _window;
    private readonly ContentControl _body = new();
    private readonly Button _back, _next;
    private readonly StackPanel _steps = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock _error = new() { Foreground = UiKit.Bad, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly RegionEditorControl _editor = new();
    private readonly TextBox _label = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _regionList = new();
    private int _step;
    private string _character = "";
    private WatchType _type = WatchType.Structure;

    public static void ShowFor(Window? owner, CctvService svc, Watcher? editing) => new WatcherWizard(owner, svc, editing)._window.ShowDialog();

    private WatcherWizard(Window? owner, CctvService svc, Watcher? editing)
    {
        _svc = svc; _editing = editing;
        if (editing != null) { _character = editing.Character; _label.Text = editing.Label; _type = editing.WatchType; _editor.Regions.AddRange(editing.Regions); _step = 2; }
        else { _character = svc.KnownCharacters().FirstOrDefault().Name ?? ""; }

        _back = UiKit.Button("이전", () => Go(_step - 1));
        _next = UiKit.Button("다음", OnNext, "PrimaryButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_back); buttons.Children.Add(_next);

        var root = new DockPanel { Margin = new Thickness(22) };
        var head = new StackPanel();
        head.Children.Add(UiKit.Text(editing == null ? "감시 눈깔 등록" : "감시 눈깔 수정", 18, FontWeights.Bold));
        head.Children.Add(UiKit.Dim("캐릭터, 감지 위치, 감시 타입과 인식 영역을 설정합니다.", 12, new Thickness(0, 2, 0, 14)));
        head.Children.Add(_steps);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_error, Dock.Bottom);
        root.Children.Add(head); root.Children.Add(buttons); root.Children.Add(_error); root.Children.Add(_body);

        _window = DialogKit.Create(owner, editing == null ? "감시 눈깔 등록" : "감시 눈깔 수정", 940, 780, root);
        _editor.Changed += RefreshRegionList;
        Go(_step);
    }

    private void Go(int step)
    {
        _error.Text = "";
        _step = Math.Clamp(step, 0, 2);
        _steps.Children.Clear();
        for (int i = 0; i < 3; i++) _steps.Children.Add(new Border { Width = 52, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), Background = i <= _step ? UiKit.Accent : UiKit.Panel3 });
        _back.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Content = _step < 2 ? "다음" : "설정 저장";
        _body.Content = _step switch { 0 => CharacterStep(), 1 => InfoStep(), _ => RegionStep() };
    }

    private void OnNext()
    {
        _error.Text = "";
        if (_step == 0 && _character.Length == 0) { _error.Text = "캐릭터를 선택해주세요."; return; }
        if (_step == 1 && string.IsNullOrWhiteSpace(_label.Text)) { _error.Text = "감지 이름을 입력해주세요."; return; }
        if (_step < 2) { Go(_step + 1); return; }
        if (_editor.Regions.Count == 0) { _error.Text = "인식 영역을 하나 이상 지정해주세요. 스크린샷 위에서 마우스로 끌어 영역을 그립니다."; return; }

        var id = _editing?.Id ?? $"watcher-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
        _svc.SaveWatcher(new Watcher(id, _label.Text.Trim(), _character, _type, true, 2, [.. _editor.Regions]));
        _window.Close();
    }

    // ---------- 1) 캐릭터 ----------

    private UIElement CharacterStep()
    {
        var box = new StackPanel();
        box.Children.Add(UiKit.Text("감시에 사용할 캐릭터", 15, FontWeights.SemiBold));
        box.Children.Add(UiKit.Dim("CCTV 폴더의 파일 이름에서 찾은 캐릭터와 지금 실행 중인 클라이언트입니다.", 12, new Thickness(0, 2, 0, 12)));
        var known = _svc.KnownCharacters();
        if (known.Count == 0) box.Children.Add(UiKit.Dim("발견된 캐릭터가 없습니다. 화면 감시 캡처가 스크린샷을 저장하면 여기에 나타납니다.", 12));
        foreach (var (name, images, latest) in known)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(UiKit.Text(name, 14, FontWeights.SemiBold));
            info.Children.Add(UiKit.Dim(images > 0 ? $"스크린샷 {images}장 · 최근 {(latest.Length >= 19 ? latest.Substring(11, 8) : "미확인")}" : "스크린샷 없음 (실행 중인 클라이언트)", 12));
            var mark = UiKit.Text(name == _character ? "●" : "○", 16, FontWeights.Bold, name == _character ? UiKit.Accent : UiKit.DimBrush); Grid.SetColumn(mark, 1);
            g.Children.Add(info); g.Children.Add(mark);
            var row = new RowButton(g) { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 6), Background = name == _character ? UiKit.AccentSoft : UiKit.Panel2 };
            row.MouseEnter += (_, _) => row.Background = name == _character ? UiKit.AccentSoft : UiKit.Panel3;
            row.MouseLeave += (_, _) => row.Background = name == _character ? UiKit.AccentSoft : UiKit.Panel2;
            var captured = name;
            row.Clicked += () => { _character = captured; Go(0); };
            box.Children.Add(row);
        }
        return new ScrollViewer { Content = box, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ---------- 2) 이름과 타입 ----------

    private UIElement InfoStep()
    {
        var box = new StackPanel();
        box.Children.Add(UiKit.Text("감시 위치 정보", 15, FontWeights.SemiBold));
        box.Children.Add(UiKit.Dim($"선택된 캐릭터: {_character}", 12, new Thickness(0, 2, 0, 14)));
        box.Children.Add(UiKit.Section("감지 이름", "타임라인과 요약에 이 이름으로 표시됩니다. 예: 1번 스트럭쳐, 동쪽 웜홀", _label));

        var types = new WrapPanel();
        var structure = new FilterChip("스트럭쳐 감시", _type == WatchType.Structure); var gate = new FilterChip("웜홀 / 게이트 감시", _type == WatchType.Gate);
        var desc = UiKit.Dim("", 12, new Thickness(0, 8, 0, 0));
        void Apply() { structure.SetActive(_type == WatchType.Structure); gate.SetActive(_type == WatchType.Gate); desc.Text = _type == WatchType.Structure ? "도킹 카운터와 오버뷰 이탈을 함께 비교해 도킹 여부를 판정합니다." : "웜홀·게이트 랜딩 후 오버뷰 이탈을 점프아웃으로, 신규 출현을 점프인으로 판정합니다."; }
        structure.Toggled += _ => { _type = WatchType.Structure; Apply(); }; gate.Toggled += _ => { _type = WatchType.Gate; Apply(); };
        types.Children.Add(structure); types.Children.Add(gate);
        Apply();
        var typeBox = new StackPanel(); typeBox.Children.Add(types); typeBox.Children.Add(desc);
        box.Children.Add(UiKit.Section("감시 타입", "", typeBox));
        return box;
    }

    // ---------- 3) 인식 영역 ----------

    private UIElement RegionStep()
    {
        var stat = _svc.KnownCharacters().FirstOrDefault(c => c.Name == _character);
        var latestId = _svc.LatestImageId(_character);
        _editor.SetImage(latestId is { } id ? DialogKit.LoadImage(_svc.Store.ImagePath(id)) : null);

        var box = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(UiKit.Text("인식 영역 지정", 15, FontWeights.SemiBold));
        top.Children.Add(UiKit.Dim($"{(_label.Text.Length > 0 ? _label.Text + " · " : "")}{_character}", 12, new Thickness(0, 2, 0, 8)));
        var bar = new WrapPanel();
        foreach (var kind in new[] { RegionKind.Overview, RegionKind.Probe, RegionKind.Dock })
        {
            var chip = new FilterChip(kind.Label(), _editor.CurrentKind == kind, kind switch { RegionKind.Overview => "함선 행의 이름·종류·코퍼레이션·속도가 모두 들어오게", RegionKind.Probe => "ID·이름 헤더부터 시그니처 행까지", _ => "[0] 처럼 도킹 숫자가 보이는 작은 영역" });
            var captured = kind;
            chip.Toggled += _ => { _editor.CurrentKind = captured; Go(2); };
            bar.Children.Add(chip);
        }
        var clear = new FilterChip("모두 지우기", false); clear.Toggled += _ => { _editor.Regions.Clear(); _editor.Redraw(); RefreshRegionList(); clear.SetActive(false); };
        bar.Children.Add(clear);
        top.Children.Add(bar);
        DockPanel.SetDock(top, Dock.Top);
        box.Children.Add(top);

        var help = UiKit.Dim(latestId != null
            ? $"{_character} 의 가장 최근 스크린샷을 사용합니다{(stat.Latest?.Length >= 19 ? $" ({stat.Latest.Substring(11, 8)})" : "")}. 영역을 마우스로 끌어 그립니다. 종류 버튼으로 오버뷰 · 프로빙 창 · 도킹 숫자를 바꿔 가며 지정하세요. 스크린샷이 화면 감시 캡처의 '선택 영역'만 저장한 것이면 그 범위 안에서 지정합니다."
            : "이 캐릭터의 스크린샷이 아직 없어 영역을 그릴 수 없습니다. 화면 감시 캡처를 켜 스크린샷이 저장되면 다시 열어 주세요.", 12, new Thickness(0, 8, 0, 0));
        DockPanel.SetDock(help, Dock.Bottom);
        box.Children.Add(help);
        DockPanel.SetDock(_regionList, Dock.Bottom);
        _regionList.Margin = new Thickness(0, 8, 0, 0);
        box.Children.Add(_regionList);
        box.Children.Add(_editor);
        RefreshRegionList();
        return box;
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
