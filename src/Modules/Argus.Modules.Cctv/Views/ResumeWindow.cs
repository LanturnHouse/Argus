using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Argus.Modules.Cctv;

/// <summary>
/// 일시중지한 감시를 다시 시작하는 창. 1단계: 어느 스크린샷부터 다시 분석할지 고른다 (일시중지 시각 전후의 스크린샷을 늘어놓는다).
/// 2단계: 그 스크린샷 위에 인식 영역을 확인한다 — 이전 영역이 그대로 선택돼 있어 그대로 시작하거나, 지우고 새로 그리면 그 지점부터 새 영역으로 분석한다.
/// 앞의 분석 결과는 그대로 둔다 (이미 분석한 스크린샷부터 시작하면 그 지점부터 다시 분석한다).
/// </summary>
internal static class ResumeWindow
{
    public static void ShowFor(Window? owner, CctvService svc, Watcher watcher)
    {
        var (pausedAt, before, after) = svc.RestartCandidates(watcher);
        ImageRow? picked = null;          // null: 지금 이후에 촬영되는 스크린샷부터
        var host = new ContentControl();
        Window window = null!;

        var all = before.Concat(after).ToList();   // 확대해서 넘겨 볼 순서 (일시중지 시각 앞 → 뒤)

        UIElement PickerPage()
        {
            var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            VirtualizingPanel.SetIsVirtualizing(list, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

            ListBoxItem Add(UIElement content, int? imageIndex)
            {
                var item = new ListBoxItem { Content = content, Padding = new Thickness(8, 6, 8, 6), Cursor = imageIndex != null ? System.Windows.Input.Cursors.Hand : null, IsHitTestVisible = imageIndex != null, Focusable = false };
                if (imageIndex is { } i) item.PreviewMouseLeftButtonUp += (_, _) => { list.SelectedItem = null; host.Content = ViewerPage(i); };   // 클릭하면 확대해서 확인한다
                list.Items.Add(item);
                return item;
            }

            // '지금 이후' 는 목록 위에 따로 고정해 둔다 (목록은 일시중지 시각 근처로 스크롤돼 열린다). 누르면 바로 인식 영역 확인으로 간다.
            var nowBox = new RowButton(NowRow()) { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(1), BorderBrush = UiKit.Accent, Background = UiKit.AccentSoft };
            nowBox.Clicked += () => { picked = null; host.Content = RegionPage(); };

            int index = 0;
            foreach (var b in before) Add(ImageRow(svc, b.Image, b.Status), index++);
            var separator = Add(UiKit.Text($"━━  감시를 일시중지한 시각  {(pausedAt.Length >= 19 ? pausedAt.Substring(11, 8) : pausedAt)}  ━━", 12, FontWeights.SemiBold, UiKit.Warn, new Thickness(0, 6, 0, 6)), null);
            foreach (var a in after) Add(ImageRow(svc, a.Image, a.Status), index++);
            list.Loaded += (_, _) => list.ScrollIntoView(separator);

            var cancel = UiKit.Button("취소", () => window.Close(), "GhostButton");
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            footer.Children.Add(cancel);

            var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
            var head = new StackPanel();
            head.Children.Add(UiKit.Text($"감시 재시작 — {watcher.Label}", 18, FontWeights.Bold));
            head.Children.Add(UiKit.Dim("자리를 잡은 뒤 처음 보이는 스크린샷을 찾으세요. 목록에서 스크린샷을 누르면 크게 보면서 이전/다음으로 넘겨 볼 수 있고, 가운데 '확인'으로 그 스크린샷부터 다시 분석합니다. 목록의 위쪽은 일시중지 전, 아래쪽은 일시중지 뒤에 찍힌 것입니다.", 12, new Thickness(0, 2, 0, 12)));
            head.Children.Add(nowBox);
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(head); root.Children.Add(footer); root.Children.Add(list);
            return root;
        }

        // 확대 보기: 스크린샷 한 장을 크게 보여 주고 < 확인 > 로 이전 · 선택 · 다음을 한다 (← → Enter Esc 도 된다).
        UIElement ViewerPage(int startIndex)
        {
            var index = startIndex;
            var image = new Image { Stretch = Stretch.Uniform };
            var title = UiKit.Text("", 15, FontWeights.Bold);
            var meta = UiKit.Dim("", 12, new Thickness(0, 2, 0, 8), false);
            var prev = UiKit.Button("<", () => { }, null, 64); var next = UiKit.Button(">", () => { }, null, 64);
            var ok = UiKit.Button("확인", () => { }, "PrimaryButton", 200);
            var list = UiKit.Button("← 목록", () => host.Content = PickerPage(), "GhostButton");
            foreach (var b in new[] { prev, next, ok }) { b.Padding = new Thickness(0, 9, 0, 9); b.Margin = new Thickness(6, 0, 6, 0); b.FontSize = 15; }

            void Show()
            {
                var it = all[index];
                image.Source = DialogKit.LoadImage(it.Image.FilePath);
                title.Text = $"{it.Image.CapturedAt.Substring(11, 12)}   ({index + 1} / {all.Count})";
                var state = it.Status switch { "processed" => "분석됨", "skipped" => "건너뜀", "failed" => "실패", _ => "대기" };
                var side = string.CompareOrdinal(it.Image.CapturedAt, pausedAt) <= 0 ? "일시중지 전" : "일시중지 뒤";
                meta.Text = $"{it.Image.Filename}  ·  {state}  ·  {side}";
                prev.IsEnabled = index > 0; next.IsEnabled = index < all.Count - 1;
            }
            void Move(int d) { var n = index + d; if (n < 0 || n >= all.Count) return; index = n; Show(); }
            void Choose() { picked = all[index].Image; host.Content = RegionPage(); }
            prev.Click += (_, _) => Move(-1); next.Click += (_, _) => Move(1); ok.Click += (_, _) => Choose();
            Show();

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            bar.Children.Add(prev); bar.Children.Add(ok); bar.Children.Add(next);
            var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            footer.Children.Add(bar);
            list.HorizontalAlignment = HorizontalAlignment.Left; list.VerticalAlignment = VerticalAlignment.Center; list.Margin = new Thickness(0, 12, 0, 0);
            footer.Children.Add(list);

            var head = new StackPanel();
            head.Children.Add(title); head.Children.Add(meta);
            var frame = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(6), Child = image };
            var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18), Focusable = true };
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(head); root.Children.Add(footer); root.Children.Add(frame);
            root.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Left) { Move(-1); e.Handled = true; }
                else if (e.Key == System.Windows.Input.Key.Right) { Move(1); e.Handled = true; }
                else if (e.Key == System.Windows.Input.Key.Enter) { Choose(); e.Handled = true; }
                else if (e.Key == System.Windows.Input.Key.Escape) { host.Content = PickerPage(); e.Handled = true; }
            };
            root.Loaded += (_, _) => root.Focus();
            return root;
        }

        UIElement NowRow()
        {
            var sp = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
            sp.Children.Add(UiKit.Text("지금 이후에 촬영되는 스크린샷부터", 13.5, FontWeights.SemiBold, UiKit.AccentText));
            sp.Children.Add(UiKit.Dim("가장 흔한 선택입니다. 쌓여 있는 이전 스크린샷은 건너뛰고, 지금부터 새로 찍히는 것부터 분석합니다. 누르면 바로 인식 영역 확인으로 넘어갑니다.", 12, new Thickness(0, 2, 0, 0)));
            return sp;
        }

        UIElement RegionPage()
        {
            var editor = new RegionEditorControl();
            var startPath = picked != null ? picked.FilePath : svc.LatestImageId(watcher.Character) is { } id ? svc.Store.ImagePath(id) : null;
            editor.SetImage(DialogKit.LoadImage(startPath));
            editor.Regions.AddRange(svc.Store.LatestRegions(watcher.Id));   // 이전 영역이 그대로 선택돼 있다
            var original = svc.Store.LatestRegions(watcher.Id);

            var status = UiKit.Text("", 12.5, FontWeights.SemiBold, UiKit.Good);
            var error = UiKit.Text("", 12, FontWeights.Normal, UiKit.Bad, null, true);
            void Refresh()
            {
                var same = CctvStore.SameRegions(editor.Regions, original);
                status.Text = same ? "인식 영역: 이전 영역 그대로" : $"인식 영역: 새 영역으로 변경됨 (오버뷰 {editor.Regions.Count(r => r.Kind == RegionKind.Overview)} · 프로빙 {editor.Regions.Count(r => r.Kind == RegionKind.Probe)} · 도킹 {editor.Regions.Count(r => r.Kind == RegionKind.Dock)})";
                status.Foreground = same ? UiKit.Good : UiKit.Warn;
            }
            editor.Changed += Refresh; Refresh();

            var chips = new WrapPanel();
            var kindChips = new List<FilterChip>();
            foreach (var kind in new[] { RegionKind.Overview, RegionKind.Probe, RegionKind.Dock })
            {
                var chip = new FilterChip(kind.Label(), editor.CurrentKind == kind);
                var captured = kind;
                chip.Toggled += _ => { editor.CurrentKind = captured; foreach (var c in kindChips) c.SetActive(false); chip.SetActive(true); };
                kindChips.Add(chip); chips.Children.Add(chip);
            }
            var clear = new FilterChip("모두 지우고 다시 그리기", false);
            clear.Toggled += _ => { editor.Regions.Clear(); editor.Redraw(); Refresh(); clear.SetActive(false); };
            chips.Children.Add(clear);

            var back = UiKit.Button("←  이전", () => host.Content = PickerPage(), "GhostButton");
            var start = UiKit.Button("감시 시작", () => { }, "PrimaryButton");
            start.Click += async (_, _) =>
            {
                error.Text = "";
                if (editor.Regions.Count == 0) { error.Text = "인식 영역을 하나 이상 지정해주세요."; return; }
                start.IsEnabled = false; back.IsEnabled = false;
                try { await svc.ResumeWatchingAsync(watcher, picked, [.. editor.Regions]); window.Close(); }
                catch (Exception ex) { error.Text = "재시작하지 못했습니다: " + ex.Message; start.IsEnabled = true; back.IsEnabled = true; }
            };
            var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(back); buttons.Children.Add(start);
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            var statusBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            statusBox.Children.Add(status); statusBox.Children.Add(error);
            footer.Children.Add(statusBox);

            var from = picked == null ? "지금 이후에 촬영되는 스크린샷부터 (화면은 가장 최근 스크린샷)" : $"{picked.CapturedAt.Substring(11, 12)} 스크린샷부터{(picked.CapturedAt.CompareTo(pausedAt) < 0 ? " (이미 분석한 구간 — 그 지점부터 다시 분석)" : "")}";
            var head = new StackPanel();
            head.Children.Add(UiKit.Text($"인식 영역 확인 — {from}", 15, FontWeights.Bold));
            head.Children.Add(UiKit.Dim("이전에 지정한 영역이 그대로 선택돼 있습니다. 그대로 시작하면 이전 영역으로 이어 가고, 지우고 새로 그리면 이 지점부터 새 영역으로 분석합니다. 위치를 옮긴 경우 스크린샷 위에서 영역을 다시 그리세요.", 12, new Thickness(0, 2, 0, 10)));
            head.Children.Add(chips);

            var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(head); root.Children.Add(footer); root.Children.Add(editor);
            return root;
        }

        host.Content = PickerPage();
        window = DialogKit.Create(owner, $"감시 재시작 — {watcher.Label}", 1040, 800, host);
        window.ShowDialog();
    }

    private static UIElement ImageRow(CctvService svc, ImageRow image, string status)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var thumb = new Image { Width = 144, Height = 81, Stretch = Stretch.UniformToFill };
        thumb.Loaded += (_, _) =>
        {
            if (thumb.Source != null) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.UriSource = new Uri(Path.GetFullPath(image.FilePath)); bmp.DecodePixelWidth = 288; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; bmp.EndInit(); bmp.Freeze();
                thumb.Source = bmp;
            }
            catch { /* 파일이 없으면 빈 칸 */ }
        };
        var frame = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(4), Child = thumb, Width = 144, Height = 81, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        Grid.SetColumn(frame, 0); grid.Children.Add(frame);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(UiKit.Text(image.CapturedAt.Substring(11, 12), 14, FontWeights.SemiBold));
        info.Children.Add(UiKit.Dim(image.Filename, 11.5, new Thickness(0, 2, 0, 4), false));
        var (label, bg, fg) = status switch
        {
            "processed" => ("분석됨", UiKit.GoodBg, UiKit.Good),
            "skipped" => ("건너뜀", UiKit.NeutralBg, UiKit.NeutralText),
            "failed" => ("실패", UiKit.BadBg, UiKit.Bad),
            _ => ("대기", UiKit.WarnBg, UiKit.Warn),
        };
        var chip = UiKit.Chip(label, bg, fg); chip.HorizontalAlignment = HorizontalAlignment.Left;
        info.Children.Add(chip);
        Grid.SetColumn(info, 1); grid.Children.Add(info);
        return grid;
    }
}
