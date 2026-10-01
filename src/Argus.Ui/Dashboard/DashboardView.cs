using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Argus.Core.Clients;
using Argus.Core.Dashboard;

namespace Argus.Ui;

/// <summary>
/// 대시보드: 상단 요약 띠와 실행 중인 클라이언트 표(한 줄에 클라이언트 하나). 모듈이 내놓는 정보(<see cref="IDashboardContributor"/>)를 모아서
/// 0.5초마다 갱신하며, 화면에 보이는 내용이 바뀐 경우에만 다시 그린다. 다른 모듈을 직접 알지 못하고, 아무도 채우지 않는 열은 표에서 빠진다.
/// </summary>
public sealed class DashboardView : UserControl
{
    private static readonly (Brush Bg, Brush Fg)[] Tones =
    [
        (Frozen(0x27, 0x2C, 0x37), Frozen(0xB7, 0xBE, 0xCE)),   // Neutral
        (Frozen(0x1F, 0x33, 0x58), Frozen(0x9C, 0xC1, 0xFF)),   // Accent
        (Frozen(0x16, 0x35, 0x27), Frozen(0x6F, 0xD6, 0xA0)),   // Good
        (Frozen(0x3A, 0x2F, 0x14), Frozen(0xF5, 0xC1, 0x5A)),   // Warn
        (Frozen(0x3B, 0x1B, 0x1F), Frozen(0xFF, 0x8A, 0x8F)),   // Bad
    ];
    private static readonly Brush ActiveRow = Frozen(0x1B, 0x2A, 0x45), AlertRow = Frozen(0x2A, 0x17, 0x1B), Line = Frozen(0x22, 0x26, 0x2F);
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    /// <summary>표의 열 순서와 머리글, 최소 너비, 너비 비율.</summary>
    private static readonly (string Key, string Header, double Min, double Weight)[] Columns =
    [
        (DashboardColumns.Cycle, "사이클 · 키", 120, 1.3),
        (DashboardColumns.Dps, "▼ 받는 DPS", 80, 0.9),
        (DashboardColumns.Logi, "✚ LOGI", 70, 0.8),
        (DashboardColumns.Neut, "⚡ 뉴트", 70, 0.8),
        (DashboardColumns.Status, "상태", 140, 1.7),
        (DashboardColumns.Watch, "화면 감시", 170, 2.0),
    ];

