using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Argus.Core.Modules;

namespace Argus.Modules.Cctv;

public enum CctvState { Stopped, Starting, Running, Failed }

/// <summary>웹앱 서비스가 알려 준 현재 상태 (GET /api/status 에서 읽은 것).</summary>
public sealed record CctvStatus(string FolderPath, int ImageCount, int Pending, int Processing, int Processed, int Failed, string VisionMode);

/// <summary>
/// EVE CCTV 웹앱을 Argus 가 대신 실행·감시한다: Node 서비스(8765)와 웹 화면(5173)을 띄우고, 둘 다 응답할 때까지 기다리고, 죽으면 알리고, 끌 때 같이 끈다.
/// 웹앱 코드는 고치지 않고 그대로 쓴다. 웹앱과는 HTTP(localhost)로만 이야기한다.
/// </summary>
public sealed class CctvService : IDisposable
{
    private const string SettingsKey = "cctv";
    private const int StartTimeoutSeconds = 120;

    private readonly IModuleContext _ctx;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly object _lock = new();
    private JobObject? _job;
    private NodeProcess? _service, _ui;
    private CancellationTokenSource? _cts;

    public CctvSettings Settings { get; }
    public CctvState State { get; private set; } = CctvState.Stopped;
    public string Message { get; private set; } = "";
    public CctvStatus? Status { get; private set; }

    /// <summary>상태가 바뀔 때 (다른 스레드에서 올 수 있다).</summary>
    public event Action? Changed;

    public CctvService(IModuleContext ctx)
    {
        _ctx = ctx;
        Settings = ctx.Settings.Load<CctvSettings>(SettingsKey);
    }

    public void SaveSettings() => _ctx.Settings.Save(SettingsKey, Settings);

    public static string UiUrl => $"http://localhost:{CctvSettings.UiPort}/";

    /// <summary>Argus 화면 감시 캡처가 PNG 를 저장하는 폴더 (캡처 모듈의 설정을 읽기만 한다).</summary>
    public string ArgusCaptureFolder() => _ctx.Settings.Load<CaptureFolderProbe>("capture").OutputFolder;
    private sealed class CaptureFolderProbe { public string OutputFolder { get; set; } = ""; }

    // ---------- 시작·종료 ----------

    public void Start()
    {
        lock (_lock)
        {
            if (State is CctvState.Starting or CctvState.Running) return;

            var app = Settings.ResolveAppFolder();
            var node = Settings.ResolveNode();
            if (app == null) { Fail("CCTV 웹앱 폴더를 찾을 수 없습니다. 설정 > CCTV 에서 폴더를 지정하세요. (local-service\\server.mjs 가 있는 곳)"); return; }
            if (node == null) { Fail("node.exe 를 찾을 수 없습니다. Argus 옆 runtime\\node.exe 를 두거나, Node.js 22.13 이상을 설치하거나, 설정 > CCTV 에서 경로를 지정하세요."); return; }
            if (PortInUse(CctvSettings.ServicePort) || PortInUse(CctvSettings.UiPort))
            {
                Fail($"포트 {CctvSettings.ServicePort} 또는 {CctvSettings.UiPort} 를 이미 다른 프로그램이 쓰고 있습니다. 예전 EVE CCTV 실행 창이 켜져 있다면 먼저 종료하세요.");
                return;
            }

            try
            {
                _job = new JobObject();
                _service = new NodeProcess("서비스", node, app, ["--disable-warning=ExperimentalWarning", "local-service/server.mjs"], _job);
                _ui = new NodeProcess("웹 화면", node, app, ["scripts/run-framework.mjs", "dev"], _job);
            }
            catch (Exception ex)
            {
                KillProcesses();
                Fail("실행하지 못했습니다: " + ex.Message);
                return;
            }

            _cts = new CancellationTokenSource();
            Set(CctvState.Starting, "웹앱을 시작하는 중… (처음에는 1분 가까이 걸릴 수 있습니다)");
            var ct = _cts.Token;
            _ = Task.Run(() => MonitorAsync(ct));
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            KillProcesses();
            Status = null;
            Set(CctvState.Stopped, "");
        }
    }

    private void KillProcesses()
    {
        _service?.Dispose(); _ui?.Dispose();
        _service = null; _ui = null;
        _job?.Dispose(); _job = null;   // 남은 자식 프로세스도 함께 끝난다
    }

    private void Fail(string message) => Set(CctvState.Failed, message);

    private void Set(CctvState state, string message)
    {
        State = state; Message = message;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }

    // ---------- 감시 ----------

    private async Task MonitorAsync(CancellationToken ct)
    {
        var started = Environment.TickCount64;
        var failures = 0;
        var lastStatus = 0L;
        var autoAligned = false;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }

