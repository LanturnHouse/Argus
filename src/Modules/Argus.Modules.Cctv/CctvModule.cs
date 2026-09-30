using Argus.Core.Dashboard;
using Argus.Core.Modules;

namespace Argus.Modules.Cctv;

/// <summary>
/// EVE CCTV 웹앱(Node 서비스 + 웹 화면)을 Argus 안에서 실행하고 'CCTV' 탭에 보여준다.
/// 웹앱은 화면 감시 캡처가 저장하는 CCTV 스크린샷 폴더를 읽어 분석한다 — 두 모듈은 그 폴더(파일)로만 이어져 있고 서로를 직접 참조하지 않는다.
/// </summary>
public sealed class CctvModule : IArgusModule, IDashboardContributor
{
    private CctvService? _service;
    private CctvView? _view;

    public string Id => "argus.cctv";
    public string DisplayName => "CCTV";
    public string Icon => "";

    public Task StartAsync(IModuleContext context, CancellationToken ct)
    {
        _service = new CctvService(context);
        if (_service.Settings.AutoStart) _service.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _service?.Dispose();   // 웹앱 프로세스도 함께 종료
        return Task.CompletedTask;
    }

    public object? CreateView() => _service is { } s ? _view ??= new CctvView(s) : null;
    public object? CreateSettingsView() => _service is { } s ? new CctvSettingsView(s) : null;

    // ---------- 대시보드 ----------

    public IReadOnlyList<DashboardChip> SummaryChips()
    {
        if (_service is not { } s) return [];
        var chips = new List<DashboardChip>
        {
            s.State switch
            {
                CctvState.Running => new("CCTV 실행 중", ChipTone.Good, "EVE CCTV 웹앱이 실행 중입니다"),
                CctvState.Starting => new("CCTV 시작 중", ChipTone.Warn, s.Message),
                CctvState.Failed => new("CCTV 오류", ChipTone.Bad, s.Message),
                _ => new("CCTV 꺼짐", ChipTone.Neutral, "CCTV 탭에서 시작할 수 있습니다"),
            },
        };
        if (s.State == CctvState.Running && s.Status is { } st && st.Pending + st.Processing > 0)
            chips.Add(new($"CCTV 분석 대기 {st.Pending + st.Processing:N0}장", ChipTone.Accent, "아직 분석하지 못한 스크린샷 수"));
        return chips;
    }
}
