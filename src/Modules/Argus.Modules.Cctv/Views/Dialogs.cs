using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Argus.Modules.Cctv;

/// <summary>모든 CCTV 보조 창의 공통 모양 (Argus 테마 배경, 작업 표시줄에 안 뜸, 소유 창 가운데).</summary>
internal static class DialogKit
{
    public static Window Create(Window? owner, string title, double width, double height, UIElement content)
    {
        var w = new Window
        {
            Title = title, Width = width, Height = height, Owner = owner, ShowInTaskbar = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = UiKit.Hex("#0E1014"), Foreground = UiKit.TextBrush, Content = content, MinWidth = 420, MinHeight = 300,
        };
        w.SourceInitialized += (_, _) =>
        {
            try { var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle; var on = 1; DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); } catch { /* 다크 제목 표시줄은 선택 사항 */ }
        };
        return w;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    public static BitmapImage? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.UriSource = new Uri(Path.GetFullPath(path)); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; bmp.EndInit(); bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static UIElement Fact(string label, UIElement value)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(UiKit.Dim(label, 11.5, null, false));
        if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(value);
        return sp;
    }

    public static UIElement Fact(string label, string value) => Fact(label, UiKit.Text(value, 13, null, null, null, true));
}

/// <summary>이벤트 판정 근거: 판정 프레임(인식 영역 표시)과 이전 프레임, 판정 사유.</summary>
internal static class EvidenceWindow
{
    public static void ShowFor(Window? owner, CctvService svc, EventRow e, Func<string?, string> canonical)
    {
        var detail = e.ImageId is { } id ? svc.Store.Image(id) : null;
        var kind = e.Type.StartsWith("signature_") ? RegionKind.Probe : e.Type == "docked" ? RegionKind.Dock : RegionKind.Overview;
        var box = detail?.Observations.FirstOrDefault(o => o.Kind == kind).Payload.SourceBox;

        var left = new StackPanel();
        if (e.PreviousImageId is { } prevId && svc.Store.ImagePath(prevId) is { } prevPath)
        {
            left.Children.Add(UiKit.Dim($"비교: 이전 프레임 · {e.PreviousFilename}", 11.5, new Thickness(0, 0, 0, 4)));
            left.Children.Add(Screenshot(prevPath, null));
        }
        if (detail != null)
        {
            left.Children.Add(UiKit.Dim(e.PreviousImageId != null ? $"판정 프레임 · {e.Filename}" : e.Filename ?? "", 11.5, new Thickness(0, e.PreviousImageId != null ? 12 : 0, 0, 4)));
            left.Children.Add(Screenshot(detail.Image.FilePath, box is null ? null : (box, kind)));
        }
        else left.Children.Add(UiKit.Dim("원본 이미지를 찾을 수 없습니다 (폴더에서 삭제되었을 수 있습니다).", 12));

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        var verification = EventPresentation.Verification(e);
        right.Children.Add(DialogKit.Fact("판정", $"{EventPresentation.Label(e.Type)}{(verification != null ? $" · {verification}" : "")} · 인식 신뢰도 {Math.Round((e.Confidence ?? 0) * 100)}%"));
        right.Children.Add(DialogKit.Fact("캐릭터 / 콥 / 함선", $"{e.Character ?? "미확인"} · {(e.Corporation is { Length: > 0 } c ? $"[{canonical(c)}]" : "—")}\n{EventPresentation.Detail(e)}"));
        right.Children.Add(DialogKit.Fact("감시 눈깔", e.WatcherLabel ?? "미지정 눈깔"));
        right.Children.Add(DialogKit.Fact("판정 시각", e.Time.Replace("T", " ")));
        right.Children.Add(DialogKit.Fact("판정 규칙", EventPresentation.Rule(e)));
        if (e.Details["reason"]?.ToString() is { Length: > 0 } reason) right.Children.Add(DialogKit.Fact("판정 사유 코드", reason));
        if (detail != null) right.Children.Add(UiKit.Button("판정 프레임 원본 열기", () => Open(detail.Image.FilePath)));
        if (e.PreviousImageId is { } p2 && svc.Store.ImagePath(p2) is { } pp) right.Children.Add(UiKit.Button("이전 프레임 원본 열기", () => Open(pp)));

        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(leftScroll, 0); Grid.SetColumn(rightScroll, 1);
        grid.Children.Add(leftScroll); grid.Children.Add(rightScroll);
        DialogKit.Create(owner, $"{EventPresentation.Label(e.Type)} 판정 근거", 1080, 720, grid).ShowDialog();
    }

