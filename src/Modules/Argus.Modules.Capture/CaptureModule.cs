using Argus.Core.Dashboard;
using Argus.Core.Modules;

namespace Argus.Modules.Capture;

public sealed class CaptureModule : IArgusModule, IDashboardContributor
{
    private CaptureService? _service;

    public string Id => "argus.capture";
    public string Icon => "";
    public string DisplayName => "CCTV";

    public Task StartAsync(IModuleContext context, CancellationToken ct)
    {
        _service = new CaptureService(context);
        _service.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _service?.Dispose();
        return Task.CompletedTask;
    }

    public object? CreateView() => _service is null ? null : new CaptureView(_service);

    // ---------- 대시보드 ----------

    private static string Ago(DateTime at)
    {
        var s = (DateTime.Now - at).TotalSeconds;
        return s < 60 ? $"{Math.Max(0, (int)s)}초 전" : s < 3600 ? $"{(int)(s / 60)}분 전" : s < 86400 ? $"{(int)(s / 3600)}시간 전" : at.ToString("M월 d일");
    }

    public IReadOnlyList<DashboardChip> ClientChips(string character)
    {
        if (_service?.GetConfig(character) is not { } cfg) return [];
        var chips = new List<DashboardChip>
        {
            !cfg.HasRoi ? new("감시 영역 미지정", ChipTone.Warn, "CCTV에서 감시할 영역을 지정하세요", Column: DashboardColumns.Watch)
            : _service.IsRunning(character) ? new("감시 중", ChipTone.Good, _service.GetStatus(character), Column: DashboardColumns.Watch)
            : new("감시 정지", ChipTone.Neutral, Column: DashboardColumns.Watch),
        };
        if (_service.GetLog(character).FirstOrDefault() is { } last) chips.Add(new($"마지막 감지 {Ago(last.At)}", ChipTone.Neutral, last.At.ToString("yyyy-MM-dd HH:mm:ss"), Column: DashboardColumns.Watch));
        if (!string.IsNullOrWhiteSpace(cfg.Note)) chips.Add(new($"메모: {cfg.Note.Trim()}", ChipTone.Accent, "감시 대상 메모", Column: DashboardColumns.Watch));
        return chips;
    }

    public IReadOnlyList<DashboardChip> SummaryChips()
    {
        if (_service is null) return [];
        var names = _service.Characters;
        var watching = names.Count(_service.IsRunning);
        var today = names.Sum(n => _service.GetLog(n).Count(r => r.At.Date == DateTime.Today));
        return
        [
            watching > 0 ? new($"감시 중 {watching}개", ChipTone.Good) : new("감시 중 0개", ChipTone.Neutral),
            today > 0 ? new($"오늘 감지 {today}회", ChipTone.Accent, "이번 실행 중 오늘 감지된 횟수") : new("오늘 감지 0회", ChipTone.Neutral, "이번 실행 중 오늘 감지된 횟수"),
        ];
    }

    public object? CreateSettingsView() => _service is null ? null : new CaptureSettingsView(_service);
}