            // 자식이 죽었으면 끝까지 기다리지 말고 바로 알린다
            var dead = (_service is { HasExited: true } s ? s : null) ?? (_ui is { HasExited: true } u ? u : null);
            if (dead != null)
            {
                // 메시지는 프로세스를 정리하기 전에 만든다 (정리하면 종료 코드를 읽을 수 없다)
                var tail = string.Join("\n", dead.Lines().TakeLast(6));
                var message = $"'{dead.Name}' 프로세스가 종료되었습니다 (코드 {dead.ExitCode}).\n{tail}";
                lock (_lock) { if (_cts?.IsCancellationRequested != false) return; _cts = null; KillProcesses(); Fail(message); }
                return;
            }

            var serviceOk = await GetOkAsync($"http://127.0.0.1:{CctvSettings.ServicePort}/api/health", ct);
            var uiOk = serviceOk && await GetOkAsync(UiUrl, ct);

            if (serviceOk && uiOk)
            {
                failures = 0;
                if (State != CctvState.Running) { lock (_lock) { if (!ct.IsCancellationRequested) Set(CctvState.Running, ""); } }
                if (Environment.TickCount64 - lastStatus >= 3000)
                {
                    lastStatus = Environment.TickCount64;
                    await RefreshStatusAsync(ct);
                    // 웹앱에 CCTV 폴더가 아직 지정되지 않았으면(처음 쓰는 경우) Argus 화면 감시가 저장하는 폴더로 자동 연결한다. 이미 지정돼 있으면 건드리지 않는다.
                    if (!autoAligned && Status is { FolderPath: "" } && ArgusCaptureFolder() is { Length: > 0 } mine && Directory.Exists(mine))
                    {
                        autoAligned = true;
                        await AlignFolderAsync(mine);
                    }
                }
            }
            else if (State == CctvState.Running && ++failures >= 10)
            {
                lock (_lock) { if (!ct.IsCancellationRequested) Set(CctvState.Starting, "웹앱이 응답하지 않습니다. 다시 연결을 기다리는 중…"); }
            }
            else if (State == CctvState.Starting && Environment.TickCount64 - started > StartTimeoutSeconds * 1000L)
            {
                var tail = string.Join("\n", (_service?.Lines() ?? []).Concat(_ui?.Lines() ?? []).TakeLast(8));
                lock (_lock) { if (ct.IsCancellationRequested) return; _cts = null; KillProcesses(); Fail($"{StartTimeoutSeconds}초 안에 시작되지 않았습니다.\n{tail}"); }
                return;
            }
        }
    }

    private async Task<bool> GetOkAsync(string url, CancellationToken ct)
    {
        try
        {
            using var r = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)r.StatusCode < 500;
        }
        catch { return false; }
    }

    private async Task RefreshStatusAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"http://127.0.0.1:{CctvSettings.ServicePort}/api/status", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
            var folder = root.TryGetProperty("folder", out var f) ? f : default;
            var proc = root.TryGetProperty("processing", out var p) ? p : default;
            Status = new CctvStatus(
                folder.ValueKind == JsonValueKind.Object && folder.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "",
                folder.ValueKind == JsonValueKind.Object ? Int(folder, "imageCount") : 0,
                proc.ValueKind == JsonValueKind.Object ? Int(proc, "pending") : 0,
                proc.ValueKind == JsonValueKind.Object ? Int(proc, "processing") : 0,
                proc.ValueKind == JsonValueKind.Object ? Int(proc, "processed") : 0,
                proc.ValueKind == JsonValueKind.Object ? Int(proc, "failed") : 0,
                root.TryGetProperty("visionMode", out var vm) ? vm.ToString() : "");
            Changed?.Invoke();
        }
        catch { /* 다음 주기에 다시 */ }
    }

    // ---------- 폴더 맞추기 ----------

    /// <summary>
    /// 웹앱이 읽는 CCTV 폴더를 지정한 폴더로 바꾼다. 웹앱은 폴더가 바뀌면 그 폴더 기준의 분석 결과를 새로 만들므로(이전 분석 결과는 초기화된다) 사용자가 확인한 뒤에만 부른다.
    /// 성공하면 null, 실패하면 이유.
    /// </summary>
    public async Task<string?> AlignFolderAsync(string path)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { path }), Encoding.UTF8, "application/json");
            using var r = await _http.PostAsync($"http://127.0.0.1:{CctvSettings.ServicePort}/api/config/folder", content);
            if (r.IsSuccessStatusCode) { await RefreshStatusAsync(CancellationToken.None); return null; }
            return await r.Content.ReadAsStringAsync();
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>두 프로세스가 최근에 낸 출력 (문제가 생겼을 때 원인을 보려고).</summary>
    public string RecentLog() => string.Join("\n", (_service?.Lines() ?? []).Concat(_ui?.Lines() ?? []).TakeLast(120));

    private static bool PortInUse(int port)
    {
        try
        {
            using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start(); l.Stop();
            return false;
        }
        catch { return true; }
    }
}
