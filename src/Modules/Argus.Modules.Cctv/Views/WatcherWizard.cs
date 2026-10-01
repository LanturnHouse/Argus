using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Argus.Modules.Cctv;

/// <summary>감시 재시작 창의 결과.</summary>
internal enum ResumeOutcome { Cancelled, Back, Started }

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

    private readonly bool _resume;              // 감시 재시작 모드: 캐릭터는 바꿀 수 없고, 저장 대신 그 지점부터 감시를 다시 시작한다
    private readonly ImageRow? _startImage;     // 재시작 지점의 스크린샷 (null: 지금 이후 → 가장 최근 스크린샷 위에 그린다)
    private readonly TextBlock _regionStatus = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private ResumeOutcome _outcome = ResumeOutcome.Cancelled;
    private Button? _saveButton, _backButton;

    public static void ShowFor(Window? owner, CctvService svc, Watcher? editing) => new WatcherWizard(owner, svc, editing, null, false)._window.ShowDialog();

    /// <summary>일시중지한 감시를 다시 시작하는 창 (눈깔 수정과 같은 창, 캐릭터 고정). 재시작하면 Started, '이전'이면 Back.</summary>
    internal static ResumeOutcome ShowResume(Window? owner, CctvService svc, Watcher watcher, ImageRow? startImage)
    {
        var wizard = new WatcherWizard(owner, svc, watcher, startImage, true);
        wizard._window.ShowDialog();
        return wizard._outcome;
    }

    private WatcherWizard(Window? owner, CctvService svc, Watcher? editing, ImageRow? startImage, bool resume)
    {
        _svc = svc; _editing = editing; _resume = resume; _startImage = startImage;
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

        var save = UiKit.Button(resume ? "감시 시작" : "설정 저장", () => { if (_resume) _ = ResumeAsync(); else OnSave(); }, "PrimaryButton"); save.Margin = new Thickness(0);
        _saveButton = save;
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (resume)
        {
            _backButton = UiKit.Button("←  이전", () => { _outcome = ResumeOutcome.Back; _window.Close(); }, "GhostButton");
            _backButton.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(_backButton);
        }
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Right);
        var messages = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (resume) messages.Children.Add(_regionStatus);
        messages.Children.Add(_error);
        footer.Children.Add(buttons); footer.Children.Add(messages);

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        var head = new StackPanel();
        head.Children.Add(UiKit.Text(resume ? "감시 재시작" : editing == null ? "감시 눈깔 등록" : "감시 눈깔 수정", 18, FontWeights.Bold));
        head.Children.Add(new Border { Height = 12 });
        head.Children.Add(top);
        head.Children.Add(regionHead);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_regionList, Dock.Bottom);
        DockPanel.SetDock(_help, Dock.Bottom);
        _regionList.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(head); root.Children.Add(footer); root.Children.Add(_regionList); root.Children.Add(_help); root.Children.Add(_editor);

        _window = DialogKit.Create(owner, resume ? "감시 재시작" : editing == null ? "감시 눈깔 등록" : "감시 눈깔 수정", 1040, 800, root);
        _editor.Changed += RefreshRegionList;
        FillCharacters(editing?.Character);
        if (resume) _character.IsEnabled = false;   // 캐릭터는 바꿀 수 없다
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
        if (_resume)
        {
            // 재시작 지점의 스크린샷(없으면 가장 최근 스크린샷) 위에서 영역을 확인한다.
            var path = _startImage?.FilePath ?? (name.Length > 0 && _svc.LatestImageId(name) is { } li ? _svc.Store.ImagePath(li) : null);
            _editor.SetImage(DialogKit.LoadImage(path));
            _help.Text = _startImage != null
                ? $"{_startImage.CapturedAt.Substring(11, 12)} 스크린샷부터 다시 시작합니다. 이전에 지정한 영역이 그대로 선택돼 있습니다 — 그대로 시작하거나, 지우고 다시 그리면 이 지점부터 새 영역으로 분석합니다."
                : "지금 이후에 촬영되는 스크린샷부터 다시 시작합니다 (화면은 가장 최근 스크린샷). 이전에 지정한 영역이 그대로 선택돼 있습니다 — 그대로 시작하거나, 지우고 다시 그리면 이 지점부터 새 영역으로 분석합니다.";
            return;
        }
        var id = name.Length > 0 ? _svc.LatestImageId(name) : null;
        var latest = _known.FirstOrDefault(k => k.Name == name).Latest;
        _editor.SetImage(id is { } i ? DialogKit.LoadImage(_svc.Store.ImagePath(i)) : null);
        _help.Text = id != null
            ? $"{name} 의 가장 최근 스크린샷을 사용합니다{(latest?.Length >= 19 ? $" ({latest.Substring(11, 8)})" : "")}. 영역 종류를 고른 뒤 스크린샷 위를 끌어 그립니다."
            : _known.Count == 0 ? "스크린샷이 있는 캐릭터가 없습니다."
            : "이 캐릭터의 스크린샷을 폴더에서 찾을 수 없어 영역을 그릴 수 없습니다.";
    }

    private void OnSave()
    {
        _error.Text = "";
        if (CurrentCharacter.Length == 0) { _error.Text = "캐릭터를 선택해주세요."; return; }
        if (string.IsNullOrWhiteSpace(_label.Text)) { _error.Text = "감지 이름을 입력해주세요."; return; }
        if (_editor.Regions.Count == 0) { _error.Text = "인식 영역을 하나 이상 지정해주세요."; return; }
        if (_editing != null && (_type != _editing.WatchType || CurrentCharacter != _editing.Character || !CctvStore.SameRegions(_editor.Regions, _editing.Regions)))
        {
            var answer = MessageBox.Show("영역(또는 감시 타입 · 캐릭터)을 바꿔 저장하면 이 캐릭터의 분석 결과가 모두 지워지고 폴더의 스크린샷을 처음부터 다시 분석합니다.\n\n중간에 화면 위치만 바뀐 경우라면 취소하고, 눈깔 목록의 '일시중지 → 재시작'을 쓰세요. 앞의 분석 결과를 그대로 두고 새 영역으로 이어서 분석합니다.\n\n처음부터 다시 분석할까요?",
                "처음부터 다시 분석", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        var id = _editing?.Id ?? $"watcher-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
        _svc.SaveWatcher(new Watcher(id, _label.Text.Trim(), CurrentCharacter, _type, true, 2, [.. _editor.Regions]));
        _window.Close();
    }

    /// <summary>재시작 모드의 '감시 시작': 이름 · 타입 · 영역을 반영해 그 지점부터 감시를 다시 시작한다. 앞의 분석 결과는 그대로 둔다.</summary>
    private async Task ResumeAsync()
    {
        _error.Text = "";
        if (string.IsNullOrWhiteSpace(_label.Text)) { _error.Text = "감지 이름을 입력해주세요."; return; }
        if (_editor.Regions.Count == 0) { _error.Text = "인식 영역을 하나 이상 지정해주세요."; return; }
        if (_type != _editing!.WatchType && _svc.Store.HasProcessedFrom(_editing.Character, _startImage?.CaptureKey ?? CctvStore.KeyOf(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))))
        {
            _error.Text = "이미 분석한 스크린샷부터 다시 시작할 때는 감시 타입을 바꿀 수 없습니다 (앞의 결과를 새 타입으로 다시 해석하게 됩니다). 분석하지 않은 스크린샷이나 '지금 이후'부터 시작하세요.";
            return;
        }
        _saveButton!.IsEnabled = false; _backButton!.IsEnabled = false;
        try
        {
            await _svc.ResumeWatchingAsync(new Watcher(_editing.Id, _label.Text.Trim(), _editing.Character, _type, true, 2, [.. _editor.Regions]), _startImage, [.. _editor.Regions]);
            _outcome = ResumeOutcome.Started;
            _window.Close();
        }
        catch (Exception ex) { _error.Text = "재시작하지 못했습니다: " + ex.Message; _saveButton.IsEnabled = true; _backButton.IsEnabled = true; }
    }

    private void RefreshRegionList()
    {
        if (_resume && _editing != null)
        {
            var same = CctvStore.SameRegions(_editor.Regions, _editing.Regions);
            _regionStatus.Text = same ? "인식 영역: 이전 영역 그대로" : "인식 영역: 새 영역으로 변경됨";
            _regionStatus.Foreground = same ? UiKit.Good : UiKit.Warn;
        }
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