    private readonly IClientRegistry _clients;
    private readonly IReadOnlyList<IDashboardContributor> _contributors;
    private readonly WrapPanel _summary = new();
    private readonly Border _tableHost = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _empty = new() { Text = "실행 중인 EVE 클라이언트가 없습니다.", Margin = new Thickness(2, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _signature = "\0";

    public DashboardView(IClientRegistry clients, IEnumerable<IDashboardContributor> contributors)
    {
        _clients = clients;
        _contributors = [.. contributors];

        var title = new TextBlock { Text = "대시보드", FontSize = 24, FontWeight = FontWeights.Bold };
        var sub = new TextBlock { Text = "실행 중인 EVE 클라이언트와 각 기능의 현재 상태", Margin = new Thickness(0, 4, 0, 16) };
        sub.SetResourceReference(StyleProperty, "Dim");
        _empty.SetResourceReference(StyleProperty, "Dim");

        var summaryBar = new Border { Child = _summary, Padding = new Thickness(14, 10, 14, 4), Margin = new Thickness(0, 0, 0, 14) };
        summaryBar.SetResourceReference(StyleProperty, "Card");

        var root = new StackPanel();
        root.Children.Add(title);
        root.Children.Add(sub);
        root.Children.Add(summaryBar);
        root.Children.Add(SectionLabel("클라이언트"));
        root.Children.Add(_tableHost);
        root.Children.Add(_empty);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root, Padding = new Thickness(0, 0, 12, 0) };

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(force: true); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>지금 바로 갱신한다 (시험용).</summary>
    public void RefreshNow() => Refresh(force: true);

    private static TextBlock SectionLabel(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(2, 6, 0, 8) };
        t.SetResourceReference(StyleProperty, "Dim");
        return t;
    }

    // ---------- 모으기 ----------

    private List<DashboardChip> Safe(Func<IDashboardContributor, IReadOnlyList<DashboardChip>> get)
    {
        var list = new List<DashboardChip>();
        foreach (var c in _contributors)
        {
            try { list.AddRange(get(c)); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Dashboard] {c.GetType().Name}: {ex.Message}"); }   // 한 모듈이 실패해도 대시보드는 계속 그린다
        }
        return list;
    }

    private static string Sig(IEnumerable<DashboardChip> chips) => string.Join("\u0001", chips.Select(c => $"{c.Text}|{c.Tone}|{c.Color}|{c.Column}|{c.Plain}"));

    private sealed record Row(EveClient Client, bool Active, List<DashboardChip> Chips);

    private void Refresh(bool force = false)
    {
        var fg = WindowFocus.Foreground;
        var clients = _clients.Current.GroupBy(c => c.Character, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        var summary = new List<DashboardChip> { new($"실행 중 {clients.Count}", ChipTone.Accent) };
        summary.AddRange(Safe(c => c.SummaryChips()));
        var rows = clients.Select(c => new Row(c, c.Hwnd == fg, Safe(x => x.ClientChips(c.Character)))).ToList();

        var sig = string.Join("\u0002", new[]
        {
            Sig(summary),
            string.Join("\u0001", rows.Select(r => $"{r.Client.Character}|{r.Client.ProcessId}|{r.Active}|{Sig(r.Chips)}")),
        });
        if (!force && sig == _signature) return;
        _signature = sig;

        _summary.Children.Clear();
        foreach (var chip in summary) _summary.Children.Add(Chip(chip, new Thickness(0, 0, 8, 6)));

        _tableHost.Child = rows.Count == 0 ? null : BuildTable(rows);
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 그리기 ----------

    private static string ColumnOf(DashboardChip c) => Columns.Any(x => x.Key == c.Column) ? c.Column : DashboardColumns.Status;

    private static UIElement Chip(DashboardChip c, Thickness margin)
    {
        var (bg, fg) = Tones[(int)c.Tone];
        var text = new TextBlock { Text = c.Text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = fg };
        if (c.Color != null) try { text.Foreground = (Brush)new BrushConverter().ConvertFromString(c.Color)!; } catch { }
        if (c.Plain)
        {
            text.FontSize = 13; text.Margin = margin; text.ToolTip = c.Tooltip; text.VerticalAlignment = VerticalAlignment.Center;
            return text;
        }
        return new Border { Child = text, Background = bg, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = margin, ToolTip = c.Tooltip };
    }

    private UIElement BuildTable(List<Row> rows)
    {
        // 아무도 채우지 않는 열(그 모듈이 없음)은 빼고 그린다
        var shown = Columns.Where(col => rows.Any(r => r.Chips.Any(c => ColumnOf(c) == col.Key))).ToList();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.3, GridUnitType.Star), MinWidth = 190 });
        foreach (var col in shown) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(col.Weight, GridUnitType.Star), MinWidth = col.Min });
        int span = grid.ColumnDefinitions.Count;

        // 머리글
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(grid, 0, 0, Header("클라이언트"));
        for (int i = 0; i < shown.Count; i++) AddCell(grid, 0, i + 1, Header(shown[i].Header));
        AddLine(grid, 0, span);

        for (int r = 0; r < rows.Count; r++)
        {
            int gr = r + 1;
            var row = rows[r];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 줄 배경: 지금 앞에 있는 클라이언트는 파랑, 경고(Bad 상태 칩)가 있으면 붉은 기운
            var alert = row.Chips.Any(c => ColumnOf(c) == DashboardColumns.Status && c.Tone == ChipTone.Bad);
            if (row.Active || alert)
            {
                var bg = new Border { Background = alert ? AlertRow : ActiveRow };
                Grid.SetRow(bg, gr); Grid.SetColumnSpan(bg, span);
                grid.Children.Add(bg);
            }

            AddCell(grid, gr, 0, NameCell(row));
            for (int i = 0; i < shown.Count; i++)
            {
                var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 6, 4, 0) };
                foreach (var chip in row.Chips.Where(c => ColumnOf(c) == shown[i].Key)) wrap.Children.Add(Chip(chip, new Thickness(0, 0, 6, 6)));
                AddCell(grid, gr, i + 1, wrap);
            }
            if (r < rows.Count - 1) AddLine(grid, gr, span);
        }

        var card = new Border { Child = grid, Padding = new Thickness(6, 2, 6, 2) };
        card.SetResourceReference(StyleProperty, "Card");
        return card;
    }

    private static void AddCell(Grid g, int row, int col, UIElement e) { Grid.SetRow(e, row); Grid.SetColumn(e, col); g.Children.Add(e); }

    private static void AddLine(Grid g, int row, int span)
    {
        var line = new Border { Height = 1, Background = Line, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(line, row); Grid.SetColumnSpan(line, span);
        g.Children.Add(line);
    }

    private static UIElement Header(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(8, 7, 4, 7) };
        t.SetResourceReference(StyleProperty, "Dim");
        return t;
    }

    private static UIElement NameCell(Row row)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Success");
        var name = new TextBlock { Text = row.Client.Character, FontSize = 14, FontWeight = row.Active ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

        var panel = new DockPanel { Margin = new Thickness(8, 9, 4, 9), ToolTip = $"PID {row.Client.ProcessId}" };
        if (row.Active)
        {
            var pill = Chip(new DashboardChip("활성", ChipTone.Accent, "지금 사용 중인 클라이언트"), new Thickness(8, 0, 0, 0));
            DockPanel.SetDock(pill, Dock.Right);
            panel.Children.Add(pill);
        }
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(dot); left.Children.Add(name);
        panel.Children.Add(left);
        return panel;
    }
}
