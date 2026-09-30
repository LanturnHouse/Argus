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
/// 그 위에 하단 수치 바(받는 DPS · 받는 LOGI · 받는 뉴트)와 오른쪽 위 대각 태클 리본(HIC · SCRAM · DISRUPT)이 층으로 얹힌다.
/// 레드박싱 색조는 이후 단계에서 추가된다 (리본은 항상 그 위에 그린다).
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
    private static readonly Brush RibbonHic = Frozen(0xE5, 0x48, 0x4D), RibbonScram = Frozen(0x2F, 0x7B, 0xFF), RibbonDisrupt = Frozen(0x2F, 0xBF, 0x5B);
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    private readonly Grid _canvas = new() { Width = DesignWidth, Height = 118, ClipToBounds = true };
    private readonly Border _bar = new()
    {
        // 아래쪽 5px 은 비워 둔다: 활성 클라이언트의 흰 테두리(맨 위 층)가 수치를 가리지 않게 글자를 위로 올린다.
        Height = 27, Padding = new Thickness(0, 0, 0, 5), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
        Background = new LinearGradientBrush(Color.FromArgb(0xB8, 0, 0, 0), Color.FromArgb(0xEB, 0, 0, 0), 90),
    };
    private readonly Grid _ribbons = new() { Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.6 } };   // 그림자는 리본 묶음에 한 번만 (리본 사이에 어두운 선이 생기지 않게)

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
        _canvas.Children.Add(_ribbons);   // 리본은 뒤쪽: 색조 위(색조가 리본 색을 바꾸지 않게), 이름 라벨과 하단 수치 아래(리본에 가려지지 않게)
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

    /// <summary>전투 수치와 태클 리본을 갱신한다. 수치는 snapshot 이 null 이면(전투 로그를 못 읽는 중) 그리지 않고, 태클 리본은 수치와 별개로 그린다.</summary>
    public void SetCombat(CombatSnapshot? s, TackleFlags tackle, HudFlags flags)
    {
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

        // 오른쪽 위 대각 리본: HIC → 스크램블 → 디스럽터 순으로 모서리에서 안쪽으로 겹치지 않게 쌓는다. 색만으로 구분하지 않도록 글자도 함께 적는다.
        _ribbons.Children.Clear();
        if (!flags.Tackle) return;
        var index = 0;
        if (tackle.Hic) AddRibbon("HIC", RibbonHic, index++);
        if (tackle.Scram) AddRibbon("SCRAM", RibbonScram, index++);
        if (tackle.Disrupt) AddRibbon("DISRUPT", RibbonDisrupt, index++);
    }

    internal static Brush NeutColor(double v) => Math.Round(v) > 0 ? ColorNeutDrain : Math.Round(v) < 0 ? ColorNeutGain : ColorNeut;

    private static UIElement Cell(string icon, double value, Brush color, bool neut = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = icon, FontSize = 9, Foreground = color, Opacity = 0.85, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = neut ? FormatNeut(value) : Format(value), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
        // 값이 0 이면 흐리게 (수치가 없다는 것이 한눈에 보이게)
        var border = new Border { Child = sp, Opacity = Math.Abs(Math.Round(value)) <= 0 ? 0.35 : 1, BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), BorderThickness = new Thickness(1, 0, 0, 0) };
        return border;
    }

    private void AddRibbon(string text, Brush color, int index)
    {
        // 시안: 45도 대각 리본. 리본 높이 = 16, 다음 리본은 대각선에 수직으로 정확히 그 높이만큼 안쪽으로 밀어서 틈 없이 붙인다.
        // 길이는 타일보다 훨씬 길게(300) 만들어 양 끝이 화면 가장자리에서 잘리게 한다 (안쪽 리본이 짧아 끝이 잘려 보이던 문제).
        const double height = 16, length = 300, step = height / 1.41421356;   // 수직 간격 height 를 x, y 이동량으로 (÷√2)
        const double centerRight = 26;   // 첫 리본의 중심이 컨테이너 오른쪽 끝에서 안쪽으로 26px
        var ribbon = new Border
        {
            Width = length, Height = height, Background = color,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            // 중심 위치를 고정한 채 길이만 늘린다: 오른쪽 여백 = centerRight - length / 2, 안쪽으로 갈수록 (왼쪽 아래로) step 씩
            Margin = new Thickness(0, 14 + step * index, centerRight - length / 2 + step * index, 0),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45),
            Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.ExtraBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _ribbons.Children.Add(ribbon);
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

    /// <summary>하단 수치 바와 태클 리본의 투명도 (0.2 ~ 1).</summary>
    public void SetOpacities(double bar, double ribbon)
    {
        _bar.Opacity = bar;
        _ribbons.Opacity = ribbon;
    }

    // 시험용: 지금 화면에 무엇이 그려져 있는지
    internal bool TintVisible => _tint.Visibility == Visibility.Visible;
    internal string? SurgeKeyShown => _surgeKey.Visibility == Visibility.Visible ? _surgeKeyText.Text : null;
    internal int FlashMs => _flashMs;
    internal double SurgeKeyFontSize => _surgeKeyText.FontSize;
    /// <summary>하단 바의 각 칸에 지금 적힌 수치 글자 (왼쪽부터).</summary>
    /// <summary>수치 칸의 글자색 (시험용).</summary>
    internal List<Brush> CellBrushes() => _bar.Child is UniformGrid g
        ? [.. g.Children.OfType<Border>().Select(b => ((TextBlock)((StackPanel)b.Child).Children[1]).Foreground)]
        : [];
    internal List<string> CellTexts() => _bar.Child is UniformGrid g
        ? [.. g.Children.OfType<Border>().Select(b => ((StackPanel)b.Child).Children.OfType<TextBlock>().Last().Text)] : [];
    /// <summary>지금 그려진 리본 글자 (모서리에서 안쪽 순서).</summary>
    internal List<string> RibbonTexts() => [.. _ribbons.Children.OfType<Border>().Select(b => ((TextBlock)b.Child).Text)];
    internal double BarOpacity => _bar.Opacity;
    internal double RibbonOpacity => _ribbons.Opacity;
    internal double BarHeight => _bar.Height;

    /// <summary>타일의 가로/세로 비율에 맞춰 기준 캔버스 높이를 정한다.</summary>
    public void SetAspect(double aspect) => _canvas.Height = DesignWidth / Math.Max(0.5, aspect);
}
