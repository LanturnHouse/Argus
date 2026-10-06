using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Argus.Modules.Preview;

public sealed record PreviewRow(string Character, bool Visible, bool InCycle, string Info, string HotkeyText, bool HasHotkey, bool HasWarning, string WarnText, string CycleOrder);

public partial class PreviewView : UserControl
{
    private static readonly Brush WarnFg = Frozen(0xF5, 0xC1, 0x5A), WarnBg = Frozen(0x2B, 0x24, 0x15), WarnLine = Frozen(0x8A, 0x6A, 0x1F);
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    private readonly PreviewService _svc;
    private readonly HotkeyService _hotkeys;
    private readonly TextBlock _hint = new() { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private bool _loading;
    private string? _capturing;       // 단축키를 받는 중인 대상: "next", "prev", "c:캐릭터"
    private int _captureId;           // 예전 지정 세션의 늦은 완료 통지가 새 세션을 지우지 않게 구분
    private string _presetBoxKey = "";
    private string _presetListKey = "";

    public PreviewView(PreviewService svc, HotkeyService hotkeys)
    {
        _svc = svc;
        _hotkeys = hotkeys;
        InitializeComponent();
        _hint.SetResourceReference(StyleProperty, "Dim");
        _hint.Visibility = Visibility.Collapsed;
        _svc.Changed += () => Dispatcher.BeginInvoke(Reload);
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => { if (_capturing != null) _hotkeys.CancelCapture(); };
    }

    private void Reload()
    {
        if (_drag is { Active: true }) return;   // 끌고 있는 동안에는 목록을 다시 만들지 않는다 (놓으면 갱신)
        _loading = true;
        EnabledBox.IsChecked = _svc.Settings.Enabled;
        UpdateEditButton();

        var presetKey = string.Join("|", _svc.Settings.Presets.Select(p => p.Id + ":" + p.Name));
        if (presetKey != _presetListKey) { _presetListKey = presetKey; PresetList.ItemsSource = _svc.Settings.Presets.ToList(); }
        PresetList.SelectedItem = _svc.ActivePreset;

        var warnings = _hotkeys.Warnings();
        var cycleNo = 0;   // 사이클에 포함된 클라이언트만 목록 순서대로 1, 2, 3… 을 매긴다
        var rows = _svc.Clients().Select(c =>
        {
            var action = HotkeyActions.Jump(c.Character);
            var mine = warnings.Where(w => w.Actions.Contains(action)).Select(w => w.Message).ToList();
            var text = _capturing == "c:" + c.Character ? "누르세요…  (Esc 취소)" : c.Layout.Hotkey?.ToString() ?? "단축키 없음";
            return new PreviewRow(c.Character, c.Layout.Visible, c.Layout.InCycle,
                $"{c.Layout.W} × {c.Layout.H}  @ ({c.Layout.X}, {c.Layout.Y})",
                text, c.Layout.Hotkey != null, mine.Count > 0, string.Join("\n\n", mine),
                c.Layout.InCycle && !c.LoggedOut ? (++cycleNo).ToString() : "-");
        }).ToList();
        // 드래그 중에는 자주 호출되므로 내용이 같으면 다시 그리지 않는다.
        if (ClientList.ItemsSource is not List<PreviewRow> old || !old.SequenceEqual(rows))
        {
            var sv = FindScrollViewer(ClientList);
            var offset = sv?.VerticalOffset ?? 0;
            ClientList.ItemsSource = rows;
            if (sv != null && offset > 0) { ClientList.UpdateLayout(); sv.ScrollToVerticalOffset(offset); }
        }
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RebuildPresetBox(warnings);
        _loading = false;
    }

    // ---- 상단 ----

    private void Enabled_Click(object sender, RoutedEventArgs e) => _svc.SetEnabled(EnabledBox.IsChecked == true);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        _svc.SetEditMode(!_svc.EditMode);
        UpdateEditButton();
    }

