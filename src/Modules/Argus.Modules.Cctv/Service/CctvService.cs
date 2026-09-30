using System.Globalization;
using System.Text.RegularExpressions;
using Argus.Core.Events;
using Argus.Core.Modules;

namespace Argus.Modules.Cctv;

/// <summary>
/// 분석 상태. Off: 분석을 켜지 않음(기본) — 스크린샷은 모이지만 읽지 않는다. Idle: 켜져 있고 읽을 이미지가 없어 모델이 내려가 있다.
/// Loading: 읽을 이미지가 생겨 모델을 올리는 중. Working: 모델이 올라가 이미지를 읽는 중.
/// </summary>
public enum AnalysisState { Off, Idle, Loading, Working }

/// <summary>화면에 보여 줄 현재 상태.</summary>
public sealed record CctvStatus(
    AnalysisState State, string? Message, bool IsError, string Folder, int ImageCount, ProcessingCounts Counts, string? Processing,
    string Model, DateTime? LastAnalyzedAt, int ModelCalls, int ReusedCalls);

/// <summary>
/// CCTV 분석: 스크린샷 폴더를 지켜보다가(화면 감시 캡처가 저장한 CCTV 파일) 새 이미지를 등록하고,
/// 분석이 켜져 있으면 비전 모델로 인식 영역을 읽어 이벤트를 판정한다.
/// 비전 모델은 사용자가 분석을 켜 둔 동안 '읽을 이미지가 있을 때만' 올리고, 대기가 모두 끝나면(짧은 유예 뒤) 내린다.
/// Argus 를 켠다고, 분석을 켠다고 모델이 저절로 올라가지 않는다.
/// </summary>
public sealed class CctvService : IDisposable
{
    private const string SettingsKey = "cctv";
    private static readonly Regex CctvName = new(@"^CCTV(\d{14})(\d*)_(.+)\.png$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IModuleContext _ctx;
    private readonly VisionClient _vision = new();
    private readonly RegionRecognizer _recognizer;
    private readonly Analyzer _analyzer;
    private readonly object _lock = new();
    private readonly Dictionary<long, int> _attempts = [];
    private CancellationTokenSource? _cts;          // 분석 루프
    private Task? _loop;
    private CancellationTokenSource? _scanCts;      // 폴더 감시 루프 (서비스 수명 동안)
    private volatile bool _scanNow;
    private string? _processing;
    private DateTime? _lastAnalyzedAt;
    private IDisposable? _regionSub;

    public CctvStore Store { get; }
    public CctvSettings Settings { get; }
    public AnalysisState State { get; private set; } = AnalysisState.Off;
    private bool _modelLoaded;
    public string? Message { get; private set; }
    public bool IsError { get; private set; }
    public string CropRoot { get; }

    /// <summary>화면을 갱신해야 할 때 (다른 스레드에서 올 수 있다).</summary>
    public event Action? Changed;

    public CctvService(IModuleContext ctx)
    {
        _ctx = ctx;
        Settings = ctx.Settings.Load<CctvSettings>(SettingsKey);
        Settings.Normalize();
        var dir = ctx.DataDirectory("argus.cctv");
        Directory.CreateDirectory(dir);
        CropRoot = Path.Combine(dir, "crops");
        Store = new CctvStore(Path.Combine(dir, "cctv.sqlite"));
        Store.ResetStuckProcessing();
        _recognizer = new RegionRecognizer(_vision, () => Settings.Vision, CropRoot);
        _analyzer = new Analyzer(Store);
    }

    /// <summary>서비스 시작: 폴더 감시만 켠다. 비전 모델은 올리지 않는다.</summary>
    public void Start()
    {
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        _ = Task.Run(() => ScanLoopAsync(ct));
        _regionSub = _ctx.Events.Subscribe<RegionChanged>(e => { if (e.SavedPath != null) _scanNow = true; });
    }

    public void SaveSettings() { Settings.Normalize(); _ctx.Settings.Save(SettingsKey, Settings); }

    public VisionClient Vision => _vision;

    /// <summary>분석할 폴더: 설정에서 지정했으면 그것, 아니면 화면 감시 캡처가 저장하는 폴더.</summary>
    public string ImageFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Settings.ImageFolder)) return Settings.ImageFolder;
            var capture = _ctx.Settings.Load<CaptureFolderProbe>("capture").OutputFolder;
            return string.IsNullOrWhiteSpace(capture) ? _ctx.DataDirectory("argus.capture") : capture;
        }
    }
    private sealed class CaptureFolderProbe { public string OutputFolder { get; set; } = ""; }

    // ---------- 폴더 감시 ----------

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        var last = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_scanNow || (DateTime.Now - last).TotalSeconds >= Settings.ScanSeconds)
                {
                    _scanNow = false; last = DateTime.Now;
                    if (ScanFolder()) { Changed?.Invoke(); }
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[CCTV] 폴더 확인 실패: {ex.Message}"); }
            try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>폴더의 CCTV 파일을 등록하고, 사라진 파일은 기록에서 지운다. 바뀐 것이 있으면 true.</summary>
    public bool ScanFolder()
    {
        var folder = ImageFolder;
        if (!Directory.Exists(folder)) return false;
        var known = Store.KnownImagePaths(folder);
        var present = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        foreach (var path in Directory.EnumerateFiles(folder, "CCTV*.png"))
        {
            var name = Path.GetFileName(path);
            var m = CctvName.Match(name);
            if (!m.Success) continue;
            present.Add(path);
            if (known.Contains(path)) continue;
            var info = new FileInfo(path);
            var (key, at) = ParseCapture(m.Groups[1].Value, m.Groups[2].Value);
            if (Store.AddImage(folder, path, name, m.Groups[3].Value, key, at, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds())) changed = true;
        }
        var removed = Store.RemoveMissingImages(folder, present);
        foreach (var id in removed)
        {
            try { var dir = Path.Combine(CropRoot, id.ToString()); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 다음에 다시 */ }
        }
        return changed || removed.Count > 0;
    }

    internal static (string CaptureKey, string CapturedAt) ParseCapture(string baseDigits, string fraction)
    {
        var ms = (fraction + "000")[..3];
        var at = $"{baseDigits[..4]}-{baseDigits[4..6]}-{baseDigits[6..8]}T{baseDigits[8..10]}:{baseDigits[10..12]}:{baseDigits[12..14]}.{ms}";
        return (baseDigits + fraction, at);
    }

    // ---------- 분석 켜기 · 끄기 ----------

    /// <summary>
    /// 분석을 켠다. 여기서는 비전 모델 서버(Ollama)에 연결되는지, 고른 모델이 설치돼 있는지만 확인하고 모델은 올리지 않는다.
    /// 모델은 읽을 이미지가 생겼을 때 올라간다.
    /// </summary>
    public async Task EnableAsync()
    {
        lock (_lock) { if (State != AnalysisState.Off) return; State = AnalysisState.Loading; Message = "비전 모델 서버를 확인하는 중…"; IsError = false; }
        Changed?.Invoke();

        var test = await _vision.TestAsync(Settings.Vision).ConfigureAwait(false);
        if (!test.Ok) { lock (_lock) { State = AnalysisState.Off; Message = test.Message; IsError = true; } Changed?.Invoke(); return; }

        lock (_lock)
        {
            Store.ResetStuckProcessing();
            _recognizer.ResetReuse();
            _attempts.Clear();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            State = AnalysisState.Idle; Message = null; IsError = false;
            _loop = Task.Run(() => ProcessLoopAsync(ct));
        }
        Changed?.Invoke();
    }

    /// <summary>분석을 끈다: 진행 중인 이미지까지 마치고 멈춘 뒤 모델이 올라가 있으면 내린다.</summary>
    public async Task DisableAsync()
    {
        Task? loop; CancellationTokenSource? cts;
        lock (_lock)
        {
            if (State == AnalysisState.Off) return;
            cts = _cts; loop = _loop; _cts = null; _loop = null;
        }
        cts?.Cancel();
        if (loop != null) { try { await loop.ConfigureAwait(false); } catch { /* 취소 */ } }
        cts?.Dispose();
        await UnloadIfLoadedAsync().ConfigureAwait(false);
        lock (_lock) { State = AnalysisState.Off; Message = null; IsError = false; _processing = null; }
        Changed?.Invoke();
    }

    private async Task UnloadIfLoadedAsync()
    {
        if (!_modelLoaded) return;
        _modelLoaded = false;
        await _vision.UnloadModelAsync(Settings.Vision).ConfigureAwait(false);
    }

    private void SetState(AnalysisState state) { lock (_lock) { if (State != AnalysisState.Off) State = state; } Changed?.Invoke(); }

    // ---------- 분석 루프 ----------

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        var idleSince = DateTime.Now;
        while (!ct.IsCancellationRequested)
        {
            ImageRow? image = null;
            try
            {
                image = Store.NextPendingImage();
                if (image == null)
                {
                    // 읽을 이미지가 없다: 잠깐(유예) 기다려도 새 이미지가 없으면 모델을 내린다.
                    if (_modelLoaded && (DateTime.Now - idleSince).TotalSeconds >= Settings.UnloadAfterIdleSeconds)
                    {
                        await UnloadIfLoadedAsync().ConfigureAwait(false);
                        SetState(AnalysisState.Idle);
                    }
                    await Task.Delay(750, ct).ConfigureAwait(false);
                    continue;
                }
                idleSince = DateTime.Now;

                if (!_modelLoaded)
                {
                    SetState(AnalysisState.Loading);
                    lock (_lock) { Message = $"모델 '{Settings.Vision.Model}' 을(를) 올리는 중… (대기 {Store.Counts().Pending}장)"; IsError = false; }
                    Changed?.Invoke();
                    var error = await _vision.LoadModelAsync(Settings.Vision, ct).ConfigureAwait(false);
                    if (error != null) throw new VisionUnavailableException(error);
                    _modelLoaded = true;
                    lock (_lock) Message = null;
                }
                SetState(AnalysisState.Working);
                await ProcessImageAsync(image, ct).ConfigureAwait(false);
                idleSince = DateTime.Now;
            }
            catch (OperationCanceledException) { if (image != null && Store.ImageStatus(image.Id) == "processing") Store.MarkPending(image.Id); break; }
            catch (VisionUnavailableException ex)
            {
                // 비전 모델이 응답하지 않는다: 이미지는 대기로 돌리고 잠시 뒤 다시 시도한다 (실패로 기록하지 않는다).
                if (image != null && Store.ImageStatus(image.Id) == "processing") Store.MarkPending(image.Id);
                _modelLoaded = false;
                SetState(AnalysisState.Idle);
                SetProblem($"비전 모델 응답 없음 — 10초 뒤 다시 시도합니다. ({ex.Message})");
                try { await Task.Delay(10_000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            catch (RecognitionFailedException ex) when (image != null)
            {
                var n = _attempts[image.Id] = _attempts.GetValueOrDefault(image.Id) + 1;
                if (n >= 3) { Store.FailImage(image.Id, ex.Message); SetProblem($"인식 실패: {image.Filename} · {ex.Message}"); }
                else Store.MarkPending(image.Id);
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                if (image != null) { Store.FailImage(image.Id, ex.Message); SetProblem($"분석 실패: {image.Filename} · {ex.Message}"); }
                else { SetProblem("분석 오류: " + ex.Message); try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; } }
            }
            finally { lock (_lock) _processing = null; Changed?.Invoke(); }
        }
    }

    private void SetProblem(string message) { lock (_lock) { Message = message; IsError = true; } }

    private async Task ProcessImageAsync(ImageRow image, CancellationToken ct)
    {
        var regions = Store.WatcherRegions(image.Character);
        if (regions.Count == 0) return;
        Store.MarkProcessing(image.Id);
        lock (_lock) { _processing = image.Filename; if (IsError) { Message = null; IsError = false; } }
        Changed?.Invoke();

        var full = await Task.Run(() => ImageOps.Load(image.FilePath), ct).ConfigureAwait(false);
        var observations = new List<Observation>();
        for (int i = 0; i < regions.Count; i++) observations.Add(await _recognizer.RecognizeAsync(image, full, regions[i], i, ct).ConfigureAwait(false));

        // 읽는 동안 감시 설정이 바뀌어 이 이미지가 다시 대기로 돌아갔다면 이번 결과는 버린다.
        if (Store.ImageStatus(image.Id) != "processing") return;
        var active = observations.Where(o => Store.WatcherEnabled(o.WatcherId)).ToList();
        var before = Store.MaxEventId();
        Store.CompleteImage(image.Id, active);
        _analyzer.AnalyzeImage(image, active);
        _lastAnalyzedAt = DateTime.Now;
        _attempts.Remove(image.Id);
        PublishNewEvents(before);
    }

    private void PublishNewEvents(long afterId)
    {
        var fresh = Store.EventsAfter(afterId);
        if (fresh.Count == 0) return;
        _ctx.Events.Publish(new CctvEventsDetected([.. fresh.Select(e => new CctvDetection(e.Id, e.Type, e.Time, e.Character, e.Corporation, e.Ship, e.WatcherLabel ?? ""))]));
    }

    // ---------- 눈깔 등록에 쓰는 정보 ----------

    /// <summary>등록할 수 있는 캐릭터: 스크린샷이 폴더에 있는 캐릭터만 (이름, 이미지 수, 가장 최근 촬영 시각). 스크린샷이 없으면 영역을 지정할 수 없으므로 목록에 넣지 않는다.</summary>
    public List<(string Name, int Images, string Latest)> KnownCharacters() =>
        [.. Store.CharacterStats(ImageFolder).Where(s => s.ImageCount > 0).Select(s => (s.Name, s.ImageCount, s.LatestCaptureAt))];

    /// <summary>이 캐릭터의 가장 최근 스크린샷 id (영역 지정에 쓴다). 없으면 null.</summary>
    public long? LatestImageId(string character) =>
        Store.CharacterStats(ImageFolder).FirstOrDefault(s => s.Name == character) is { LatestImageId: > 0 } st ? st.LatestImageId : null;

    // ---------- 감시 눈깔 관리 ----------

    public void SaveWatcher(Watcher watcher)
    {
        Store.SaveWatcher(watcher);
        _recognizer.ResetReuse();
        Changed?.Invoke();
    }

    public void DeleteWatcher(string id)
    {
        Store.DeleteWatcher(id);
        _recognizer.ResetReuse();
        Changed?.Invoke();
    }

    /// <summary>분석 기록(이벤트·현재 상태·이미지 목록)을 모두 지우고 폴더를 처음부터 다시 등록한다. 분석이 켜져 있어도 안전하게 멈췄다 이어간다.</summary>
    public void ResetAll()
    {
        Store.ResetDerivedData();
        _recognizer.ResetReuse();
        try { if (Directory.Exists(CropRoot)) Directory.Delete(CropRoot, true); } catch { /* 나중에 다시 */ }
        _scanNow = true;
        Changed?.Invoke();
    }

    // ---------- 상태 ----------

    public CctvStatus Status()
    {
        var folder = ImageFolder;
        return new CctvStatus(State, Message, IsError, folder, Store.ImageCount(folder), Store.Counts(), _processing, Settings.Vision.Model, _lastAnalyzedAt, _recognizer.ModelCalls, _recognizer.ReusedCalls);
    }

    public void Dispose()
    {
        _regionSub?.Dispose();
        _scanCts?.Cancel();
        // 분석 중이면 멈추고 모델을 내린다 (Argus 를 닫을 때 GPU 를 붙들고 있지 않게).
        try { DisableAsync().Wait(TimeSpan.FromSeconds(20)); } catch { /* 종료 중 */ }
        _vision.Dispose();
        Store.Dispose();
    }
}
