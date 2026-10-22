using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Argus.Modules.Capture;

public sealed class ClientRow : INotifyPropertyChanged
{
    private string _status = "";
    private string _note = "";
    private bool _running;
    public required string Name { get; init; }
    public string Note { get => _note; set { if (_note != value) { _note = value; Raise(nameof(Note)); } } }
    public string Status { get => _status; set { if (_status != value) { _status = value; Raise(nameof(Status)); } } }
    public bool Running { get => _running; set { if (_running != value) { _running = value; Raise(nameof(Running)); } } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class DetectionRow(DetectionRecord r)
{
    public string TimeText { get; } = r.At.ToString("HH:mm:ss");
    public string Path { get; } = r.SavedPath ?? "";
    public string Detail { get; } = r.SavedPath is null
        ? $"{r.ChangedPixels:N0}px 변화 · 저장 안 함"
        : $"{r.ChangedPixels:N0}px 변화 · {System.IO.Path.GetFileName(r.SavedPath)}";
    public Visibility OpenVisibility { get; } = r.SavedPath is null ? Visibility.Collapsed : Visibility.Visible;
}

public partial class CaptureView : UserControl
{
    private static readonly (string Label, int Value)[] Intervals =
        [("0.25초", 250), ("0.5초", 500), ("1초", 1000), ("2초", 2000), ("5초", 5000)];
    private static readonly (string Label, int Value)[] AlertIntervals =
        [("1초", 1), ("2초", 2), ("5초", 5), ("10초", 10), ("30초", 30), ("60초", 60)];

    private readonly CaptureService _service;
    private readonly ObservableCollection<ClientRow> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _noteSave = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(600) };   // 메모 입력이 멈추면 파일에 저장 (화면 갱신이 바빠도 밀리지 않게 보통 우선순위)
    private ClientCaptureConfig? _cfg;
    private string? _character;
    private bool _loading;

    public CaptureView(CaptureService service)
    {
        _service = service;
        InitializeComponent();

        foreach (var (l, v) in Intervals) IntervalBox.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        foreach (var (l, v) in AlertIntervals) AlertBox.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        FolderBox.Text = _service.OutputFolder;
        _service.OutputFolderChanged += () => Dispatcher.BeginInvoke(() => FolderBox.Text = _service.OutputFolder);
        ClientList.ItemsSource = _rows;

        _service.ClientsUpdated += () => Dispatcher.BeginInvoke(RefreshList);
        _service.DetectionLogged += name => Dispatcher.BeginInvoke(() => { if (name == _character) RefreshLog(); });
        NoteBox.TextChanged += (_, _) => Note_Changed();
        _noteSave.Tick += (_, _) => FlushNote();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { RefreshList(); _timer.Start(); };
        Unloaded += (_, _) => { _timer.Stop(); FlushNote(); };
    }

    // ---- 클라이언트 목록 ----

    private void RefreshList()
    {
        var names = _service.Characters;
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!names.Contains(_rows[i].Name)) _rows.RemoveAt(i);
        foreach (var n in names)
            if (_rows.All(r => r.Name != n)) _rows.Add(new ClientRow { Name = n });

        var sel = _rows.FirstOrDefault(r => r.Name == _character);
        if (sel != null) ClientList.SelectedItem = sel;
        else if (_rows.Count > 0) ClientList.SelectedIndex = 0;
        else SelectClient(null);
        NoClient.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshRows();
    }

    private void RefreshRows()
    {
        foreach (var r in _rows)
        {
            var cfg = _service.GetConfig(r.Name);
            r.Note = cfg?.Note ?? "";
            r.Running = _service.IsRunning(r.Name);
            r.Status = r.Running ? _service.GetStatus(r.Name)
                     : cfg is { HasRoi: false } ? "영역 미지정"
                     : _service.IsMinimized(r.Name) ? "최소화됨" : "대기";
        }
    }

    private void ClientList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SelectClient((ClientList.SelectedItem as ClientRow)?.Name);

