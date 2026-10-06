using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Image = System.Windows.Controls.Image;

namespace Argus.Modules.Capture;

/// <summary>현재 감시 중인 영역만 잘라서 실시간으로 보여주는 창. 저장이나 감시에는 영향을 주지 않는다.</summary>
internal sealed class RegionPreviewWindow : Window
{
    private readonly CaptureService _service;
    private readonly ClientCaptureConfig _cfg;
    private readonly string _character;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    private readonly TextBlock _info = new() { FontSize = 12 };
    private readonly TextBlock _note = new() { Foreground = Brushes.Gray, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _busy;

    public RegionPreviewWindow(CaptureService service, ClientCaptureConfig cfg, string character)
    {
        _service = service; _cfg = cfg; _character = character;
        Title = $"감시 영역 미리보기 — {character}";
        ShowInTaskbar = false;
        Argus.Ui.DarkTitleBar.Apply(this);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        MinWidth = 320; MinHeight = 160;

        // 영역 크기에 맞춰 시작 크기를 정한다 (작은 영역은 확대, 큰 영역은 축소).
        var scale = Math.Clamp(Math.Min(900.0 / cfg.RoiW, 600.0 / cfg.RoiH), 1.0, 4.0);
        Width = Math.Clamp(cfg.RoiW * scale + 32, 360, 1000);
        Height = Math.Clamp(cfg.RoiH * scale + 90, 240, 720);

        if (TryFindResource("Bg") is Brush bg) Background = bg;
        if (TryFindResource("Text") is Brush fg) { Foreground = fg; _info.Foreground = TryFindResource("TextDim") as Brush ?? fg; }

        var rootPanel = new DockPanel { Margin = new Thickness(12) };
        _info.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_info, Dock.Top);
        rootPanel.Children.Add(_info);

        var frame = new Grid { Background = Brushes.Black };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor); // 확대해도 픽셀이 흐려지지 않게
        frame.Children.Add(_image);
        frame.Children.Add(_note);
        rootPanel.Children.Add(frame);
        Content = rootPanel;

        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        _info.Text = $"{_cfg.RoiW} × {_cfg.RoiH}  @ ({_cfg.RoiX}, {_cfg.RoiY})";
        try
        {
            var frame = await Task.Run(() => _service.Grab(_character));
            if (frame == null)
            {
                _note.Text = _service.IsMinimized(_character) ? "창이 최소화되어 캡처할 수 없습니다" : "프레임을 기다리는 중…";
                return;
            }
            _note.Text = "";

            // 창 크기가 바뀌어 영역이 벗어나면 잘라 맞춘다 (모니터와 같은 규칙).
            var (x, y, w, h) = _cfg.ResolveRoi(frame.Width, frame.Height);
            var pixels = ChangeDetector.Crop(frame, x, y, w, h);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, w * 4);
            bmp.Freeze();
            _image.Source = bmp;
        }
        finally { _busy = false; }
    }
}
