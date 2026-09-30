using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Argus.Ui;

namespace Argus.Modules.Cctv;

/// <summary>설정 > CCTV: 비전 모델(Ollama) 서버와 모델, 모델을 내리는 시점, 이미지 폴더, 분석 기록.</summary>
internal sealed class CctvSettingsView : UserControl
{
    private readonly CctvService _svc;
    private readonly TextBox _host = new();
    private readonly TextBox _model = new() { MinWidth = 260 };
    private readonly ComboBox _modelPick = new() { MinWidth = 220, Margin = new Thickness(8, 0, 0, 0) };
    private readonly NumberBox _timeout = new() { Minimum = 60, Maximum = 600, Step = 1, Decimals = 0, Unit = "초" };
    private readonly NumberBox _idle = new() { Minimum = 0, Maximum = 600, Step = 1, Decimals = 0, Unit = "초" };
    private readonly TextBlock _testResult = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _loaded = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _folder = new() { IsReadOnly = true };
    private readonly TextBlock _data = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _loading;

    public CctvSettingsView(CctvService svc)
    {
        _svc = svc;
        _testResult.SetResourceReference(StyleProperty, "Dim"); _loaded.SetResourceReference(StyleProperty, "Dim"); _data.SetResourceReference(StyleProperty, "Dim");

        var root = new StackPanel();
        root.Children.Add(UiKit.Section("비전 모델은 이렇게 켜지고 꺼집니다",
            "CCTV 탭에서 '분석 켜기'를 눌러도 모델은 바로 올라가지 않습니다. 읽을 스크린샷이 생겼을 때 올라가고, 대기가 모두 끝난 뒤 아래 유예 시간이 지나면 내려갑니다. " +
            "Argus 를 켜거나 끌 때는 모델을 올리거나 붙들지 않습니다. 분석을 끄거나 Argus 를 닫으면 올라가 있던 모델도 내립니다.", new Border()));

        var hostRow = new DockPanel();
        var test = UiKit.Button("연결 확인", async () => await TestAsync()); test.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(test, Dock.Right);
        hostRow.Children.Add(test); hostRow.Children.Add(_host);
        var hostBox = new StackPanel(); hostBox.Children.Add(hostRow); hostBox.Children.Add(_testResult);   // 결과가 생기면 위쪽 여백을 준다 (빈 줄이 공간을 차지하지 않게)
        root.Children.Add(UiKit.Section("Ollama 서버 주소", "비전 모델을 실행하는 Ollama 서버입니다. 이 PC 의 서버만 쓰며 밖으로 나가는 요청은 없습니다. 기본값은 http://127.0.0.1:11434 입니다.", hostBox));

        var modelRow = new DockPanel();
        var refresh = UiKit.Button("목록 불러오기", async () => await LoadModelsAsync()); refresh.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(refresh, Dock.Right); DockPanel.SetDock(_modelPick, Dock.Right);
        _modelPick.Width = 200; _modelPick.MinWidth = 0; _model.MinWidth = 0;
        modelRow.Children.Add(refresh); modelRow.Children.Add(_modelPick); modelRow.Children.Add(_model);
        root.Children.Add(UiKit.Section("비전 모델", "이미지를 읽을 수 있는(비전) 모델이어야 합니다. 권장: qwen2.5vl:7b (6GB, 그래픽 메모리에 올라가 도킹 숫자는 1초 안에, 오버뷰 한 영역은 행 수에 따라 수 초~수십 초에 읽습니다). 더 큰 모델은 그래픽 메모리를 넘으면 매우 느려집니다.", modelRow));
        root.Children.Add(UiKit.Section("모델 응답 대기 시간", "모델 호출 하나를 이만큼 기다려도 답이 없으면 그 이미지를 잠시 뒤 다시 시도합니다.", _timeout));
        root.Children.Add(UiKit.Section("대기가 끝난 뒤 모델을 내리기까지", "읽을 이미지가 모두 끝난 뒤 이 시간 동안 새 이미지가 없으면 모델을 내려 그래픽 메모리를 비웁니다. 0 이면 바로 내립니다. 스크린샷이 몇 초 간격으로 계속 들어오면 올렸다 내렸다 하지 않도록 조금 두는 것이 좋습니다.", _idle));

        var loadedRow = new StackPanel();
        var unload = UiKit.Button("지금 모델 내리기", async () => { await _svc.Vision.UnloadModelAsync(_svc.Settings.Vision); await Task.Delay(500); await RefreshLoadedAsync(); });
        loadedRow.Children.Add(_loaded); unload.Margin = new Thickness(0, 6, 0, 0); unload.HorizontalAlignment = HorizontalAlignment.Left; loadedRow.Children.Add(unload);
        root.Children.Add(UiKit.Section("지금 올라가 있는 모델", "Ollama 가 지금 그래픽 메모리에 올려 둔 모델입니다 (다른 프로그램이 올린 것도 보입니다).", loadedRow));

        var folderRow = new DockPanel();
        var btns = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(btns, Dock.Right);
        btns.Children.Add(UiKit.Button("변경", PickFolder)); btns.Children.Add(UiKit.Button("자동", () => { _svc.Settings.ImageFolder = ""; Save(); Load(); })); btns.Children.Add(UiKit.Button("열기", () => { if (Directory.Exists(_svc.ImageFolder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_svc.ImageFolder}\"") { UseShellExecute = true }); }));
        ((Button)btns.Children[0]).Margin = new Thickness(8, 0, 0, 0);
        folderRow.Children.Add(btns); folderRow.Children.Add(_folder);
        root.Children.Add(UiKit.Section("분석할 스크린샷 폴더", "'자동'이면 화면 감시 캡처가 CCTV 스크린샷을 저장하는 폴더를 씁니다. 파일 이름이 CCTV{날짜시각}_{캐릭터}.png 인 것만 분석합니다.", folderRow));

