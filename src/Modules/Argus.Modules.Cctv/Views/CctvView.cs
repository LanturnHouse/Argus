using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Argus.Modules.Cctv;

/// <summary>사이드바 'CCTV' 탭: 분석 켜기/끄기와 상태, 요약, 감시 눈깔, 감지 타임라인, 코퍼레이션 현황, 프로빙 변화.</summary>
internal sealed class CctvView : UserControl
{
    private readonly CctvService _svc;
    private readonly Border _analysisHost = new(), _warningHost = new(), _statsHost = new(), _watchersHost = new(), _corpHost = new(), _sigHost = new();
    private readonly StackPanel _timelineList = new();
    private readonly TextBox _search = new() { Width = 240, ToolTip = "캐릭터 · 함선 · 콥 티커 검색" };
    private readonly TextBlock _searchHint = new() { Text = "캐릭터 · 함선 · 콥 티커 검색", FontSize = 12, Foreground = UiKit.DimBrush, IsHitTestVisible = false, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _categoryRow = new();
    private readonly ComboBox _watcherCombo = new() { Width = 150, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private bool _fillingWatchers;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly HashSet<EventCategory> _categories = [.. Enum.GetValues<EventCategory>()];
    private string _watcherFilter = "";
    private bool _exact;
    private const int PageSize = 20;
    private int _page;
    private string _signature = "\0";
    private bool _refreshQueued;
    private (string, long) _lastStamp;
    private ViewData? _data;

    private sealed record ViewData(CctvStatus Status, List<Watcher> Watchers, List<EventRow> Events, List<CurrentObject> Objects, List<CurrentSignature> Signatures,
        List<DockPeak> DockPeaks, List<RegionWarning> Warnings, Dictionary<string, LatestState> Latest, List<CorpGroup> Corps, Func<string?, string> Canonical);

    public CctvView(CctvService svc)
    {
        _svc = svc;
        var root = new StackPanel();
        root.Children.Add(UiKit.Text("분석", 24, FontWeights.Bold));
        root.Children.Add(UiKit.Dim("스크린샷의 오버뷰 · 프로빙 창 · 도킹 숫자를 비전 모델로 읽어 출입 · 도킹 · 시그니처 변화를 판정합니다.", 12, new Thickness(0, 4, 0, 16)));
        root.Children.Add(_analysisHost);
        root.Children.Add(BuildAnalysisSettingsCard());
        root.Children.Add(_warningHost);
        root.Children.Add(_statsHost);
        root.Children.Add(_watchersHost);
        root.Children.Add(BuildTimelineCard());

        var two = new Grid();
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MinWidth = 380 });
        Grid.SetColumn(_corpHost, 0); Grid.SetColumn(_sigHost, 2);
        two.Children.Add(_corpHost); two.Children.Add(_sigHost);
        root.Children.Add(two);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root, Padding = new Thickness(0, 0, 12, 0) };

