using Argus.Core.Dashboard;
using Argus.Core.Modules;

namespace Argus.Modules.CombatLog;

/// <summary>EVE 전투 로그를 읽어 캐릭터별 수치(받는 DPS·LOGI·뉴트, 태클, 레드박싱)를 계산해 이벤트로 알린다. 화면은 프리뷰 HUD 등 구독하는 쪽이 그린다.</summary>
public sealed class CombatLogModule : IArgusModule, IDashboardContributor
{
    private CombatLogService? _service;

    public string Id => "argus.combatlog";
    public string DisplayName => "전투 로그";

    // 지금은 프리뷰 HUD 에만 쓰이므로 설정 페이지에서 프리뷰 그룹 아래에 보여준다.
    public string? SettingsParentId => "argus.preview";

    public Task StartAsync(IModuleContext context, CancellationToken ct)
    {
        _service = new CombatLogService(context);
        _service.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _service?.Dispose();
        return Task.CompletedTask;
    }

    // ---------- 대시보드 ----------

    private static string Fmt(double v) => v < 0 ? "+" + Fmt(-v) : Math.Round(v).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>받는 뉴트: 캡이 빠지고 있으면 -N, 내가 빤 양이 더 많아 늘어나면 +N (프리뷰 HUD 와 같은 표기).</summary>
    private static string FmtNeut(double v) { var r = Math.Round(v); return r > 0 ? "-" + Fmt(r) : r < 0 ? "+" + Fmt(-r) : "0"; }

    private const string DimColor = "#8B93A5";

    public IReadOnlyList<DashboardChip> ClientChips(string character)
    {
        var s = _service?.Latest(character);
        if (s == null) return [];

        // 숫자는 각자의 열에 글자만 (0 이면 흐리게), 경고는 '상태' 열에 알약으로
        DashboardChip Num(string column, string tip, double v, string text, string color) =>
            new(text, ChipTone.Neutral, tip, Math.Round(v) == 0 ? DimColor : color, column, Plain: true);
        var chips = new List<DashboardChip>
        {
            Num(DashboardColumns.Dps, "받는 DPS", s.DpsIn, Fmt(s.DpsIn), "#FFB454"),
            Num(DashboardColumns.Logi, "받는 LOGI", s.LogiIn, Fmt(s.LogiIn), "#9BE7C4"),
            Num(DashboardColumns.Neut, "받는 뉴트 (노스·캡 전송 반영). 캡이 빠지면 빨강 -, 늘어나면 파랑 +", s.NeutIn, FmtNeut(s.NeutIn), s.NeutIn > 0 ? "#FF5C5C" : "#5CB8FF"),
        };
        if (s.SurgeAt != long.MinValue && Environment.TickCount64 - s.SurgeAt <= 10_000) chips.Add(new("레드박싱", ChipTone.Bad, "받는 피해가 갑자기 크게 늘었습니다"));
        if (s.Hic) chips.Add(new("HIC", ChipTone.Bad, "HIC 에게 포인팅당함"));
        if (s.Scram) chips.Add(new("SCRAM", ChipTone.Accent, "워프 스크램블러에 걸림"));
        if (s.Disrupt) chips.Add(new("DISRUPT", ChipTone.Good, "워프 디스럽터에 걸림"));
        return chips;
    }

    public IReadOnlyList<DashboardChip> SummaryChips()
    {
        var n = _service?.TrackedCount ?? 0;
        return [n > 0 ? new($"전투 로그 {n}개 읽는 중", ChipTone.Good, "실행 중인 클라이언트의 전투 로그를 읽고 있습니다") : new("전투 로그 대기 중", ChipTone.Neutral, "읽을 로그가 아직 없습니다")];
    }

    public object? CreateSettingsView() => _service is null ? null : new CombatLogSettingsView(_service);
}