    private static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* 연결 프로그램 없음 */ } }

    /// <summary>스크린샷을 창 폭에 맞춰 보여 주고, 인식 영역이 있으면 그 자리에 테두리를 그린다.</summary>
    private static UIElement Screenshot(string path, (SourceBox Box, RegionKind Kind)? region)
    {
        var bmp = DialogKit.LoadImage(path);
        if (bmp == null) return UiKit.Dim("이미지를 열 수 없습니다.", 12);
        var grid = new Grid { Background = Brushes.Black };
        var image = new Image { Source = bmp, Stretch = Stretch.Uniform };
        grid.Children.Add(image);
        if (region is { } r)
        {
            var canvas = new Canvas { IsHitTestVisible = false };
            var rect = new System.Windows.Shapes.Rectangle { Stroke = UiKit.Accent, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0x8D, 0xFF)) };
            var label = new Border { Background = UiKit.Accent, Padding = new Thickness(6, 1, 6, 2), Child = new TextBlock { Text = $"{r.Kind.Label()} 추출 영역", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White } };
            canvas.Children.Add(rect); canvas.Children.Add(label);
            grid.Children.Add(canvas);
            void Place()
            {
                double scale = Math.Min(grid.ActualWidth / bmp.PixelWidth, grid.ActualHeight / bmp.PixelHeight);
                if (double.IsNaN(scale) || scale <= 0) return;
                double w = bmp.PixelWidth * scale, h = bmp.PixelHeight * scale, ox = (grid.ActualWidth - w) / 2, oy = (grid.ActualHeight - h) / 2;
                Canvas.SetLeft(rect, ox + r.Box.Left * scale); Canvas.SetTop(rect, oy + r.Box.Top * scale);
                rect.Width = Math.Max(2, r.Box.Width * scale); rect.Height = Math.Max(2, r.Box.Height * scale);
                Canvas.SetLeft(label, ox + r.Box.Left * scale); Canvas.SetTop(label, Math.Max(0, oy + r.Box.Top * scale - 20));
            }
            grid.SizeChanged += (_, _) => Place();
        }
        return grid;
    }

}