    private void SelectClient(string? character)
    {
        _character = character;
        _cfg = character == null ? null : _service.GetConfig(character);
        Detail.Visibility = _cfg != null ? Visibility.Visible : Visibility.Collapsed;
        if (_cfg == null) return;

        _loading = true;
        CharName.Text = _character;
        NoteBox.Text = _cfg.Note;
        NoteHint.Visibility = string.IsNullOrEmpty(_cfg.Note) ? Visibility.Visible : Visibility.Collapsed;
        SelectByTag(IntervalBox, _cfg.IntervalMs);
        SelectByTag(AlertBox, _cfg.AlertIntervalSec);
        BeepBox.IsChecked = _cfg.Beep;
        ThresholdSlider.Value = _cfg.PixelThreshold;
        MinPixelsSlider.Value = _cfg.MinChangedPixels;
        ThresholdText.Text = _cfg.PixelThreshold.ToString();
        MinPixelsText.Text = _cfg.MinChangedPixels.ToString();
        SaveNone.IsChecked = _cfg.SaveMode == SaveMode.None;
        SaveArea.IsChecked = _cfg.SaveMode == SaveMode.SelectedArea;
        SaveFull.IsChecked = _cfg.SaveMode == SaveMode.FullClient;
        _loading = false;

        UpdateRegionCard();
        UpdateButtons();
        UpdateStatus();
        RefreshLog();
    }

