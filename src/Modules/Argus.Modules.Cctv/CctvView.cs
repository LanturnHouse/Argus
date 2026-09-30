using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Argus.Modules.Cctv;

/// <summary>사이드바 'CCTV' 탭: 서비스 시작·종료, 상태와 폴더 연결 확인, 그리고 웹 화면(WebView2).</summary>
internal sealed class CctvView : UserControl
{
    private readonly CctvService _svc;
    private readonly TextBlock _stateText = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statePill = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _toggle = new() { Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _browser = new() { Content = "브라우저로 열기", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _reload = new() { Content = "새로고침", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _log = new() { Content = "로그", Padding = new Thickness(14, 7, 14, 7) };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _alignBar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x2F, 0x14)) };
    private readonly TextBlock _alignText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xC1, 0x5A)) };
    private readonly Button _alignBtn = new() { Content = "Argus 저장 폴더로 맞추기", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBox _logBox = new() { IsReadOnly = true, Visibility = Visibility.Collapsed, Height = 200, Margin = new Thickness(0, 10, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 11, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Grid _content = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _placeholder = new() { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 480 };
    private WebView2? _web;
    private bool _webFailed;

    public CctvView(CctvService svc)
    {
        _svc = svc;
        _placeholder.SetResourceReference(StyleProperty, "Dim");
        _info.SetResourceReference(StyleProperty, "Dim");

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        _statePill.Child = _stateText;
        bar.Children.Add(_statePill); bar.Children.Add(_toggle); bar.Children.Add(_browser); bar.Children.Add(_reload); bar.Children.Add(_log);

        var alertRow = new DockPanel();
        DockPanel.SetDock(_alignBtn, Dock.Right);
        alertRow.Children.Add(_alignBtn); alertRow.Children.Add(_alignText);
        _alignBar.Child = alertRow;

        _content.Children.Add(_placeholder);

        var title = new TextBlock { Text = "CCTV", FontSize = 24, FontWeight = FontWeights.Bold };
        var sub = new TextBlock { Text = "EVE CCTV 웹앱(오버뷰·프로브·도킹 감시)을 Argus 안에서 실행합니다. 화면 감시 캡처가 저장하는 스크린샷을 그대로 분석합니다.", Margin = new Thickness(0, 4, 0, 14), TextWrapping = TextWrapping.Wrap };
        sub.SetResourceReference(StyleProperty, "Dim");

        var top = new StackPanel();
        top.Children.Add(title); top.Children.Add(sub); top.Children.Add(bar); top.Children.Add(_info); top.Children.Add(_alignBar); top.Children.Add(_logBox);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top); root.Children.Add(_content);
        Content = root;

        _toggle.Click += (_, _) => { if (_svc.State is CctvState.Stopped or CctvState.Failed) _svc.Start(); else _svc.Stop(); };
        _browser.Click += (_, _) => Process.Start(new ProcessStartInfo(CctvService.UiUrl) { UseShellExecute = true });
        _reload.Click += (_, _) => _web?.Reload();
        _log.Click += (_, _) => { _logBox.Visibility = _logBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; RefreshLog(); };
        _alignBtn.Click += async (_, _) => await AlignAsync();

        _svc.Changed += OnChanged;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => { };
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var state = _svc.State;
        var (label, bg, fg) = state switch
        {
            CctvState.Running => ("실행 중", Color.FromRgb(0x16, 0x35, 0x27), Color.FromRgb(0x6F, 0xD6, 0xA0)),
            CctvState.Starting => ("시작 중…", Color.FromRgb(0x3A, 0x2F, 0x14), Color.FromRgb(0xF5, 0xC1, 0x5A)),
            CctvState.Failed => ("오류", Color.FromRgb(0x3B, 0x1B, 0x1F), Color.FromRgb(0xFF, 0x8A, 0x8F)),
            _ => ("꺼짐", Color.FromRgb(0x27, 0x2C, 0x37), Color.FromRgb(0xB7, 0xBE, 0xCE)),
        };
        _stateText.Text = label; _stateText.Foreground = new SolidColorBrush(fg); _statePill.Background = new SolidColorBrush(bg);
        _toggle.Content = state is CctvState.Stopped or CctvState.Failed ? "시작" : "중지";
        _browser.IsEnabled = state == CctvState.Running;
        _reload.IsEnabled = state == CctvState.Running && _web != null;

        // 상태 줄
        var s = _svc.Status;
        if (state == CctvState.Running && s != null)
            _info.Text = $"CCTV 폴더: {(s.FolderPath.Length > 0 ? s.FolderPath : "(아직 지정 안 됨)")}  ·  이미지 {s.ImageCount:N0}장  ·  분석 대기 {s.Pending:N0} / 처리 중 {s.Processing:N0} / 완료 {s.Processed:N0} / 실패 {s.Failed:N0}  ·  인식 방식 {s.VisionMode}";
        else _info.Text = state == CctvState.Failed ? _svc.Message : state == CctvState.Starting ? _svc.Message : "";

        // 폴더 불일치 안내 (이미 지정된 폴더를 마음대로 바꾸지 않고 물어본다)
        var mine = _svc.ArgusCaptureFolder();
        var differs = state == CctvState.Running && s is { FolderPath.Length: > 0 } && mine.Length > 0 && !SamePath(s.FolderPath, mine);
        _alignBar.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        if (differs) _alignText.Text = $"웹앱이 읽는 폴더가 Argus 화면 감시의 저장 폴더와 다릅니다.\n웹앱: {s!.FolderPath}\nArgus: {mine}";

        // 웹 화면 / 안내
        if (state == CctvState.Running) _ = ShowWebAsync();
        else
        {
            if (_web != null) _web.Visibility = Visibility.Collapsed;
            _placeholder.Visibility = Visibility.Visible;
            _placeholder.Text = state switch
            {
                CctvState.Starting => "CCTV 웹앱을 시작하는 중입니다…",
                CctvState.Failed => "CCTV 웹앱을 시작하지 못했습니다. 위 메시지를 확인하세요.",
                _ => "CCTV 웹앱이 꺼져 있습니다. '시작'을 누르면 켭니다.\n(설정 > CCTV 에서 Argus 를 켤 때 자동으로 시작하게 할 수 있습니다.)",
            };
        }
        if (_logBox.Visibility == Visibility.Visible) RefreshLog();
    }

    private void RefreshLog()
    {
        _logBox.Text = _svc.RecentLog();
        _logBox.ScrollToEnd();
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return a == b; }
    }

    private async Task AlignAsync()
    {
        var mine = _svc.ArgusCaptureFolder();
        var answer = MessageBox.Show(
            $"웹앱이 읽는 CCTV 폴더를 Argus 화면 감시의 저장 폴더로 바꿉니다.\n\n{mine}\n\n웹앱은 폴더가 바뀌면 그 폴더 기준으로 분석 결과를 새로 만들어서, 지금까지의 분석 결과(감지 이벤트 등)가 초기화됩니다. 계속할까요?",
            "CCTV 폴더 바꾸기", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var error = await _svc.AlignFolderAsync(mine);
        if (error != null) MessageBox.Show("폴더를 바꾸지 못했습니다:\n" + error, "CCTV 폴더 바꾸기", MessageBoxButton.OK, MessageBoxImage.Warning);
        Refresh();
    }

    // ---------- WebView2 (처음 '실행 중'이 되었을 때 만든다: 브라우저 프로세스는 메모리를 쓰므로 CCTV 를 켜지 않으면 만들지 않는다) ----------

    private bool _creating;

    private async Task ShowWebAsync()
    {
        if (_webFailed) { _placeholder.Visibility = Visibility.Visible; return; }
        if (_web != null)
        {
            _web.Visibility = Visibility.Visible; _placeholder.Visibility = Visibility.Collapsed;
            if (_web.Source == null || _web.Source.AbsoluteUri != CctvService.UiUrl) try { _web.Source = new Uri(CctvService.UiUrl); } catch { }
            return;
        }
        if (_creating) return;
        _creating = true;
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Argus", "webview2");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            var web = new WebView2();
            _content.Children.Add(web);
            await web.EnsureCoreWebView2Async(env);
            web.Source = new Uri(CctvService.UiUrl);
            _web = web;
            _placeholder.Visibility = Visibility.Collapsed;
            _reload.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _webFailed = true;
            _placeholder.Visibility = Visibility.Visible;
            _placeholder.Text = "웹 화면을 이 창 안에 표시하지 못했습니다 (WebView2 런타임이 필요합니다). 위의 '브라우저로 열기'로 볼 수 있습니다.\n" + ex.Message;
        }
        finally { _creating = false; }
    }
}