    private void UpdateEditButton()
    {
        EditBtn.Content = _svc.EditMode ? "편집 끝내기" : "배치 편집";
        if (_svc.EditMode) EditBtn.SetResourceReference(StyleProperty, "PrimaryButton");
        else EditBtn.ClearValue(StyleProperty);
        EditBanner.Visibility = _svc.EditMode ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 프리셋 ----

    private void Preset_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetList.SelectedItem is not LayoutPreset p) return;
        _svc.SwitchPreset(p.Id);
    }

    private void NewPreset_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(Window.GetWindow(this), "새 프리셋", "프리셋 이름 (현재 프리뷰 배치와 단축키 설정을 복사해서 만듭니다)");
        if (name == null) return;
        if (_svc.PresetNameTaken(name)) { MessageBox.Show($"'{name}' 이름의 프리셋이 이미 있습니다.", "새 프리셋", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _svc.CreatePreset(name);
    }

    private LayoutPreset? PresetOf(object sender) =>
        sender is FrameworkElement { Tag: string id } ? _svc.Settings.Presets.FirstOrDefault(x => x.Id == id) : null;

    private void RenamePreset_Click(object sender, RoutedEventArgs e)
    {
        var p = PresetOf(sender) ?? _svc.ActivePreset;
        var name = PromptWindow.Ask(Window.GetWindow(this), "프리셋 이름 변경", "새 이름", p.Name);
        if (name == null || name == p.Name) return;
        if (_svc.PresetNameTaken(name, p.Id)) { MessageBox.Show($"'{name}' 이름의 프리셋이 이미 있습니다.", "이름 변경", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _svc.RenamePreset(p.Id, name);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        var p = PresetOf(sender) ?? _svc.ActivePreset;
        if (_svc.Settings.Presets.Count <= 1)
        {
            MessageBox.Show("프리셋은 최소 하나가 있어야 합니다.", "삭제", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"'{p.Name}' 프리셋을 삭제할까요?", "삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _svc.DeletePreset(p.Id);
    }

    // ---- 프리셋 설정 칸 (이 프리셋의 사이클 단축키) ----

    private void RebuildPresetBox(List<HotkeyWarning> warnings)
    {
        var p = _svc.ActivePreset;
        // 바뀐 게 없으면 다시 만들지 않는다 (배치 편집 중 잦은 갱신에서 깜빡이지 않게).
        var key = string.Join("|", p.Name, p.CycleNext?.ToString(), p.CyclePrev?.ToString(), p.SurgeHotkey?.ToString(), _svc.Settings.SurgeSeconds, _capturing, string.Join("~", warnings.Select(w => w.Message)));
        if (key == _presetBoxKey) return;
        _presetBoxKey = key;

        PresetBox.Children.Clear();
        var title = new TextBlock { Text = $"프리셋 '{p.Name}' 설정", Margin = new Thickness(0, 0, 0, 10) }; title.SetResourceReference(StyleProperty, "CardTitle");
        PresetBox.Children.Add(title);
        PresetBox.Children.Add(_hint);
        foreach (var w in warnings) PresetBox.Children.Add(WarningBox(w.Message));
        PresetBox.Children.Add(CycleRow("사이클 다음 클라이언트", "next", p.CycleNext, t => _svc.SetCycleHotkey(true, t)));
        PresetBox.Children.Add(CycleRow("사이클 이전 클라이언트", "prev", p.CyclePrev, t => _svc.SetCycleHotkey(false, t)));
        PresetBox.Children.Add(CycleRow("레드박싱이 난 클라이언트로 전환", "surge", p.SurgeHotkey, t => _svc.SetSurgeHotkey(t), $"레드박싱 후 {_svc.Settings.SurgeSeconds}초 동안"));
    }

    private UIElement CycleRow(string label, string key, HotkeyTrigger? trigger, Action<HotkeyTrigger?> set, string? note = null)
    {
        var capturing = _capturing == key;
        var text = capturing ? "누르세요…  (Esc 취소)" : trigger?.ToString() ?? "단축키 없음";
        var pick = new Button
        {
            Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
            ToolTip = text, Width = 210, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 4, 0),   // 폭 고정: 지정 중 문구가 바뀌어도 커지지 않는다
        };
        if (trigger == null && !capturing) pick.Opacity = 0.7;
        if (capturing) pick.SetResourceReference(StyleProperty, "PrimaryButton");
        pick.Click += (_, _) => Begin(key, t => set(t));

        var clear = new Button { Content = "", ToolTip = "단축키 지우기", Width = 30, Padding = new Thickness(0, 6, 0, 6), IsEnabled = trigger != null };
        clear.SetResourceReference(FontFamilyProperty, "IconFont");
        clear.Click += (_, _) => set(null);

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(pick); bar.Children.Add(clear);
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(bar, Dock.Right);
        row.Children.Add(bar);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label });
        if (note != null)
        {
            var dim = new TextBlock { Text = note, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = note };
            dim.SetResourceReference(StyleProperty, "Dim");
            labels.Children.Add(dim);
        }
        row.Children.Add(labels);
        return row;
    }

    private static UIElement WarningBox(string message)
    {
        var icon = new TextBlock { Text = "", FontSize = 14, Foreground = WarnFg, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var text = new TextBlock { Text = message, Foreground = WarnFg, TextWrapping = TextWrapping.Wrap };
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon); dock.Children.Add(text);
        return new Border { Child = dock, Background = WarnBg, BorderBrush = WarnLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 8) };
    }

    // ---- 단축키 지정 ----

    private void Begin(string key, Action<HotkeyTrigger> apply)
    {
        var id = ++_captureId;
        _capturing = key;
        SetHint("");
        Reload();
        _hotkeys.BeginCapture(apply, SetHint, () => Dispatcher.BeginInvoke(() =>
        {
            if (id != _captureId) return;
            _capturing = null;
            SetHint("");
            Reload();
        }));
    }

    private void SetHint(string text)
    {
        _hint.Text = text;
        _hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) Begin("c:" + name, t => _svc.SetClientHotkey(name, t));
    }

    private void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) _svc.SetClientHotkey(name, null);
    }

    // ---- 클라이언트 ----

    private void Visible_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PreviewRow row }) _svc.SetVisible(row.Character, !row.Visible);
    }

    /// <summary>이 클라이언트의 프리뷰 위에 표시할 전투 HUD 요소를 체크로 고른다.</summary>
    private void Hud_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } button) return;
        var layout = _svc.Clients().FirstOrDefault(c => c.Character == name)?.Layout;
        if (layout == null) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string header, HudElement element, bool on)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = on, StaysOpenOnClick = true };
            item.Click += (_, _) => _svc.SetHud(name, element, item.IsChecked);
            menu.Items.Add(item);
        }
        Item("받는 DPS  ▼", HudElement.DpsIn, layout.HudDpsIn);
        Item("받는 LOGI  ✚", HudElement.Logi, layout.HudLogi);
        Item("받는 뉴트 (노스·캡 전송 반영)  ⚡", HudElement.Neut, layout.HudNeut);
        menu.Items.Add(new Separator());
        Item("레드박싱 경고 (붉은 깜빡임 · 전환 키 안내)", HudElement.Surge, layout.HudSurge);
        menu.IsOpen = true;
    }

    private void InCycle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PreviewRow row }) _svc.SetInCycle(row.Character, !row.InCycle);
    }

    // ---- 끌어서 순서 바꾸기: 잡은 줄이 마우스를 따라오고, 다른 줄들은 자리를 비켜준다 ----

    private const double DragThreshold = 4;

    private sealed class RowDrag
    {
        public required string Name;
        public required int From;
        public required double StartY;
        public required List<ListBoxItem> Items;
        public required double[] Tops;
        public required double[] Heights;
        public required double[] Offsets;   // 각 줄이 지금 향해 가는(애니메이션 중인) 세로 이동량
        public bool Active;
        public int To;
    }

    private RowDrag? _drag;

    private void Grip_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name } grip) return;
        DragBegin(name, e.GetPosition(ClientList).Y);
        if (_drag != null) { grip.CaptureMouse(); e.Handled = true; }
    }

    private void Grip_Move(object sender, MouseEventArgs e)
    {
        if (_drag == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { DragEnd(); return; }
        DragMove(e.GetPosition(ClientList).Y);
    }

    private void Grip_Up(object sender, MouseButtonEventArgs e) => DragEnd();

    private void Grip_LostCapture(object sender, MouseEventArgs e) => DragEnd();

    /// <summary>줄을 잡는다. 실제로 끌기 시작하는 것은 조금 움직인 뒤부터다 (그냥 클릭은 무시).</summary>
    internal void DragBegin(string name, double y)
    {
        if (ClientList.ItemsSource is not List<PreviewRow> rows) return;
        var from = rows.FindIndex(r => r.Character == name);
        if (from < 0) return;
        var items = rows.Select(r => RowContainer(r.Character)).ToList();
        if (items.Any(i => i == null)) return;
        foreach (var it in items) it!.RenderTransform = new TranslateTransform();
        _drag = new RowDrag
        {
            Name = name, From = from, StartY = y, To = from,
            Items = items!,
            Tops = [.. items.Select(i => i!.TranslatePoint(new Point(0, 0), ClientList).Y)],
            Heights = [.. items.Select(i => i!.ActualHeight)],
            Offsets = new double[items.Count],
        };
    }

    internal void DragMove(double y)
    {
        var d = _drag;
        if (d == null) return;
        var dy = y - d.StartY;
        var held = d.Items[d.From];
        if (!d.Active)
        {
            if (Math.Abs(dy) < DragThreshold) return;
            d.Active = true;
            Panel.SetZIndex(held, 10);   // 다른 줄 위로 올라와서 보인다
            held.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black };
            held.Opacity = 0.97;
        }

        // 잡은 줄은 마우스를 그대로 따라온다 (목록 안에서만).
        dy = Math.Clamp(dy, d.Tops[0] - d.Tops[d.From], d.Tops[^1] - d.Tops[d.From]);
        ((TranslateTransform)held.RenderTransform).Y = dy;

        // 잡은 줄의 중심이 어느 자리에 있는지 → 그 사이의 줄들이 한 칸씩 비켜준다.
        var center = d.Tops[d.From] + dy + d.Heights[d.From] / 2;
        var to = d.From;
        for (int i = 0; i < d.Items.Count; i++)
            if (center >= d.Tops[i] && center < d.Tops[i] + d.Heights[i]) { to = i; break; }
        d.To = to;

        var shift = d.Heights[d.From];
        for (int j = 0; j < d.Items.Count; j++)
        {
            if (j == d.From) continue;
            var want = d.From < to && j > d.From && j <= to ? -shift
                     : to < d.From && j >= to && j < d.From ? shift
                     : 0;
            if (want == d.Offsets[j]) continue;
            d.Offsets[j] = want;
            Slide(d.Items[j], want);
        }
    }

    /// <summary>놓는다: 잡은 줄이 자기 자리로 미끄러져 들어간 뒤 순서를 확정한다.</summary>
    internal void DragEnd()
    {
        var d = _drag;
        if (d == null) return;
        _drag = null;
        Mouse.Capture(null);
        if (!d.Active) return;

        var held = d.Items[d.From];
        Slide(held, d.Tops[d.To] - d.Tops[d.From], () =>
        {
            held.Effect = null; held.Opacity = 1; Panel.SetZIndex(held, 0);
            if (d.To != d.From) _svc.MoveClient(d.Name, d.To);   // 줄들은 이미 최종 위치에 있으므로 다시 그려져도 튀지 않는다
            else foreach (var it in d.Items) it.RenderTransform = Transform.Identity;
        });
    }

    private static void Slide(UIElement el, double to, Action? done = null)
    {
        var t = (TranslateTransform)el.RenderTransform;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) anim.Completed += (_, _) => done();
        t.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private ListBoxItem? RowContainer(string character) =>
        ClientList.ItemsSource is List<PreviewRow> rows && rows.Find(r => r.Character == character) is { } row
            ? ClientList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem : null;

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}
