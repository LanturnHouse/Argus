using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Argus.Core.Clients;
using Argus.Core.Events;
using Argus.Modules.Capture.Native;

namespace Argus.Modules.Capture;

/// <summary>클라이언트 하나를 주기적으로 캡처하며 영역 변화를 감시한다.</summary>
internal sealed class ClientMonitor(
    string character,
    Func<WgcSession?> getSession,
    ClientCaptureConfig config,
    Func<string> getOutputFolder,
    IEventBus bus)
{
    private readonly ChangeDetector _detector = new();
    private CancellationTokenSource? _cts;
    private (int, int, int, int) _lastRoi;

    public bool IsRunning => _cts != null;
    public string Status { get; private set; } = "대기";

    public void Start()
    {
        if (_cts != null) return;
        if (!config.HasRoi) { Status = "감시 영역을 먼저 지정하세요"; return; }
        _cts = new CancellationTokenSource();
        _detector.Reset();
        var ct = _cts.Token;
        _ = Task.Run(() => LoopAsync(ct));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        Status = "중지됨";
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var lastAlert = DateTime.MinValue;
        var pendingPixels = 0; // 쿨다운 때문에 알리지 못한 변화 중 가장 많이 변한 픽셀 수 (0 이면 없음)
        var startShot = false; // 감시를 시작하고 처음 읽은 화면을 한 장 저장했는가

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var session = getSession();
                if (session == null) Status = "캡처 세션 없음";
                else if (session.IsMinimized) Status = "창이 최소화됨 (캡처 불가)";
                else if (session.Grab() is { } frame)
                {
                    if (session.Faulted) Status = "프레임 읽기 실패 (세션 재생성 중)";
                    else
                    {
                        if (!startShot) { startShot = true; SaveStartShot(frame); }
                        Tick(frame, ref lastAlert, ref pendingPixels);
                    }
                }
                else Status = "프레임 대기 중";
            }
            catch (Exception ex) { Status = "오류: " + ex.Message; }

            try { await Task.Delay(Math.Max(100, config.IntervalMs), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick(Frame frame, ref DateTime lastAlert, ref int pendingPixels)
    {
        var (rx, ry, rw, rh) = config.ResolveRoi(frame.Width, frame.Height);
        if (_lastRoi != (rx, ry, rw, rh)) { _detector.Reset(); _lastRoi = (rx, ry, rw, rh); } // 영역이 바뀌면 기준 프레임 재설정
        var region = _detector.Compare(frame, rx, ry, rw, rh, config.PixelThreshold, out var changed);

        var isChange = changed >= Math.Max(1, config.MinChangedPixels);
        var now = DateTime.Now;
        var cooled = (now - lastAlert).TotalSeconds >= config.AlertIntervalSec;

        if (isChange) pendingPixels = Math.Max(pendingPixels, changed);
        if (pendingPixels == 0) { Status = "감시 중"; return; }
        if (!cooled) { Status = "변화 감지 (쿨다운 대기)"; return; }

        var reported = pendingPixels;
        pendingPixels = 0;
        lastAlert = now;
        string? path = null, saveError = null;
        if (config.SaveMode != SaveMode.None)
        {
            try
            {
                path = config.SaveMode == SaveMode.FullClient
                    ? SavePng(frame.Bgra, frame.Width, frame.Height, now)
                    : SavePng(region, rw, rh, now);
            }
            catch (Exception ex)
            {
                saveError = ex.Message;
                System.Diagnostics.Trace.WriteLine($"[CCTV] 변화 스크린샷 저장 실패: {ex.Message}");
            }
        }
        Status = saveError is null
            ? $"변화 감지: {now:HH:mm:ss} ({reported}px)"
            : $"변화 감지: {now:HH:mm:ss} ({reported}px) · 저장 실패: {saveError}";
        bus.Publish(new RegionChanged(character, now, reported, path, config.Beep));
    }

    /// <summary>
    /// 감시를 시작하면 처음 읽은 화면을 한 장 저장한다 (변화가 없어도). 분석에서 눈깔을 추가할 때 이 스크린샷 위에 바로 영역을 지정할 수 있다.
    /// 저장 범위는 설정을 따르되, '저장 안 함'이면 클라이언트 전체 화면을 저장한다. 알림은 울리지 않고 감지 기록에도 넣지 않는다.
    /// </summary>
    private void SaveStartShot(Frame frame)
    {
        try
        {
            var now = DateTime.Now;
            if (config.SaveMode == SaveMode.SelectedArea)
            {
                var (rx, ry, rw, rh) = config.ResolveRoi(frame.Width, frame.Height);
                SavePng(ChangeDetector.Crop(frame, rx, ry, rw, rh), rw, rh, now);
            }
            else SavePng(frame.Bgra, frame.Width, frame.Height, now);
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[CCTV] 시작 스크린샷 저장 실패: {ex.Message}"); }
    }

    /// <summary>분석(Cctv 모듈)이 읽는 파일명 규칙: CCTV{yyyyMMddHHmmss}{fff}_{캐릭터}.png</summary>
    private string SavePng(byte[] bgra, int w, int h, DateTime at)
    {
        var folder = getOutputFolder();
        Directory.CreateDirectory(folder);
        var name = $"CCTV{at:yyyyMMddHHmmssfff}_{Sanitize(character)}.png";
        var path = Path.Combine(folder, name);

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgra, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        var tmp = path + ".tmp"; // 분석이 덜 써진 파일을 읽지 않도록 완성 후 이름 변경
        using (var fs = File.Create(tmp)) encoder.Save(fs);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