        var dataBox = new StackPanel();
        dataBox.Children.Add(_data);
        var reset = UiKit.Button("분석 기록 초기화", ResetData, "DangerButton"); reset.Margin = new Thickness(0, 8, 0, 0); reset.HorizontalAlignment = HorizontalAlignment.Left; dataBox.Children.Add(reset);
        root.Children.Add(UiKit.Section("분석 기록", "이벤트, 현재 대상, 시그니처와 이미지 목록을 지우고 폴더의 이미지를 처음부터 다시 분석하게 합니다. 폴더의 원본 PNG 는 삭제하지 않습니다.", dataBox));

        Content = root;
        Loaded += async (_, _) => { Load(); _timer.Start(); await RefreshLoadedAsync(); await LoadModelsAsync(quiet: true); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) => { await RefreshLoadedAsync(); RefreshData(); };

        _host.LostFocus += (_, _) => { if (!_loading) { _svc.Settings.Vision.Host = _host.Text.Trim(); Save(); } };
        _model.LostFocus += (_, _) => CommitModel();
        _modelPick.SelectionChanged += (_, _) => { if (!_loading && _modelPick.SelectedItem is string picked) { _model.Text = picked; CommitModel(); } };
        _timeout.ValueChanged += (_, _) => { if (!_loading) { _svc.Settings.Vision.TimeoutSeconds = (int)_timeout.Value; Save(); } };
        _idle.ValueChanged += (_, _) => { if (!_loading) { _svc.Settings.UnloadAfterIdleSeconds = (int)_idle.Value; Save(); } };
    }

    private void CommitModel()
    {
        if (_loading) return;
        var name = _model.Text.Trim();
        if (name.Length == 0 || name == _svc.Settings.Vision.Model) return;
        _svc.Settings.Vision.Model = name; Save();
    }

    private void Save() => _svc.SaveSettings();

    private void Load()
    {
        _loading = true;
        var s = _svc.Settings;
        _host.Text = s.Vision.Host; _model.Text = s.Vision.Model; _timeout.Value = s.Vision.TimeoutSeconds; _idle.Value = s.UnloadAfterIdleSeconds;
        _folder.Text = string.IsNullOrWhiteSpace(s.ImageFolder) ? $"(자동) {_svc.ImageFolder}" : s.ImageFolder;
        _loading = false;
        RefreshData();
    }

    private void RefreshData()
    {
        try
        {
            var st = _svc.Status();
            long cropBytes = 0;
            try { if (Directory.Exists(_svc.CropRoot)) cropBytes = new DirectoryInfo(_svc.CropRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { /* 읽는 중 삭제됨 */ }
            _data.Text = $"이미지 {st.ImageCount:N0}장 · 대기 {st.Counts.Pending:N0} · 완료 {st.Counts.Processed:N0} · 실패 {st.Counts.Failed:N0} · 저장된 인식 영역 이미지 {cropBytes / 1024.0 / 1024.0:N1}MB";
        }
        catch { /* 화면 전환 중 */ }
    }

    private async Task TestAsync()
    {
        _svc.Settings.Vision.Host = _host.Text.Trim(); CommitModel(); Save();
        _testResult.Text = "확인하는 중…"; _testResult.Margin = new Thickness(0, 6, 0, 0);
        var (ok, msg) = await _svc.Vision.TestAsync(_svc.Settings.Vision);
        _testResult.Text = (ok ? "✔ " : "✘ ") + msg; _testResult.Margin = new Thickness(0, 6, 0, 0);
        _testResult.Foreground = ok ? UiKit.Good : UiKit.Bad;
        if (ok) await LoadModelsAsync(quiet: true);
    }

    private async Task LoadModelsAsync(bool quiet = false)
    {
        try
        {
            var models = await _svc.Vision.ListModelsAsync(_host.Text.Trim().Length > 0 ? _host.Text.Trim() : _svc.Settings.Vision.Host);
            _loading = true;
            var current = _svc.Settings.Vision.Model;
            _modelPick.Items.Clear();
            foreach (var m in models) _modelPick.Items.Add(m);
            _modelPick.SelectedItem = models.FirstOrDefault(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
            _loading = false;
            if (!quiet) _testResult.Text = $"설치된 모델 {models.Count}개를 불러왔습니다."; _testResult.Margin = new Thickness(0, 6, 0, 0);
        }
        catch (Exception ex) { _loading = false; if (!quiet) { _testResult.Text = ex is HttpRequestException ? "Ollama 에 연결할 수 없습니다." : ex.Message; _testResult.Margin = new Thickness(0, 6, 0, 0); _testResult.Foreground = UiKit.Bad; } }
    }

    private async Task RefreshLoadedAsync()
    {
        try
        {
            var models = await _svc.Vision.LoadedModelsAsync(_svc.Settings.Vision.Host);
            _loaded.Text = models.Count == 0 ? "올라가 있는 모델이 없습니다 (그래픽 메모리 사용 없음)." : string.Join(", ", models);
        }
        catch { _loaded.Text = "Ollama 에 연결할 수 없어 확인하지 못했습니다."; }
    }

    private void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "CCTV 스크린샷 폴더", InitialDirectory = _svc.ImageFolder };
        if (dlg.ShowDialog() != true) return;
        _svc.Settings.ImageFolder = dlg.FolderName; Save(); Load();
    }

    private void ResetData()
    {
        var a = MessageBox.Show("분석 기록(이벤트, 현재 대상, 시그니처, 이미지 목록)을 모두 지우고, 폴더의 이미지를 처음부터 다시 분석하게 할까요?\n원본 PNG 는 삭제하지 않습니다.", "분석 기록 초기화", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (a != MessageBoxResult.Yes) return;
        _svc.ResetAll();
        RefreshData();
    }
}