/// <summary>요약 카드(현재 도킹 / 감지·미도킹 / 성계 이탈)의 상세 목록.</summary>
internal static class SummaryWindow
{
    public static void ShowFor(Window? owner, CctvService svc, LiveStatus status, List<LatestState> rows, List<DockPeak> peaks)
    {
        var (title, caption) = status switch
        {
            LiveStatus.Docked => ("현재 도킹 확인", "도킹 카운터와 이탈 인원이 일치"),
            LiveStatus.Observed => ("감지 · 미도킹", "현재 위치를 확정하지 못한 대상"),
            _ => ("성계 이탈", "웜홀·게이트·워프 이탈 판정"),
        };
        var box = new StackPanel { Margin = new Thickness(20) };
        box.Children.Add(UiKit.Text($"{title} 상세", 18, FontWeights.Bold));
        box.Children.Add(UiKit.Dim(caption, 12, new Thickness(0, 2, 0, 14)));

        if (status == LiveStatus.Docked)
        {
            box.Children.Add(UiKit.Text("감지된 최고 도킹 수", 13, FontWeights.SemiBold));
            box.Children.Add(UiKit.Dim("스트럭쳐 감시 위치별 최고 기록입니다. 서로 다른 위치의 숫자는 합산하지 않습니다.", 12, new Thickness(0, 2, 0, 8)));
            if (peaks.Count == 0) box.Children.Add(UiKit.Dim("아직 도킹 숫자를 읽은 이미지가 없습니다.", 12, new Thickness(0, 0, 0, 14)));
            foreach (var p in peaks)
            {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                foreach (var w in new[] { -1.0, 70.0, 150.0, 170.0 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
                g.Children.Add(UiKit.Text(p.WatcherLabel, 13, FontWeights.SemiBold));
                var cnt = UiKit.Text($"{p.PeakCount}명", 13, FontWeights.Bold, UiKit.Good); Grid.SetColumn(cnt, 1); g.Children.Add(cnt);
                var tm = UiKit.Dim(p.CapturedAt.Replace("T", " ")[..19], 12, null, false); Grid.SetColumn(tm, 2); g.Children.Add(tm);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(buttons, 3);
                var src = svc.Store.ImagePath(p.ImageId);
                buttons.Children.Add(UiKit.Button("원본 이미지", () => { if (src != null) try { Process.Start(new ProcessStartInfo(src) { UseShellExecute = true }); } catch { } }, null, double.NaN, src != null).Also(b => { b.Padding = new Thickness(8, 2, 8, 2); }));
                var cropPath = svc.Store.Image(p.ImageId)?.Observations.FirstOrDefault(o => o.Id == p.ObservationId).Payload.SourceCropPath;
                buttons.Children.Add(UiKit.Button("인식 영역", () => { if (cropPath != null) try { Process.Start(new ProcessStartInfo(cropPath) { UseShellExecute = true }); } catch { } }, null, double.NaN, cropPath != null && File.Exists(cropPath)).Also(b => { b.Padding = new Thickness(8, 2, 8, 2); }));
                g.Children.Add(buttons);
                box.Children.Add(g);
            }
            box.Children.Add(UiKit.Divider(new Thickness(0, 10, 0, 10)));
        }

        var head = new Grid();
        foreach (var w in new[] { 80.0, -1.0, 150.0, 130.0, 80.0 }) head.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        string[] names = ["콥 티커", "캐릭터", "함선", "감지 위치", "시간"];
        for (int i = 0; i < names.Length; i++) { var t = UiKit.Dim(names[i], 11.5, null, false); Grid.SetColumn(t, i); head.Children.Add(t); }
        box.Children.Add(head);
        box.Children.Add(UiKit.Divider(new Thickness(0, 4, 0, 4)));

        if (rows.Count == 0) box.Children.Add(UiKit.Dim("현재 조건에 해당하는 인원이 없습니다.", 12, new Thickness(0, 8, 0, 0)));
        foreach (var r in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            foreach (var w in new[] { 80.0, -1.0, 150.0, 130.0, 80.0 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
            var cells = new[] { r.Corporation == "미확인" ? "—" : $"[{r.Corporation}]", r.Character, r.Ship, r.Source, Summaries.Time(r.Time) };
            for (int i = 0; i < cells.Length; i++)
            {
                var t = UiKit.Text(cells[i], 12.5, i == 1 ? FontWeights.SemiBold : FontWeights.Normal, i == 0 ? UiKit.AccentText : i >= 3 ? UiKit.DimBrush : null); t.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(t, i); g.Children.Add(t);
            }
            box.Children.Add(g);
        }
        box.Children.Add(UiKit.Dim($"총 {rows.Count}명 · {rows.Select(r => r.Corporation).Distinct().Count()}개 코퍼레이션", 12, new Thickness(0, 14, 0, 0)));
        DialogKit.Create(owner, $"{title} 상세", 840, 640, new ScrollViewer { Content = box, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }).ShowDialog();
    }

    private static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}

/// <summary>코퍼레이션 하나의 현재 인원: 캐릭터별 감지 함선과 전력 요약.</summary>
internal static class CorpWindow
{
    public static void ShowFor(Window? owner, CorpGroup corp)
    {
        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });

        var members = new StackPanel();
        members.Children.Add(UiKit.Text("캐릭터별 감지 함선", 14, FontWeights.SemiBold));
        members.Children.Add(UiKit.Dim("동일 캐릭터·동일 함선의 반복 감지는 한 번만 표시합니다.", 12, new Thickness(0, 2, 0, 10)));
        foreach (var m in corp.Members)
        {
            members.Children.Add(UiKit.Text(m.Name, 13.5, FontWeights.SemiBold, null, new Thickness(0, 6, 0, 2)));
            foreach (var (time, ship, source) in m.Sightings)
            {
                var row = new Grid { Margin = new Thickness(14, 1, 0, 1) };
                foreach (var w in new[] { 70.0, -1.0, 130.0 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
                row.Children.Add(UiKit.Dim(time, 12, null, false));
                var s = UiKit.Text(ship, 12.5, FontWeights.SemiBold); Grid.SetColumn(s, 1); row.Children.Add(s);
                var src = UiKit.Dim(source, 12, null, false); Grid.SetColumn(src, 2); row.Children.Add(src);
                members.Children.Add(row);
            }
        }
        var scroll = new ScrollViewer { Content = members, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var summary = new StackPanel();
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        hero.Children.Add(UiKit.Text(corp.Name, 22, FontWeights.Bold));
        hero.Children.Add(UiKit.Dim(corp.Ticker, 14, new Thickness(8, 6, 0, 0), false));
        summary.Children.Add(hero);
        summary.Children.Add(UiKit.Dim("현재 감지 인원", 12));
        summary.Children.Add(UiKit.Text(corp.Detected.ToString(), 30, FontWeights.Bold, null, new Thickness(0, 0, 0, 12)));
        summary.Children.Add(UiKit.Text("현재 전력 요약", 13, FontWeights.SemiBold));
        summary.Children.Add(UiKit.Dim("마지막 확인 상태 기준", 12, new Thickness(0, 0, 0, 8)));
        summary.Children.Add(UiKit.Text($"도킹 확인  {corp.Docked}", 13, null, UiKit.Good));
        summary.Children.Add(UiKit.Text($"감지 · 미도킹  {corp.Observed}", 13, null, UiKit.Warn, new Thickness(0, 2, 0, 12)));
        foreach (var (ship, count) in corp.Ships.OrderByDescending(s => s.Count))
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var c = UiKit.Text($"× {count}", 12.5, FontWeights.SemiBold); DockPanel.SetDock(c, Dock.Right);
            row.Children.Add(c); row.Children.Add(UiKit.Text(ship, 12.5));
            summary.Children.Add(row);
        }
        Grid.SetColumn(scroll, 0); Grid.SetColumn(summary, 2);
        grid.Children.Add(scroll); grid.Children.Add(summary);
        DialogKit.Create(owner, $"{corp.Name} 전력 상세", 820, 560, grid).ShowDialog();
    }
}
