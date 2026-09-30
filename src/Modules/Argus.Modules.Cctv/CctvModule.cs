using Argus.Core.Dashboard;
using Argus.Core.Modules;

namespace Argus.Modules.Cctv;

/// <summary>
/// CCTV 분석: EVE 스크린샷(화면 감시 캡처가 저장한 CCTV 파일)에서 오버뷰 · 프로빙 창 · 도킹 숫자를 비전 모델(Ollama)로 읽어
/// 함선의 출입(워프 · 점프), 도킹 · 언독, 코버트 출입, 시그니처 생성 · 소멸을 판정한다. 기존 EVE CCTV 웹앱을 Argus 안으로 다시 만든 것이다.
/// 화면 감시 캡처 모듈과는 스크린샷 파일(과 저장 폴더 설정)로만 이어져 있고, 서로를 직접 참조하지 않는다.
/// </summary>
public sealed class CctvModule : IArgusModule, IDashboardContributor
{
    private CctvService? _service;
    private CctvView? _view;

    public string Id => "argus.cctv";
    public string DisplayName => "CCTV";
    public string Icon => "";

    public Task StartAsync(IModuleContext context, CancellationToken ct)
    {
        _service = new CctvService(context);
        _service.Start();   // 폴더 감시만 시작한다. 비전 모델은 사용자가 분석을 켜고 읽을 이미지가 생길 때까지 올리지 않는다.
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _service?.Dispose();   // 분석 중이면 멈추고 모델을 내린다
        _service = null;
        return Task.CompletedTask;
    }

    public object? CreateView() => _service is { } s ? _view ??= new CctvView(s) : null;
    public object? CreateSettingsView() => _service is { } s ? new CctvSettingsView(s) : null;

    // ---------- 대시보드 ----------

    public IReadOnlyList<DashboardChip> SummaryChips()
    {
        if (_service is not { } s) return [];
        var st = s.Status();
        var chips = new List<DashboardChip>
        {
            st.State switch
            {
                AnalysisState.Working => new("CCTV 분석 중", ChipTone.Accent, "비전 모델이 스크린샷을 읽고 있습니다"),
                AnalysisState.Loading => new("CCTV 모델 올리는 중", ChipTone.Warn, st.Message),
                AnalysisState.Idle => new("CCTV 켜짐", ChipTone.Good, "읽을 이미지가 생기면 모델을 올립니다"),
                _ => st.IsError ? new("CCTV 오류", ChipTone.Bad, st.Message) : new("CCTV 꺼짐", ChipTone.Neutral, "CCTV 탭에서 분석을 켤 수 있습니다"),
            },
        };
        if (st.Counts.Pending + st.Counts.Processing > 0) chips.Add(new($"CCTV 대기 {st.Counts.Pending + st.Counts.Processing:N0}장", ChipTone.Warn, "아직 읽지 않은 스크린샷"));
        return chips;
    }

    public IReadOnlyList<DashboardChip> ClientChips(string character)
    {
        if (_service is not { } s) return [];
        var watcher = s.Store.ListWatchers().FirstOrDefault(w => string.Equals(w.Character, character, StringComparison.OrdinalIgnoreCase));
        return watcher == null ? [] : [new($"CCTV · {watcher.Label}", ChipTone.Accent, $"{watcher.WatchType.Label()} 감시 눈깔 (영역 {watcher.Regions.Count}개)", Column: DashboardColumns.Watch)];
    }
}
