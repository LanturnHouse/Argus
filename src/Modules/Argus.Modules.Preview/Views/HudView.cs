using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Argus.Core.Events;

namespace Argus.Modules.Preview;

/// <summary>
/// 프리뷰 위에 겹쳐 그리는 HUD (투명·클릭 통과 창의 내용). 기준 크기 210px 폭으로 그린 뒤 프리뷰 크기에 맞춰 늘려서
/// 타일이 커지거나 작아져도 같은 비율로 보인다. (A+ 시안: 안쪽 검은 경계선, 이름 라벨 뒤판, 활성 = 흰 이중 테두리)
/// 그 위에 하단 수치 바(받는 DPS · 받는 LOGI · 받는 뉴트)와 레드박싱 색조가 층으로 얹힌다.
/// </summary>
internal sealed class HudView : Grid
{
    public const double DesignWidth = 210;

    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x2E));
    private static readonly Brush Plate = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));

    private static readonly Brush ColorIn = Frozen(0xFF, 0xB4, 0x54), ColorLogi = Frozen(0x9B, 0xE7, 0xC4), ColorNeut = Frozen(0xD6, 0xA8, 0xFF);
    // 받는 뉴트는 부호로 색을 나눈다: 캡이 빠지면(-) 빨강, 내가 빤 양이 더 많아 늘어나면(+) 파랑, 0 이면 기본색
    private static readonly Brush ColorNeutDrain = Frozen(0xFF, 0x5C, 0x5C), ColorNeutGain = Frozen(0x5C, 0xB8, 0xFF);
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
    private static readonly Brush CellDivider = FrozenArgb(0x14, 255, 255, 255);
    private static Brush FrozenArgb(byte a, byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br; }

    private readonly Grid _canvas = new() { Width = DesignWidth, Height = 118, ClipToBounds = true };
    private readonly Border _bar = new()
    {
        // 아래쪽 5px 은 비워 둔다: 활성 클라이언트의 흰 테두리(맨 위 층)가 수치를 가리지 않게 글자를 위로 올린다.
        Height = 27, Padding = new Thickness(0, 0, 0, 5), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
        Background = new LinearGradientBrush(Color.FromArgb(0xB8, 0, 0, 0), Color.FromArgb(0xEB, 0, 0, 0), 90),
    };

    // 레드박싱: 붉은 색조(깜빡임)와 '이 키로 이동' 안내
    private readonly Border _tint = new()
    {
        Margin = new Thickness(0), BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x2A, 0x2A)), BorderThickness = new Thickness(4),
        Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0, 0)), Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _surgeKeyText = new() { FontSize = 16.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)) };
    private readonly Border _surgeKey = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10.5, 1.5, 10.5, 3),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
    };
    private int _flashMs;
    private string _barKey = "";   // 수치 바에 지금 그려진 글자 (같으면 다시 그리지 않는다)
    private readonly TextBlock _name = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Orange };
    private readonly Border _activeWhite = new() { Margin = new Thickness(1), BorderThickness = new Thickness(2), BorderBrush = Brushes.White, Visibility = Visibility.Collapsed };
    private readonly Border _activeInner = new() { Margin = new Thickness(3), BorderThickness = new Thickness(1), BorderBrush = Brushes.Black, Visibility = Visibility.Collapsed };
    private readonly Rectangle _edit = new() { Margin = new Thickness(1), Stroke = Accent, StrokeThickness = 2, StrokeDashArray = [4, 3], Visibility = Visibility.Collapsed };
    private readonly Border _minimized = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), Visibility = Visibility.Collapsed,
        Child = new TextBlock { Text = "최소화됨", Foreground = Brushes.Gainsboro, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    public HudView()
    {
        IsHitTestVisible = false;
        var edge = new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) }; // 타일 경계 (붙여 놓아도 구분되게)
        var label = new Border
        {
            Background = Plate, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(5, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = _name,
        };

        _surgeKey.Child = _surgeKeyText;

        // 아래에서 위로 쌓이는 순서. 활성 클라이언트의 흰 테두리는 수치 바·색조 위, 맨 위에 그려서 가려지지 않게 한다.
        _canvas.Children.Add(_minimized);
        _canvas.Children.Add(edge);
        _canvas.Children.Add(_tint);
        _canvas.Children.Add(label);
        _canvas.Children.Add(_bar);
        _canvas.Children.Add(_surgeKey);
        _canvas.Children.Add(_edit);
        _canvas.Children.Add(_activeWhite);
        _canvas.Children.Add(_activeInner);
        Children.Add(new Viewbox { Stretch = Stretch.Fill, Child = _canvas });
    }

    public void SetName(string name) => _name.Text = name;

    public void SetActive(bool active)
    {
        var v = active ? Visibility.Visible : Visibility.Collapsed;
        _activeWhite.Visibility = v;
        _activeInner.Visibility = v;
    }

    public void SetEdit(bool edit) => _edit.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;

    public void SetMinimized(bool minimized) => _minimized.Visibility = minimized ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 레드박싱 표시. tint: 붉은 색조를 깜빡이는 중인가, flashMs: 깜빡임 한 주기(ms), keyHint: 레드박싱 전환 단축키 안내 문구(null 이면 숨김).
    /// </summary>
    public void SetSurge(bool tint, int flashMs, string? keyHint)
    {
        if (!tint)
        {
            if (_tint.Visibility == Visibility.Visible) { _tint.BeginAnimation(OpacityProperty, null); _tint.Visibility = Visibility.Collapsed; _flashMs = 0; }
        }
        else if (_tint.Visibility != Visibility.Visible || _flashMs != flashMs)
        {
            _flashMs = flashMs;
            _tint.Visibility = Visibility.Visible;
            // 반 주기 켜지고 반 주기 꺼지는 딱딱한 깜빡임 (시안: steps(1))
            var half = TimeSpan.FromMilliseconds(Math.Max(50, flashMs / 2.0));
            var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = new Duration(half * 2), RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            anim.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(half)));
            _tint.BeginAnimation(OpacityProperty, anim);
        }

        _surgeKeyText.Text = keyHint ?? "";
        _surgeKey.Visibility = string.IsNullOrEmpty(keyHint) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>전투 수치를 갱신한다. snapshot 이 null 이면(전투 로그를 못 읽는 중) 그리지 않는다.</summary>
    public void SetCombat(CombatSnapshot? s, HudFlags flags)
    {
        // 표시할 글자가 지난번과 같으면 수치 바를 다시 만들지 않는다
        var key = s == null ? "" : (flags.DpsIn ? "D" + Format(s.DpsIn) + "|" : "") + (flags.Logi ? "L" + Format(s.LogiIn) + "|" : "") + (flags.Neut ? "N" + FormatNeut(s.NeutIn) + "|" : "");
        if (key == _barKey) return;
        _barKey = key;

        // 하단 수치 바: 켜 둔 항목만 같은 폭으로 나눠 그린다
        var cells = new List<UIElement>();
        if (s != null)
        {
            if (flags.DpsIn) cells.Add(Cell("▼", s.DpsIn, ColorIn));
            if (flags.Logi) cells.Add(Cell("✚", s.LogiIn, ColorLogi));
            if (flags.Neut) cells.Add(Cell("⚡", s.NeutIn, NeutColor(s.NeutIn), neut: true));
        }
        if (cells.Count == 0) { _bar.Visibility = Visibility.Collapsed; _bar.Child = null; }
        else
        {
            var grid = new UniformGrid { Rows = 1, Columns = cells.Count };
            foreach (var c in cells) grid.Children.Add(c);
            _bar.Child = grid;
            _bar.Visibility = Visibility.Visible;
        }
    }

    internal static Brush NeutColor(double v) => Math.Round(v) > 0 ? ColorNeutDrain : Math.Round(v) < 0 ? ColorNeutGain : ColorNeut;

    private static UIElement Cell(string icon, double value, Brush color, bool neut = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = icon, FontSize = 9, Foreground = color, Opacity = 0.85, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = neut ? FormatNeut(value) : Format(value), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
        // 값이 0 이면 흐리게 (수치가 없다는 것이 한눈에 보이게)
        var border = new Border { Child = sp, Opacity = Math.Abs(Math.Round(value)) <= 0 ? 0.35 : 1, BorderBrush = CellDivider, BorderThickness = new Thickness(1, 0, 0, 0) };
        return border;
    }

    /// <summary>수치를 천 단위 쉼표가 있는 숫자로 표시한다 (예: 3,000). 음수(노스로 빤 양이 더 많아 캡이 늘어남)는 + 로.</summary>
    internal static string Format(double v)
    {
        if (v < 0) return "+" + Format(-v);
        return Math.Round(v).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 받는 뉴트(이펙티브 캡 변화량)의 표기. 캡이 순수하게 빠지고 있으면 - 를 앞에 붙이고(예: -1,200), 내가 빤 양이 더 많아 캡이 늘어나면 + 를 붙인다(예: +300).
    /// </summary>
    internal static string FormatNeut(double v)
    {
        var r = Math.Round(v);
        if (r > 0) return "-" + Format(r);
        if (r < 0) return "+" + Format(-r);
        return "0";
    }

    /// <summary>하단 수치 바의 투명도 (0.2 ~ 1).</summary>
    public void SetBarOpacity(double bar) => _bar.Opacity = bar;

    // 시험용: 지금 화면에 무엇이 그려져 있는지
    internal bool TintVisible => _tint.Visibility == Visibility.Visible;
    internal string? SurgeKeyShown => _surgeKey.Visibility == Visibility.Visible ? _surgeKeyText.Text : null;
    internal int FlashMs => _flashMs;
    internal double SurgeKeyFontSize => _surgeKeyText.FontSize;
    /// <summary>수치 칸의 글자색 (시험용).</summary>
    internal List<Brush> CellBrushes() => _bar.Child is UniformGrid g
        ? [.. g.Children.OfType<Border>().Select(b => ((TextBlock)((StackPanel)b.Child).Children[1]).Foreground)]
        : [];
    /// <summary>하단 바의 각 칸에 지금 적힌 수치 글자 (왼쪽부터).</summary>
    internal List<string> CellTexts() => _bar.Child is UniformGrid g
        ? [.. g.Children.OfType<Border>().Select(b => ((StackPanel)b.Child).Children.OfType<TextBlock>().Last().Text)] : [];
    internal double BarOpacity => _bar.Opacity;
    internal double BarHeight => _bar.Height;

    /// <summary>타일의 가로/세로 비율에 맞춰 기준 캔버스 높이를 정한다.</summary>
    public void SetAspect(double aspect) => _canvas.Height = DesignWidth / Math.Max(0.5, aspect);
}
