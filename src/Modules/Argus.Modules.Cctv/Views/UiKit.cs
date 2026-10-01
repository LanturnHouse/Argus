using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Argus.Modules.Cctv;

/// <summary>CCTV 화면들이 공유하는 작은 UI 부품 (Argus 테마의 Card/Dim 스타일과 같은 색을 쓴다).</summary>
internal static class UiKit
{
    public static Brush Hex(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public static readonly Brush Panel = Hex("#161920"), Panel2 = Hex("#1E222B"), Panel3 = Hex("#272C37"), Line = Hex("#2A2F3A"), TextBrush = Hex("#E8EAF0"), DimBrush = Hex("#8B93A5"),
        Accent = Hex("#4C8DFF"), AccentSoft = Hex("#1F3358"), AccentText = Hex("#9CC1FF"), Good = Hex("#6FD6A0"), GoodBg = Hex("#163527"), Warn = Hex("#F5C15A"), WarnBg = Hex("#3A2F14"),
        Bad = Hex("#FF8A8F"), BadBg = Hex("#3B1B1F"), NeutralBg = Hex("#272C37"), NeutralText = Hex("#B7BECE");

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? color = null, Thickness? margin = null, bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = color ?? TextBrush, VerticalAlignment = VerticalAlignment.Center };
        if (margin != null) t.Margin = margin.Value;
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    public static TextBlock Dim(string text, double size = 12, Thickness? margin = null, bool wrap = true) => Text(text, size, null, DimBrush, margin, wrap);

    public static Border Chip(string text, Brush background, Brush foreground, string? tooltip = null, Thickness? margin = null)
    {
        var b = new Border
        {
            Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = foreground }, Background = background,
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = margin ?? new Thickness(0, 0, 6, 0), ToolTip = tooltip, VerticalAlignment = VerticalAlignment.Center,
        };
        return b;
    }

    /// <summary>색 글자 + 그 색을 어둡게 깐 배경의 알약 (이벤트 종류 표시 등).</summary>
    public static Border TintChip(string text, string colorHex, string? tooltip = null)
    {
        var c = ((SolidColorBrush)Hex(colorHex)).Color;
        var bg = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)); bg.Freeze();
        return Chip(text, bg, Hex(colorHex), tooltip);
    }

    public static Border Card(UIElement child, Thickness? padding = null, Thickness? margin = null)
    {
        var b = new Border { Child = child, Padding = padding ?? new Thickness(16, 14, 16, 14), Margin = margin ?? new Thickness(0, 0, 0, 14) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        return b;
    }

    public static Button Button(string text, Action onClick, string? style = null, double width = double.NaN, bool enabled = true)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsEnabled = enabled };
        if (!double.IsNaN(width)) b.Width = width;
        if (style != null) b.SetResourceReference(FrameworkElement.StyleProperty, style);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>제목 + 설명 + 컨트롤 (설정 화면용).</summary>
    public static UIElement Section(string title, string desc, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        sp.Children.Add(Text(title, 13, FontWeights.SemiBold));
        if (desc.Length > 0) sp.Children.Add(Dim(desc, 12, new Thickness(0, 2, 0, 0)));
        if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(control);
        return sp;
    }

    /// <summary>
    /// 길어질 수 있는 목록을 정해진 높이 안에서 스크롤하게 감싼다. 끝까지 스크롤한 뒤의 휠 입력은 바깥(페이지) 스크롤로 넘겨서 페이지가 멈춘 것처럼 느껴지지 않게 한다.
    /// 화면이 다시 그려져도 보던 자리를 유지하려면 <paramref name="offset"/> 으로 위치를 받고 돌려준다.
    /// </summary>
    public static ScrollViewer InnerScroll(UIElement content, double maxHeight, double offset = 0, Action<double>? onScrolled = null)
    {
        var sv = new ScrollViewer { Content = content, MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) };
        sv.PreviewMouseWheel += (_, e) =>
        {
            var atEdge = sv.ScrollableHeight <= 0 || (e.Delta > 0 && sv.VerticalOffset <= 0) || (e.Delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight);
            if (!atEdge) return;
            e.Handled = true;
            if (sv.Parent is UIElement parent) parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sv });
        };
        if (offset > 0) sv.Loaded += (_, _) => sv.ScrollToVerticalOffset(offset);
        if (onScrolled != null) sv.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) onScrolled(sv.VerticalOffset); };
        return sv;
    }

    public static TextBlock SectionHead(string text, Thickness? margin = null) => Text(text, 15, FontWeights.SemiBold, null, margin ?? new Thickness(0, 0, 0, 2));

    public static Border Divider(Thickness? margin = null) => new() { Height = 1, Background = Line, Margin = margin ?? new Thickness(0, 6, 0, 6) };
}

/// <summary>눌러서 켜고 끄는 필터 버튼 (활성이면 파란 배경).</summary>
internal sealed class FilterChip : Border
{
    private readonly TextBlock _label;
    private bool _active;
    public event Action<bool>? Toggled;

    public FilterChip(string text, bool active = false, string? tooltip = null)
    {
        _label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Child = _label;
        Padding = new Thickness(11, 4, 11, 5);
        Margin = new Thickness(0, 0, 6, 6);
        CornerRadius = new CornerRadius(12);
        Cursor = Cursors.Hand;
        ToolTip = tooltip;
        SetActive(active);
        MouseLeftButtonUp += (_, _) => { SetActive(!_active); Toggled?.Invoke(_active); };
    }

    public bool IsActive => _active;

    public void SetActive(bool active)
    {
        _active = active;
        Background = active ? UiKit.AccentSoft : UiKit.NeutralBg;
        _label.Foreground = active ? UiKit.AccentText : UiKit.NeutralText;
        BorderBrush = active ? UiKit.Accent : Brushes.Transparent;
        BorderThickness = new Thickness(1);
    }
}

/// <summary>눌러 볼 수 있는 행 (마우스를 올리면 배경이 바뀐다).</summary>
internal sealed class RowButton : Border
{
    public event Action? Clicked;

    public RowButton(UIElement content)
    {
        Child = content;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        Padding = new Thickness(8, 7, 8, 7);
        CornerRadius = new CornerRadius(6);
        MouseEnter += (_, _) => Background = UiKit.Panel2;
        MouseLeave += (_, _) => Background = Brushes.Transparent;
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
    }
}