        _search.TextChanged += (_, _) => { _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; FilterChanged(); };
        _svc.Changed += OnServiceChanged;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(force: true); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnServiceChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _refreshQueued = false; Refresh(); });
    }

    // ---------- 데이터 모으기 ----------

    private ViewData Collect()
    {
        var store = _svc.Store;
        var events = store.Events(_svc.Settings.TimelineLimit);
        var objects = store.CurrentObjects();
        var canonical = Summaries.CorporationCanonicalizer(events, objects);
        return new ViewData(_svc.Status(), store.ListWatchers(), events, objects, store.CurrentSignatures(), store.DockPeaks(), store.RegionWarnings(),
            Summaries.BuildLatestStates(events, objects, canonical), Summaries.BuildCorpGroups(events, objects, canonical), canonical);
    }

    private void Refresh(bool force = false)
    {
        // 분석 상태도 저장소도 그대로면 아무것도 다시 읽지 않는다 (초당 한 번 도는 타이머가 공짜가 되도록).
        var stamp = (_svc.ChangeStamp(), _svc.Store.Db.WriteCount);
        if (!force && stamp == _lastStamp) return;
        _lastStamp = stamp;
        ViewData d;
        try { d = Collect(); } catch (Exception ex) { Trace.WriteLine($"[CCTV] 화면 갱신 실패: {ex.Message}"); return; }

        var s = d.Status;
        var sig = string.Join("\u0002", s.State, s.Message, s.IsError, s.Folder, s.ImageCount, s.Counts, s.Processing, s.Model, s.ModelCalls, s.ReusedCalls,
            string.Join("|", d.Watchers.Select(w => $"{w.Id}:{w.Label}:{w.Character}:{w.WatchType}:{w.RegionVersion}:{w.Regions.Count}")),
            d.Events.Count, d.Events.FirstOrDefault()?.Id, string.Join(",", d.Events.Take(30).Select(e => $"{e.Id}{e.Type}{e.Ship}{EventPresentation.Verification(e)}")),
            string.Join("|", d.Objects.Select(o => $"{o.Character}{o.LastSeenAt}")), string.Join("|", d.Signatures.Select(x => $"{x.Id}{x.Name}{x.Group}")),
            string.Join("|", d.DockPeaks.Select(p => $"{p.WatcherId}{p.PeakCount}")), string.Join("|", d.Warnings.Select(w => $"{w.WatcherId}{w.Kind}")));
        if (!force && sig == _signature) return;
        _signature = sig; _data = d;

        _analysisHost.Child = BuildAnalysisCard(d);
        _warningHost.Child = BuildWarnings(d);
        _statsHost.Child = BuildStats(d);
        _watchersHost.Child = BuildWatchers(d);
        RebuildFilters(d);
        RebuildTimeline();
        _corpHost.Child = BuildCorps(d);
        _sigHost.Child = BuildSignatures(d);
    }

    // ---------- 분석 상태 ----------

    private UIElement BuildAnalysisCard(ViewData d)
    {
        var s = d.Status;
        var (label, bg, fg) = s.State switch
        {
            AnalysisState.Working => ("분석 중", UiKit.AccentSoft, UiKit.AccentText),
            AnalysisState.Loading => ("모델 올리는 중…", UiKit.WarnBg, UiKit.Warn),
            AnalysisState.Idle => ("켜짐 · 읽을 이미지 없음", UiKit.GoodBg, UiKit.Good),
            _ => ("분석 꺼짐", UiKit.NeutralBg, UiKit.NeutralText),
        };

        var top = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Counter("대기", s.Counts.Pending + s.Counts.Processing, s.Counts.Pending > 0 ? UiKit.Warn : UiKit.NeutralText));
        right.Children.Add(Counter("완료", s.Counts.Processed, UiKit.Good));
        right.Children.Add(Counter("실패", s.Counts.Failed, s.Counts.Failed > 0 ? UiKit.Bad : UiKit.NeutralText));
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(UiKit.Chip(label, bg, fg, null, new Thickness(0, 0, 12, 0)));
        var toggle = s.State == AnalysisState.Off
            ? UiKit.Button("분석 켜기", () => _ = _svc.EnableAsync(), "PrimaryButton")
            : UiKit.Button("분석 끄기", () => _ = _svc.DisableAsync());
        toggle.IsEnabled = s.State is AnalysisState.Off or AnalysisState.Idle or AnalysisState.Working;
        left.Children.Add(toggle);
        left.Children.Add(UiKit.Dim($"모델 {s.Model}", 12, new Thickness(4, 0, 0, 0), false));
        top.Children.Add(left);

        var body = new StackPanel();
        body.Children.Add(top);
        if (!string.IsNullOrEmpty(s.Message)) body.Children.Add(UiKit.Text(s.Message!, 12, FontWeights.Normal, s.IsError ? UiKit.Bad : UiKit.Warn, new Thickness(0, 10, 0, 0), wrap: true));
        else if (s.State == AnalysisState.Off)
            body.Children.Add(UiKit.Dim("분석이 꺼져 있습니다. 켜면 읽을 이미지가 있을 때만 모델을 올립니다.", 12, new Thickness(0, 10, 0, 0)));
        else if (s.State == AnalysisState.Working && s.Processing != null)
            body.Children.Add(UiKit.Dim($"읽는 중: {s.Processing}", 12, new Thickness(0, 10, 0, 0)));

        var folder = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var open = UiKit.Button("폴더 열기", () => { if (Directory.Exists(s.Folder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{s.Folder}\"") { UseShellExecute = true }); });
        open.Margin = new Thickness(8, 0, 0, 0); open.Padding = new Thickness(10, 3, 10, 3);
        DockPanel.SetDock(open, Dock.Right);
        folder.Children.Add(open);
        folder.Children.Add(UiKit.Dim($"이미지 폴더: {s.Folder} · 이미지 {s.ImageCount:N0}장" + (s.ModelCalls + s.ReusedCalls > 0 ? $" · 모델 호출 {s.ModelCalls:N0}회 (재사용 {s.ReusedCalls:N0}회)" : ""), 12, null, true));
        body.Children.Add(folder);
        return UiKit.Card(body);
    }

    private static UIElement Counter(string label, int value, Brush color)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        sp.Children.Add(UiKit.Dim(label, 12, new Thickness(0, 0, 6, 0), false));
        sp.Children.Add(UiKit.Text(value.ToString("N0"), 16, FontWeights.Bold, color));
        return sp;
    }

    private UIElement BuildWarnings(ViewData d)
    {
        var box = new StackPanel();
        var review = d.Watchers.FirstOrDefault(w => w.RegionVersion < 2);
        var warning = d.Warnings.FirstOrDefault();
        string? title = null, note = null; string? watcherId = null;
        if (review != null) { title = $"{review.Label} 인식 영역을 다시 확인해주세요."; note = "저장하면 이 캐릭터의 이미지를 처음부터 다시 분석합니다."; watcherId = review.Id; }
        else if (warning != null) { title = $"{warning.WatcherLabel} · 영역 재설정 필요"; note = $"{warning.Message} UI 배율이나 창 위치가 바뀌었다면 영역을 다시 지정해주세요."; watcherId = warning.WatcherId; }
        if (title == null) return box;

        var inner = new StackPanel();
        inner.Children.Add(UiKit.Text(title, 13, FontWeights.SemiBold, UiKit.Warn));
        inner.Children.Add(UiKit.Dim(note!, 12, new Thickness(0, 2, 0, 0)));
        var row = new RowButton(inner) { Background = UiKit.WarnBg, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14), CornerRadius = new CornerRadius(10) };
        row.MouseEnter += (_, _) => row.Background = UiKit.WarnBg; row.MouseLeave += (_, _) => row.Background = UiKit.WarnBg;
        row.Clicked += () => EditWatcher(watcherId!);
        return row;
    }

    // ---------- 분석 설정 (비전 모델 · 분석 기록 초기화) ----------

    private CctvSettingsView? _settingsView;
    private bool _settingsOpen;

    private UIElement BuildAnalysisSettingsCard()
    {
        var body = new Border { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        var arrow = UiKit.Text("▾", 13, FontWeights.SemiBold, UiKit.DimBrush);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(UiKit.Text("분석 설정", 13.5, FontWeights.SemiBold));
        title.Children.Add(UiKit.Dim("비전 모델 · 분석 기록 초기화", 12, new Thickness(12, 0, 0, 0), false));
        var head = new DockPanel();
        DockPanel.SetDock(arrow, Dock.Right);
        head.Children.Add(arrow); head.Children.Add(title);
        var toggle = new RowButton(head);
        toggle.Clicked += () =>
        {
            _settingsOpen = !_settingsOpen;
            if (_settingsOpen && _settingsView == null) { _settingsView = new CctvSettingsView(_svc); body.Child = _settingsView; }
            body.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = _settingsOpen ? "▴" : "▾";
        };
        var box = new StackPanel();
        box.Children.Add(toggle); box.Children.Add(body);
        return UiKit.Card(box, new Thickness(8, 6, 8, 8), new Thickness(0, 0, 0, 14));
    }

    // ---------- 요약 ----------

    private UIElement BuildStats(ViewData d)
    {
        int Count(LiveStatus st) => d.Latest.Values.Count(x => x.Status == st);
        var grid = new UniformGrid { Rows = 1, Columns = 3 };
        grid.Children.Add(StatCard("현재 도킹 확인", Count(LiveStatus.Docked), d.DockPeaks.Count > 0 ? (d.DockPeaks.Count > 1 ? $"최고 도킹 수 합계 {d.DockPeaks.Sum(p => p.PeakCount)}명 · 눈깔 {d.DockPeaks.Count}개" : $"최고 도킹 수 {d.DockPeaks[0].PeakCount}명") : "도킹 수 감지 대기", "도킹", d, LiveStatus.Docked, 0));
        grid.Children.Add(StatCard("감지 · 미도킹", Count(LiveStatus.Observed), "위치 미확정", "감지", d, LiveStatus.Observed, 1));
        grid.Children.Add(StatCard("성계 이탈", Count(LiveStatus.Departed), "점프아웃 판정", "이탈", d, LiveStatus.Departed, 2));
        return grid;
    }

    private UIElement StatCard(string title, int value, string note, string kind, ViewData d, LiveStatus status, int index)
    {
        var sp = new StackPanel();
        sp.Children.Add(UiKit.Dim(title, 12, null, false));
        sp.Children.Add(UiKit.Text(value.ToString(), 30, FontWeights.Bold, null, new Thickness(0, 4, 0, 2)));
        sp.Children.Add(UiKit.Dim(note, 12, null, false));
        sp.Children.Add(UiKit.Text("상세 보기 ›", 11.5, FontWeights.SemiBold, UiKit.AccentText, new Thickness(0, 8, 0, 0)));
        var row = new RowButton(sp) { Padding = new Thickness(16, 14, 16, 12), Background = UiKit.Panel, Margin = new Thickness(0, 0, index == 2 ? 0 : 10, 14), CornerRadius = new CornerRadius(12), BorderBrush = UiKit.Line, BorderThickness = new Thickness(1) };
        row.MouseEnter += (_, _) => row.Background = UiKit.Panel2; row.MouseLeave += (_, _) => row.Background = UiKit.Panel;
        row.Clicked += () => SummaryWindow.ShowFor(Window.GetWindow(this), _svc, status, d.Latest.Values.Where(x => x.Status == status).OrderByDescending(x => x.Time).ToList(), d.DockPeaks);
        return row;
    }

    // ---------- 감시 눈깔 ----------

    private UIElement BuildWatchers(ViewData d)
    {
        var box = new StackPanel();
        var head = new DockPanel();
        var add = UiKit.Button("눈깔 추가", () => NewWatcher(), "PrimaryButton");
        add.Margin = new Thickness(0);
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        var titleBox = new StackPanel();
        titleBox.Children.Add(UiKit.SectionHead("감시 눈깔"));
        head.Children.Add(titleBox);
        box.Children.Add(head);

        if (d.Watchers.Count == 0)
        {
            box.Children.Add(UiKit.Dim("등록한 눈깔이 없습니다. '눈깔 추가'로 캐릭터와 인식 영역을 지정하세요.", 12, new Thickness(0, 12, 0, 0)));
            return UiKit.Card(box);
        }

        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var w in d.Watchers)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var remove = UiKit.Button("제거", () => RemoveWatcher(w), "GhostButton"); remove.Margin = new Thickness(6, 0, 0, 0); remove.Padding = new Thickness(10, 4, 10, 4);
            var edit = UiKit.Button("영역 수정", () => EditWatcher(w.Id)); edit.Margin = new Thickness(6, 0, 0, 0); edit.Padding = new Thickness(10, 4, 10, 4);
            edit.ToolTip = "저장하면 이 캐릭터의 분석 결과를 모두 지우고 처음부터 다시 분석합니다. 중간에 위치만 바뀐 경우에는 일시중지 → 재시작을 쓰세요.";
            var pause = w.Paused
                ? UiKit.Button("재시작…", () => ResumeWindow.ShowFor(Window.GetWindow(this), _svc, w), "PrimaryButton")
                : UiKit.Button("일시중지", () => _svc.PauseWatching(w.Character));
            pause.Margin = new Thickness(0); pause.Padding = new Thickness(10, 4, 10, 4);
            pause.ToolTip = w.Paused ? "자리를 잡은 뒤 어느 스크린샷부터 다시 분석할지 고르고 인식 영역을 확인합니다." : "눈깔(화면)을 옮기는 동안 분석을 멈춥니다. 앞의 분석 결과는 그대로 둡니다.";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(pause); buttons.Children.Add(edit); buttons.Children.Add(remove);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);

            var live = w.Enabled && w.RegionVersion >= 2 && !w.Paused;
            var info = new StackPanel();
            var line1 = new StackPanel { Orientation = Orientation.Horizontal };
            line1.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = live ? UiKit.Good : UiKit.Warn, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            line1.Children.Add(UiKit.Text(w.Label, 14, FontWeights.SemiBold));
            line1.Children.Add(UiKit.Chip(w.WatchType.Label(), UiKit.NeutralBg, UiKit.NeutralText, null, new Thickness(10, 0, 0, 0)));
            if (w.Paused) line1.Children.Add(UiKit.Chip($"일시중지 · {(w.PausedAt is { Length: >= 19 } pa ? pa.Substring(11, 8) : "")}부터", UiKit.WarnBg, UiKit.Warn, null, new Thickness(8, 0, 0, 0)));
            info.Children.Add(line1);
            info.Children.Add(UiKit.Dim($"{w.Character} · 인식 영역 {w.Regions.Count}개 ({string.Join(" · ", w.Regions.GroupBy(r => r.Kind).Select(g => $"{g.Key.Label()} {g.Count()}"))})" + (live ? "" : " · 영역 재설정 필요"), 12, new Thickness(16, 2, 0, 0)));
            row.Children.Add(info);
            list.Children.Add(new Border { Child = row, Padding = new Thickness(8, 8, 8, 8), Background = UiKit.Panel2, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6) });
        }
        box.Children.Add(list);
        return UiKit.Card(box);
    }

    private void NewWatcher() => WatcherWizard.ShowFor(Window.GetWindow(this), _svc, null);
    private void EditWatcher(string id) => WatcherWizard.ShowFor(Window.GetWindow(this), _svc, _svc.Store.ListWatchers().FirstOrDefault(w => w.Id == id));

    private void RemoveWatcher(Watcher w)
    {
        var answer = MessageBox.Show($"{w.Label} ({w.Character}) 감시를 제거할까요?\n\n이 눈깔의 이벤트, 현재 대상, 시그니처 기록이 초기화됩니다.\nCCTV 폴더의 원본 PNG 는 삭제하지 않습니다. 같은 캐릭터를 다시 등록하면 폴더에 남은 스크린샷을 처음부터 다시 분석합니다.",
            "감시 눈깔 제거", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _svc.DeleteWatcher(w.Id);
    }

    // ---------- 타임라인 ----------

    private UIElement BuildTimelineCard()
    {
        var box = new StackPanel();
        box.Children.Add(UiKit.SectionHead("감지 타임라인"));
        box.Children.Add(UiKit.Dim("줄을 누르면 판정 근거를 볼 수 있습니다.", 12));

        // 한 줄: 검색창 · 정확히 일치 · (눈깔이 둘 이상이면) 눈깔 선택
        var bar = new DockPanel { Margin = new Thickness(0, 10, 0, 8) };
        DockPanel.SetDock(_watcherCombo, Dock.Right);
        var exact = new FilterChip("정확히 일치", false, "꺼 두면 입력한 글자가 들어 있는 것을 모두 찾습니다");
        exact.Margin = new Thickness(8, 0, 0, 0);
        exact.Toggled += on => { _exact = on; FilterChanged(); };
        DockPanel.SetDock(exact, Dock.Right);
        bar.Children.Add(_watcherCombo); bar.Children.Add(exact);
        var searchBox = new Grid();
        _search.Width = double.NaN;
        searchBox.Children.Add(_search); searchBox.Children.Add(_searchHint);
        bar.Children.Add(searchBox);
        box.Children.Add(bar);

        foreach (var c in Enum.GetValues<EventCategory>())
        {
            var chip = new FilterChip(EventPresentation.CategoryLabel(c), true);
            chip.Margin = new Thickness(0, 0, 6, 0);
            chip.Toggled += on => { if (on) _categories.Add(c); else _categories.Remove(c); FilterChanged(); };
            _categoryRow.Children.Add(chip);
        }
        box.Children.Add(_categoryRow);

        _watcherCombo.SelectionChanged += (_, _) =>
        {
            if (_fillingWatchers) return;
            _watcherFilter = _watcherCombo.SelectedIndex <= 0 ? "" : _watcherCombo.SelectedItem as string ?? "";
            FilterChanged();
        };

        _timelineList.Margin = new Thickness(0, 10, 0, 0);
        box.Children.Add(_timelineList);
        return UiKit.Card(box);
    }

    private void RebuildFilters(ViewData d)
    {
        var labels = d.Watchers.Select(w => w.Label).ToList();
        _watcherCombo.Visibility = labels.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (labels.Count < 2) { _watcherFilter = ""; return; }
        var current = _watcherCombo.Items.Cast<string>().Skip(1).ToList();
        if (current.SequenceEqual(labels)) return;   // 눈깔 목록이 같으면 선택을 그대로 둔다
        _fillingWatchers = true;
        _watcherCombo.Items.Clear();
        _watcherCombo.Items.Add("전체 눈깔");
        foreach (var l in labels) _watcherCombo.Items.Add(l);
        _watcherCombo.SelectedIndex = Math.Max(0, labels.IndexOf(_watcherFilter) + 1);
        if (!labels.Contains(_watcherFilter)) _watcherFilter = "";
        _fillingWatchers = false;
    }

    private static string Norm(string? v) => System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace((v ?? "").ToLowerInvariant().Replace("[", "").Replace("]", ""), @"\*+$", ""), @"\s+", " ").Trim();

    private bool Matches(EventRow e, Func<string?, string> canonical)
    {
        var needle = Norm(_search.Text);
        if (needle.Length == 0) return true;
        bool Hit(string? v) { var h = Norm(v); return _exact ? h == needle : h.Contains(needle); }
        // 캐릭터, 함선, 콥 티커 어디에 있어도 찾는다.
        return Hit(e.Character) || Hit(e.Ship) || Hit(e.Corporation is null ? null : canonical(e.Corporation));
    }

    /// <summary>검색·분류·눈깔 조건이 바뀌면 첫 페이지로 돌아간다.</summary>
    private void FilterChanged() { _page = 0; RebuildTimeline(); }

    private void RebuildTimeline()
    {
        _timelineList.Children.Clear();
        if (_data == null) return;
        var d = _data;
        var shown = d.Events
            .Where(e => _watcherFilter.Length == 0 || e.WatcherLabel == _watcherFilter)
            .Where(e => _categories.Contains(EventPresentation.Category(e.Type)))
            .Where(e => Matches(e, d.Canonical)).ToList();

        if (shown.Count == 0)
        {
            var searching = Norm(_search.Text).Length > 0;
            _timelineList.Children.Add(UiKit.Dim(_categories.Count == 0 ? "표시할 항목을 선택해주세요. 위의 분류 버튼으로 여러 항목을 함께 볼 수 있습니다."
                : searching ? $"검색 결과가 없습니다. 현재 조건 안에서 \"{_search.Text.Trim()}\"을(를) 찾지 못했습니다."
                : d.Events.Count == 0 ? "아직 판정된 이벤트가 없습니다. 분석을 켜고 인식 영역을 확인하면 폴더 이미지를 시간순으로 분석합니다." : "선택한 조건의 기록이 없습니다.", 12, new Thickness(0, 14, 0, 6)));
            return;
        }

        var pages = (shown.Count + PageSize - 1) / PageSize;
        _page = Math.Clamp(_page, 0, pages - 1);
        foreach (var e in shown.Skip(_page * PageSize).Take(PageSize)) _timelineList.Children.Add(TimelineRow(e, d));
        if (pages > 1) _timelineList.Children.Add(BuildPager(pages, shown.Count));
    }

    private UIElement BuildPager(int pages, int total)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 2) };
        void Go(int page) { _page = Math.Clamp(page, 0, pages - 1); RebuildTimeline(); }
        Button Nav(string text, int page, bool enabled) { var b = UiKit.Button(text, () => Go(page), "GhostButton", double.NaN, enabled); b.Margin = new Thickness(2, 0, 2, 0); b.Padding = new Thickness(10, 3, 10, 3); return b; }
        bar.Children.Add(Nav("«", 0, _page > 0));
        bar.Children.Add(Nav("‹ 이전", _page - 1, _page > 0));
        var label = UiKit.Text($"{_page + 1} / {pages}", 13, FontWeights.SemiBold, null, new Thickness(12, 0, 12, 0)); label.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(label);
        bar.Children.Add(Nav("다음 ›", _page + 1, _page < pages - 1));
        bar.Children.Add(Nav("»", pages - 1, _page < pages - 1));
        var count = UiKit.Dim($"총 {total}건", 12, new Thickness(14, 0, 0, 0), false); count.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(count);
        return bar;
    }

    private UIElement TimelineRow(EventRow e, ViewData d)
    {
        var g = new Grid();
        foreach (var w in new[] { 70.0, 108.0, -1, 76.0, 120.0, 70.0 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });

        var time = UiKit.Dim(Summaries.Time(e.Time), 12, null, false); Grid.SetColumn(time, 0); g.Children.Add(time);

        var typeBox = new StackPanel { Orientation = Orientation.Horizontal };
        typeBox.Children.Add(UiKit.TintChip(EventPresentation.Label(e.Type), EventPresentation.Color(e.Type), EventPresentation.Rule(e)));
        Grid.SetColumn(typeBox, 1); g.Children.Add(typeBox);

        var main = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        var marker = CctvStore.IsMarker(e.Type);
        nameLine.Children.Add(UiKit.Text(marker ? EventPresentation.MarkerTitle(e) : e.Type.StartsWith("signature_") ? (e.Character ?? "---") : (e.Character ?? "미확인 대상"), 13.5, FontWeights.SemiBold));
        if (EventPresentation.Verification(e) is { } v) nameLine.Children.Add(UiKit.Chip(v, v == "확정" ? UiKit.GoodBg : UiKit.WarnBg, v == "확정" ? UiKit.Good : UiKit.Warn, null, new Thickness(8, 0, 0, 0)));
        main.Children.Add(nameLine);
        main.Children.Add(UiKit.Dim(EventPresentation.Detail(e), 12, new Thickness(0, 1, 0, 0), false));
        Grid.SetColumn(main, 2); g.Children.Add(main);

        var corp = UiKit.Text(e.Corporation is { Length: > 0 } c && d.Canonical(c) is var t && t != "미확인" ? $"[{t}]" : "—", 12.5, FontWeights.SemiBold, UiKit.AccentText); Grid.SetColumn(corp, 3); g.Children.Add(corp);
        var src = UiKit.Dim(e.WatcherLabel ?? "미지정 눈깔", 12, null, false); src.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(src, 4); g.Children.Add(src);
        var conf = UiKit.Dim(marker ? "" : $"인식 {Math.Round((e.Confidence ?? 0) * 100)}%", 11.5, null, false); Grid.SetColumn(conf, 5); g.Children.Add(conf);

        var row = new RowButton(g);
        if (marker) { row.Cursor = Cursors.Arrow; return row; }   // 표식은 판정 근거가 없다
        row.Clicked += () => EvidenceWindow.ShowFor(Window.GetWindow(this), _svc, e, d.Canonical);
        return row;
    }

    // ---------- 코퍼레이션 ----------

    private UIElement BuildCorps(ViewData d)
    {
        var box = new StackPanel();
        box.Children.Add(UiKit.SectionHead("코퍼레이션별 전력 현황"));
        if (d.Corps.Count == 0)
        {
            box.Children.Add(UiKit.Dim("아직 집계된 콥이 없습니다.", 12, new Thickness(0, 14, 0, 4)));
            return UiKit.Card(box);
        }
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var corp in d.Corps)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
            nameLine.Children.Add(UiKit.Text(corp.Name, 14, FontWeights.SemiBold));
            nameLine.Children.Add(UiKit.Dim(corp.Ticker, 12, new Thickness(6, 0, 0, 0), false));
            info.Children.Add(nameLine);
            info.Children.Add(UiKit.Dim($"도킹 {corp.Docked} · 미도킹 {corp.Observed} · 함선 {corp.Ships.Sum(s => s.Count)}대", 12, new Thickness(0, 2, 0, 0), false));
            var count = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            count.Children.Add(UiKit.Text(corp.Detected.ToString(), 20, FontWeights.Bold));
            count.Children.Add(UiKit.Dim("현재 인원", 11, null, false));
            Grid.SetColumn(count, 1);
            g.Children.Add(info); g.Children.Add(count);
            var row = new RowButton(g) { Padding = new Thickness(10, 9, 10, 9) };
            row.Clicked += () => CorpWindow.ShowFor(Window.GetWindow(this), corp);
            list.Children.Add(row);
        }
        box.Children.Add(list);
        return UiKit.Card(box, margin: new Thickness(0, 0, 0, 14));
    }

    // ---------- 프로빙 ----------

    private const double SignatureListHeight = 420;
    private double _currentScroll, _historyScroll;   // 화면이 다시 그려져도 스크롤 위치를 유지

    private UIElement BuildSignatures(ViewData d)
    {
        var box = new StackPanel();
        box.Children.Add(UiKit.SectionHead("프로빙 변화"));
        var latest = d.Signatures.Select(s => s.LastSeenAt).Concat(d.Events.Where(e => e.Type.StartsWith("signature_")).Select(e => e.Time)).OrderBy(x => x).LastOrDefault();
        var watcher = d.Signatures.FirstOrDefault()?.WatcherLabel ?? d.Events.FirstOrDefault(e => e.Type.StartsWith("signature_"))?.WatcherLabel ?? "프로빙 창을 지정한 눈깔";
        box.Children.Add(UiKit.Dim($"{watcher} · 마지막 갱신 {(latest != null ? Summaries.Time(latest) : "대기 중")}", 12));

        var cols = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 두 목록은 각각 정해진 높이 안에서 스크롤한다 (기록이 길어져도 카드가 끝없이 늘어나지 않는다).
        var current = new StackPanel();
        var currentRows = new StackPanel();
        current.Children.Add(ColumnTitle("현재 존재하는 시그니처", d.Signatures.Count.ToString()));
        if (d.Signatures.Count == 0) currentRows.Children.Add(UiKit.Dim("현재 인식된 시그니처 없음", 12, new Thickness(0, 8, 0, 0)));
        foreach (var s in d.Signatures)
        {
            var (name, group, unscanned) = EventPresentation.SignatureFields(s.Name, s.Group);
            currentRows.Children.Add(SignatureRow(Summaries.Time(s.LastSeenAt)[..5], s.Id, name, group, unscanned, null));
        }
        current.Children.Add(UiKit.InnerScroll(currentRows, SignatureListHeight, _currentScroll, o => _currentScroll = o));
        Grid.SetColumn(current, 0); cols.Children.Add(current);

        var history = new StackPanel();
        var historyRows = new StackPanel();
        var sigEvents = d.Events.Where(e => e.Type.StartsWith("signature_")).ToList();
        history.Children.Add(ColumnTitle("생성 · 소멸 기록", sigEvents.Count > 0 ? $"{sigEvents.Count}건" : "전체 기록"));
        if (sigEvents.Count == 0) historyRows.Children.Add(UiKit.Dim("생성·소멸 기록 없음", 12, new Thickness(0, 8, 0, 0)));
        foreach (var e in sigEvents)
        {
            var (name, group, unscanned) = EventPresentation.SignatureFields(e.Details["name"]?.ToString(), e.Details["group"]?.ToString());
            historyRows.Children.Add(SignatureRow(Summaries.Time(e.Time)[..5], e.Character ?? "---", name, group, unscanned, e.Type == "signature_created"));
        }
        history.Children.Add(UiKit.InnerScroll(historyRows, SignatureListHeight, _historyScroll, o => _historyScroll = o));
        Grid.SetColumn(history, 2); cols.Children.Add(history);
        box.Children.Add(cols);
        return UiKit.Card(box, margin: new Thickness(0, 0, 0, 14));
    }

    private static UIElement ColumnTitle(string title, string right)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var r = UiKit.Text(right, 12, FontWeights.SemiBold, UiKit.AccentText); DockPanel.SetDock(r, Dock.Right);
        dock.Children.Add(r); dock.Children.Add(UiKit.Text(title, 12.5, FontWeights.SemiBold, UiKit.NeutralText));
        return dock;
    }

    private static UIElement SignatureRow(string time, string id, string name, string group, bool unscanned, bool? created)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        foreach (var w in new[] { created == null ? 0.0 : 18.0, 44.0, 46.0, -1, 76.0 })   // 현재 목록에는 +/− 표시가 없으므로 그 자리를 비우지 않고 제목과 왼쪽을 맞춘다
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        var sign = created == null ? "" : created == true ? "+" : "−";
        var t = UiKit.Text(sign, 14, FontWeights.Bold, created == true ? UiKit.Good : UiKit.Bad); Grid.SetColumn(t, 0); g.Children.Add(t);
        var tm = UiKit.Dim(time, 12, null, false); Grid.SetColumn(tm, 1); g.Children.Add(tm);
        var idt = UiKit.Text(id, 12.5, FontWeights.SemiBold); Grid.SetColumn(idt, 2); g.Children.Add(idt);
        var nm = UiKit.Text(unscanned ? "코즈믹 시그니처 (미스캔)" : name, 12.5, FontWeights.Normal, null, null); nm.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(nm, 3); g.Children.Add(nm);
        var gr = UiKit.Dim(group, 11.5, null, false); gr.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(gr, 4); g.Children.Add(gr);
        return g;
    }
}
