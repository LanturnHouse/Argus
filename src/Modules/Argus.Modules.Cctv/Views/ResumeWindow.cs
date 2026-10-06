using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Argus.Modules.Cctv;

/// <summary>
/// 일시중지한 감시를 다시 시작한다. 1단계(이 창): 어느 스크린샷부터 다시 분석할지 고른다 (일시중지 시각 전후의 스크린샷을 늘어놓고, 눌러서 크게 보며 고른다).
/// 2단계: 눈깔 수정과 같은 창(<see cref="WatcherWizard"/> 재시작 모드)에서 이름 · 감시 타입 · 인식 영역을 확인한다 — 캐릭터는 바꿀 수 없고, 이전 영역이 그대로 선택돼 있다.
/// 앞의 분석 결과는 그대로 둔다 (이미 분석한 스크린샷부터 시작하면 그 지점부터 다시 분석한다).
/// </summary>
internal static class ResumeWindow
{
    public static void ShowFor(Window? owner, CctvService svc, Watcher watcher)
    {
        while (true)
        {
            var choice = PickStart(owner, svc, watcher);
            if (choice == null) return;
            // 2단계에서 '이전'을 누르면 다시 지점 고르기로 돌아온다.
            if (WatcherWizard.ShowResume(owner, svc, watcher, choice.Value.Image) != ResumeOutcome.Back) return;
        }
    }

    /// <summary>재시작 지점 고르기. 취소하면 null, 고르면 그 이미지(null 이면 '지금 이후').</summary>
    private static (ImageRow? Image, bool Chosen)? PickStart(Window? owner, CctvService svc, Watcher watcher)
    {
        var (pausedAt, dividerAt, before, after) = svc.RestartCandidates(watcher);
        (ImageRow? Image, bool Chosen)? result = null;   // null: 취소
        var host = new ContentControl();
        Window window = null!;
        void Choose(ImageRow? image) { result = (image, true); window.Close(); }

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
            nowBox.Clicked += () => Choose(null);

            int index = 0;
            foreach (var b in before) Add(ImageRow(b.Image, b.Status), index++);
            var separator = Add(UiKit.Text($"━━  여기까지 분석함 (마지막으로 인식한 이미지 {(dividerAt.Length >= 19 ? dividerAt.Substring(11, 8) : dividerAt)})  ·  실제 일시중지 {(pausedAt.Length >= 19 ? pausedAt.Substring(11, 8) : pausedAt)}  ━━", 12, FontWeights.SemiBold, UiKit.Warn, new Thickness(0, 6, 0, 6)), null);
            foreach (var a in after) Add(ImageRow(a.Image, a.Status), index++);
            list.Loaded += (_, _) => list.ScrollIntoView(separator);

            var cancel = UiKit.Button("취소", () => window.Close(), "GhostButton");
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            footer.Children.Add(cancel);

            var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
            var head = new StackPanel();
            head.Children.Add(UiKit.Text($"감시 재시작 — {watcher.Label}", 18, FontWeights.Bold));
            head.Children.Add(UiKit.Dim("자리를 잡은 뒤 처음 보이는 스크린샷을 눌러 '확인'으로 재시작 지점을 고르세요.", 12, new Thickness(0, 2, 0, 12)));
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
                var side = string.CompareOrdinal(it.Image.CapturedAt, dividerAt) <= 0 ? "이미 분석한 구간" : "아직 분석하지 못한 구간";
                meta.Text = $"{it.Image.Filename}  ·  {state}  ·  {side}";
                prev.IsEnabled = index > 0; next.IsEnabled = index < all.Count - 1;
            }
            void Move(int d) { var n = index + d; if (n < 0 || n >= all.Count) return; index = n; Show(); }
            void ChooseThis() => Choose(all[index].Image);
            prev.Click += (_, _) => Move(-1); next.Click += (_, _) => Move(1); ok.Click += (_, _) => ChooseThis();
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
                else if (e.Key == System.Windows.Input.Key.Enter) { ChooseThis(); e.Handled = true; }
                else if (e.Key == System.Windows.Input.Key.Escape) { host.Content = PickerPage(); e.Handled = true; }
            };
            root.Loaded += (_, _) => root.Focus();
            return root;
        }

        UIElement NowRow()
        {
            var sp = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
            sp.Children.Add(UiKit.Text("지금 이후에 촬영되는 스크린샷부터", 13.5, FontWeights.SemiBold, UiKit.AccentText));
            return sp;
        }

        host.Content = PickerPage();
        window = DialogKit.Create(owner, $"감시 재시작 — {watcher.Label}", 1040, 800, host);
        window.ShowDialog();
        return result;
    }

    private static UIElement ImageRow(ImageRow image, string status)
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
