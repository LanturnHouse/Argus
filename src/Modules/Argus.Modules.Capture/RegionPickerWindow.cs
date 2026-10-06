using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace Argus.Modules.Capture;

/// <summary>
/// 클라이언트 화면을 캡처한 스냅샷을 전체 화면으로 띄우고, 드래그로 감시 영역을 고르게 하는 창.
/// 결과는 클라이언트 픽셀 좌표(스냅샷 기준)로 돌려준다.
/// </summary>
internal sealed class RegionPickerWindow : Window
{
    private readonly BitmapSource _shot;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly System.Windows.Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Rectangle _rect = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)),
        StrokeThickness = 2,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _size = new() { Foreground = Brushes.White, FontSize = 13 };
    private readonly Button _ok = new() { Content = "확인 (Enter)", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private Point? _dragStart;

    /// <summary>선택된 영역 (스냅샷 픽셀 좌표). 취소하면 null.</summary>
    public Int32Rect? Result { get; private set; }

    private Int32Rect? _selection;

    public RegionPickerWindow(BitmapSource shot, Int32Rect? current)
    {
        _shot = shot;
        _image.Source = shot;
        _selection = current;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        WindowState = WindowState.Maximized;
        Title = "감시 영역 선택";

        var root = new Grid();
        root.Children.Add(_image);
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_rect);
        root.Children.Add(_canvas);
        root.Children.Add(BuildHint());
        root.Children.Add(BuildBar());
        Content = root;

        _canvas.MouseLeftButtonDown += Down;
        _canvas.MouseMove += Move;
        _canvas.MouseLeftButtonUp += Up;
        _canvas.SizeChanged += (_, _) => Redraw();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; }
            else if (e.Key == Key.Enter && _selection != null) Confirm();
        };
        Loaded += (_, _) => { Redraw(); Focus(); };
    }

    private UIElement BuildHint()
    {
        var t = new TextBlock
        {
            Text = "드래그로 영역 선택",
            Foreground = Brushes.White,
            FontSize = 15,
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x16, 0x19, 0x20)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 10, 18, 10),
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = t,
        };
    }

    private UIElement BuildBar()
    {
        var cancel = new Button { Content = "취소 (Esc)", Padding = new Thickness(14, 8, 14, 8) };
        if (TryFindResource("PrimaryButton") is Style primary) _ok.Style = primary;

        _ok.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => DialogResult = false;

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(_size);
        _size.Margin = new Thickness(0, 0, 18, 0);
        _size.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(_ok);
        panel.Children.Add(cancel);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x16, 0x19, 0x20)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 12, 18, 12),
            Margin = new Thickness(0, 0, 0, 28),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = panel,
        };
    }

    private void Confirm()
    {
        Result = _selection;
        DialogResult = true;
    }

    // ---- 좌표 변환 ----

    private (double Scale, double OffX, double OffY) Layout()
    {
        var s = Math.Min(_canvas.ActualWidth / _shot.PixelWidth, _canvas.ActualHeight / _shot.PixelHeight);
        return (s, (_canvas.ActualWidth - _shot.PixelWidth * s) / 2, (_canvas.ActualHeight - _shot.PixelHeight * s) / 2);
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_canvas);
        _canvas.CaptureMouse();
    }

    private void Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is not { } a) return;
        _selection = ToPixels(a, e.GetPosition(_canvas));
        Redraw();
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } a) return;
        _dragStart = null;
        _canvas.ReleaseMouseCapture();
        var r = ToPixels(a, e.GetPosition(_canvas));
        _selection = r is { Width: >= 4, Height: >= 4 } ? r : null; // 클릭만 한 경우는 무시
        Redraw();
    }

    private Int32Rect ToPixels(Point a, Point b)
    {
        var (s, ox, oy) = Layout();
        var x1 = Math.Clamp((Math.Min(a.X, b.X) - ox) / s, 0, _shot.PixelWidth);
        var y1 = Math.Clamp((Math.Min(a.Y, b.Y) - oy) / s, 0, _shot.PixelHeight);
        var x2 = Math.Clamp((Math.Max(a.X, b.X) - ox) / s, 0, _shot.PixelWidth);
        var y2 = Math.Clamp((Math.Max(a.Y, b.Y) - oy) / s, 0, _shot.PixelHeight);
        return new Int32Rect((int)x1, (int)y1, (int)(x2 - x1), (int)(y2 - y1));
    }

    private void Redraw()
    {
        var (s, ox, oy) = Layout();
        var full = new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight);
        if (_selection is not { } r)
        {
            _rect.Visibility = Visibility.Collapsed;
            _dim.Data = null; // 선택 전에는 원본 밝기 그대로 보여준다
            _ok.IsEnabled = false;
            _size.Text = "선택된 영역 없음";
            return;
        }

        var x = ox + r.X * s; var y = oy + r.Y * s; var w = r.Width * s; var h = r.Height * s;
        Canvas.SetLeft(_rect, x); Canvas.SetTop(_rect, y);
        _rect.Width = w; _rect.Height = h;
        _rect.Visibility = Visibility.Visible;

        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(new RectangleGeometry(full));
        g.Children.Add(new RectangleGeometry(new Rect(x, y, w, h)));
        _dim.Data = g;

        _ok.IsEnabled = r.Width >= 4 && r.Height >= 4;
        _size.Text = $"{r.Width} × {r.Height}  @ ({r.X}, {r.Y})";
    }
}