    private static void SelectByTag(ComboBox box, int value)
    {
        var item = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == value)
                   ?? box.Items.Cast<ComboBoxItem>().First();
        box.SelectedItem = item;
    }

    private void Tick()
    {
        RefreshRows();
        UpdateStatus();
        UpdateButtons();
    }

    // ---- 메모 ----

    // 입력하는 동안 바로 설정에 넣어 왼쪽 목록 · 대시보드에 그대로 보이게 하고, 파일 저장은 입력이 멈출 때(0.6초) · Enter · 다른 곳으로 옮길 때 한다.
    private void Note_Changed()
    {
        NoteHint.Visibility = NoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loading || _cfg == null) return;
        var text = NoteBox.Text.Trim();
        if (text == _cfg.Note) return;
        _cfg.Note = text;
        if (_rows.FirstOrDefault(r => r.Name == _character) is { } row) row.Note = text;
        _noteSave.Stop(); _noteSave.Start();
    }

    private void FlushNote()
    {
        if (!_noteSave.IsEnabled) return;
        _noteSave.Stop();
        _service.Save();
    }

    private void Note_LostFocus(object sender, RoutedEventArgs e) => FlushNote();

    private void Note_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        FlushNote();
        Keyboard.ClearFocus();
    }

    // ---- 감시 영역 ----

    /// <summary>클라이언트 화면을 새로 캡처해 전체 화면 선택기에서 감시 영역을 고른다.</summary>
    private async void PickRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg == null || _character == null) return;
        var cfg = _cfg;
        var name = _character;
        PickBtn.IsEnabled = false;
        try
        {
            var frame = await Task.Run(() => _service.Grab(name));
            if (frame == null)
            {
                System.Windows.MessageBox.Show(_service.IsMinimized(name)
                    ? "클라이언트 창이 최소화되어 있어 화면을 캡처할 수 없습니다.\n창을 복원한 뒤 다시 시도하세요."
                    : "화면을 캡처하지 못했습니다. 잠시 후 다시 시도하세요.", "영역 지정", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var shot = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Bgra, frame.Width * 4);
            shot.Freeze();
            Int32Rect? current = cfg.HasRoi ? new Int32Rect(cfg.RoiX, cfg.RoiY, cfg.RoiW, cfg.RoiH) : null;

            var owner = Window.GetWindow(this);
            var picker = new RegionPickerWindow(shot, current, _service.RegionPresets) { Owner = owner };
            if (owner != null) { picker.Left = owner.Left; picker.Top = owner.Top; } // 앱이 떠 있는 모니터에서 열기
            if (picker.ShowDialog() != true || picker.Result is not { } r) return;

            if (picker.ChosenPreset is { } preset) preset.ApplyTo(cfg, shot.PixelWidth, shot.PixelHeight);   // 프리셋을 골라 확인했으면 감시 설정도 함께
            else { cfg.RoiX = r.X; cfg.RoiY = r.Y; cfg.RoiW = r.Width; cfg.RoiH = r.Height; }
            cfg.RoiFrameW = shot.PixelWidth; cfg.RoiFrameH = shot.PixelHeight;
            _service.Save();
            if (_character == name) SelectClient(name);   // 영역 카드와 감도 · 주기 등 화면 값을 다시 읽는다
        }
        finally { PickBtn.IsEnabled = true; }
    }

    // ---- 영역 프리셋 ----

    private Popup? _presetPopup;

    /// <summary>[프리셋 ▾]: 저장된 영역 프리셋(누르면 이 클라이언트에 적용)과 '지금 영역과 설정을 프리셋으로 저장'.</summary>
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg == null || _character == null) return;
        _presetPopup ??= new Popup
        {
            PlacementTarget = PresetBtn, Placement = PlacementMode.Custom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            // 단추 오른쪽 끝에 맞춰 아래로 연다 (단추가 카드 오른쪽에 있어서 왼쪽 정렬이면 창 밖으로 나간다)
            CustomPopupPlacementCallback = (popup, target, _) => [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 6), PopupPrimaryAxis.Horizontal)],
        };
        _presetPopup.Child = BuildPresetPanel();
        _presetPopup.IsOpen = true;
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private FrameworkElement BuildPresetPanel()
    {
        var list = new StackPanel();
        list.Children.Add(new TextBlock { Text = "영역 프리셋", FontSize = 12, Foreground = Res("TextDim"), Margin = new Thickness(10, 4, 10, 6) });
        var presets = _service.RegionPresets;
        if (presets.Count == 0)
            list.Children.Add(new TextBlock { Text = "저장된 프리셋이 없습니다. 영역을 지정한 뒤 아래에서 저장하세요.", FontSize = 12, Foreground = Res("TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 2, 10, 8) });
        foreach (var p in presets) list.Children.Add(PresetRow(p));

        list.Children.Add(new Border { Height = 1, Background = Res("Line"), Margin = new Thickness(4, 6, 4, 6) });
        var canSave = _cfg is { HasRoi: true };
        var save = MenuRow(new TextBlock { Text = "＋  지금 영역과 설정을 프리셋으로 저장…", FontWeight = FontWeights.SemiBold, Foreground = Res(canSave ? "AccentHover" : "TextDim") },
            canSave ? () => { _presetPopup!.IsOpen = false; SaveCurrentAsPreset(); } : null);
        if (!canSave) save.ToolTip = "먼저 감시 영역을 지정하세요";
        list.Children.Add(save);

        return new Border
        {
            Width = 420, Padding = new Thickness(6), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Background = Res("Surface2"), BorderBrush = Res("Line"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = .45 },
            Child = new ScrollViewer { Content = list, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
    }

    /// <summary>눌러서 실행하는 한 줄 (마우스를 올리면 배경이 바뀐다). onClick 이 null 이면 누를 수 없다.</summary>
    private Border MenuRow(UIElement content, Action? onClick)
    {
        var row = new Border { Child = content, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Cursor = onClick != null ? Cursors.Hand : null };
        if (onClick != null)
        {
            row.MouseEnter += (_, _) => row.Background = Res("Surface3");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => onClick();
        }
        return row;
    }

    private Border PresetRow(RegionPreset p)
    {
        var del = new Button { Content = "삭제", Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        del.SetResourceReference(StyleProperty, "GhostButton");
        del.Click += (_, _) =>
        {
            if (MessageBox.Show(Window.GetWindow(this), $"'{p.Name}' 프리셋을 삭제할까요?\n(이 프리셋을 적용한 클라이언트의 감시 영역은 그대로 남습니다.)", "영역 프리셋 삭제",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _service.DeleteRegionPreset(p.Name);
            if (_presetPopup != null) { _presetPopup.Child = BuildPresetPanel(); _presetPopup.IsOpen = true; }
        };
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = p.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = RegionPresetText.RegionLine(p), FontSize = 12, Foreground = Res("TextDim"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = RegionPresetText.SettingsLine(p), FontSize = 12, Foreground = Res("TextDim"), TextTrimming = TextTrimming.CharacterEllipsis });
        var dock = new DockPanel();
        DockPanel.SetDock(del, Dock.Right);
        dock.Children.Add(del); dock.Children.Add(info);
        var row = MenuRow(dock, () => { _presetPopup!.IsOpen = false; ApplyPresetToCurrent(p); });
        row.ToolTip = "이 클라이언트에 적용 (감시 영역 + 감도 · 주기 · 알림 · 저장 범위)";
        return row;
    }

    /// <summary>프리셋을 지금 고른 클라이언트에 적용한다. 화면 크기가 저장할 때와 다르면 먼저 묻고, 적용하면 가장 가까운 모서리 기준으로 옮긴다.</summary>
    private async void ApplyPresetToCurrent(RegionPreset p)
    {
        if (_cfg == null || _character == null) return;
        var cfg = _cfg; var name = _character;
        var frame = await Task.Run(() => _service.Grab(name));
        int w = frame?.Width ?? 0, h = frame?.Height ?? 0;
        if (!RegionPresetText.ConfirmFit(Window.GetWindow(this), p, w, h)) return;
        p.ApplyTo(cfg, w, h);
        _service.Save();
        if (_character == name) SelectClient(name);   // 영역 카드와 감도 · 주기 등 화면 값을 다시 읽는다
        RefreshRows();
    }

    /// <summary>지금 클라이언트의 감시 영역과 감시 설정을 이름 붙여 프리셋으로 저장한다.</summary>
    private async void SaveCurrentAsPreset()
    {
        if (_cfg is not { HasRoi: true } || _character == null) return;
        var cfg = _cfg; var name = _character;
        var owner = Window.GetWindow(this);
        var suggested = cfg.Note.Length > 40 ? cfg.Note[..40] : cfg.Note;
        var presetName = Argus.Ui.PromptWindow.Ask(owner, "영역 프리셋 저장", "프리셋 이름 (지금 감시 영역과 감도 · 주기 · 알림 · 저장 범위를 함께 저장합니다)", suggested);
        if (presetName == null) return;
        if (_service.RegionPresetExists(presetName) &&
            MessageBox.Show(owner, $"'{presetName}' 프리셋이 이미 있습니다. 지금 영역과 설정으로 바꿀까요?", "영역 프리셋 저장",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        // 기준 화면 크기: 영역을 지정할 때의 크기. 예전에 지정한 영역이라 모르면 지금 창 크기로 (최소화돼 있으면 모르는 채로 저장).
        int w = cfg.RoiFrameW, h = cfg.RoiFrameH;
        if (w <= 0 || h <= 0)
        {
            var frame = await Task.Run(() => _service.Grab(name));
            w = frame?.Width ?? 0; h = frame?.Height ?? 0;
        }
        _service.SaveRegionPreset(RegionPreset.From(presetName, cfg, w, h));
    }

    private RegionPreviewWindow? _previewWin;

    /// <summary>지정된 감시 영역만 잘라 실시간으로 보여준다. 이미 열려 있으면 앞으로 가져온다.</summary>
    private void ViewRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg is not { HasRoi: true } || _character == null) return;
        if (_previewWin is { IsLoaded: true }) _previewWin.Close(); // 다른 클라이언트/영역으로 바뀐 경우를 위해 새로 연다
        _previewWin = new RegionPreviewWindow(_service, _cfg, _character) { Owner = Window.GetWindow(this) };
        _previewWin.Show();
    }

    private void UpdateRegionCard()
    {
        if (_cfg == null) return;
        ViewBtn.IsEnabled = _cfg.HasRoi;
        PresetBtn.ToolTip = _service.RegionPresets.Count == 0 && !_cfg.HasRoi
            ? "영역을 지정한 뒤 영역 프리셋으로 저장하면, 다른 클라이언트에도 같은 영역과 설정을 바로 적용할 수 있습니다"
            : "감시 영역과 감시 설정을 영역 프리셋으로 저장하거나, 저장한 프리셋을 이 클라이언트에 적용합니다";
        if (_cfg.HasRoi)
        {
            RoiLabel.Text = $"{_cfg.RoiW} × {_cfg.RoiH}  @ ({_cfg.RoiX}, {_cfg.RoiY})";
            RoiBadge.Background = (System.Windows.Media.Brush)FindResource("AccentSoft");
            PickBtn.Content = "영역 다시 지정";
        }
        else
        {
            RoiLabel.Text = "미지정";
            RoiBadge.Background = (System.Windows.Media.Brush)FindResource("Surface3");
            PickBtn.Content = "영역 지정";
        }
    }

    // ---- 설정 ----

    private void Setting_Changed(object sender, RoutedEventArgs e) => ApplySettings();

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdText == null || MinPixelsText == null) return; // 초기화 중
        ThresholdText.Text = ((int)ThresholdSlider.Value).ToString();
        MinPixelsText.Text = ((int)MinPixelsSlider.Value).ToString();
        ApplySettings();
    }

    private void ApplySettings()
    {
        if (_loading || _cfg == null) return;
        if (IntervalBox.SelectedItem is ComboBoxItem i) _cfg.IntervalMs = (int)i.Tag;
        if (AlertBox.SelectedItem is ComboBoxItem a) _cfg.AlertIntervalSec = (int)a.Tag;
        _cfg.Beep = BeepBox.IsChecked == true;
        _cfg.PixelThreshold = (int)ThresholdSlider.Value;
        _cfg.MinChangedPixels = (int)MinPixelsSlider.Value;
        _cfg.SaveMode = SaveNone.IsChecked == true ? SaveMode.None
                      : SaveFull.IsChecked == true ? SaveMode.FullClient : SaveMode.SelectedArea;
        _service.Save();
    }

    // ---- 시작/중지 ----

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_character == null || _cfg == null) return;
        if (_service.IsRunning(_character)) _service.StopMonitor(_character);
        else if (_cfg.HasRoi) _service.StartMonitor(_character);
        UpdateButtons();
        UpdateStatus();
        RefreshRows();
    }

    private void UpdateButtons()
    {
        if (_character == null || _cfg == null) return;
        var running = _service.IsRunning(_character);
        ToggleBtn.Content = running ? "■  감시 중지" : "▶  감시 시작";
        ToggleBtn.Style = (Style)FindResource(running ? "DangerButton" : "PrimaryButton");
        ToggleBtn.IsEnabled = running || _cfg.HasRoi; // 영역 지정 전에는 시작할 수 없다
        ToggleBtn.ToolTip = ToggleBtn.IsEnabled ? null : "먼저 감시 영역을 지정하세요";
    }

    private void UpdateStatus()
    {
        if (_character == null || _cfg == null) return;
        var running = _service.IsRunning(_character);
        StatusText.Text = running ? _service.GetStatus(_character)
                        : !_cfg.HasRoi ? "감시 영역을 지정하면 시작할 수 있습니다"
                        : _service.IsMinimized(_character) ? "창이 최소화되어 캡처할 수 없습니다" : "대기 중";
        StateDot.Fill = (System.Windows.Media.Brush)FindResource(running ? "Success" : "TextDim");
    }

    // ---- 감지 기록 ----

    private void RefreshLog()
    {
        if (_character == null) return;
        var rows = _service.GetLog(_character).Select(r => new DetectionRow(r)).ToList();
        LogList.ItemsSource = rows;
        LogEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LogCount.Text = rows.Count == 0 ? "" : $"최근 {rows.Count}건";
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path)) { System.Windows.MessageBox.Show("파일을 찾을 수 없습니다.", "열기"); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* 연결 프로그램 없음 */ }
    }

    // ---- 저장 폴더 ----

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _service.OpenOutputFolder();
}
