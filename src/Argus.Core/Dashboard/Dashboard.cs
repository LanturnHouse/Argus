namespace Argus.Core.Dashboard;

public enum ChipTone { Neutral, Accent, Good, Warn, Bad }

/// <summary>대시보드 클라이언트 표의 열 이름. 모듈이 칩을 어느 열에 넣을지 정할 때 쓴다. 모르는 열 이름은 '상태' 열로 간다.</summary>
public static class DashboardColumns
{
    public const string Cycle = "cycle";     // 사이클 순번 · 단축키
    public const string Dps = "dps";         // 받는 DPS
    public const string Logi = "logi";       // 받는 LOGI
    public const string Neut = "neut";       // 받는 뉴트
    public const string Status = "status";   // 경고·상태 (레드박싱 등)
    public const string Watch = "watch";     // CCTV
}

/// <summary>대시보드에 붙는 작은 표시 하나. Column 은 <see cref="DashboardColumns"/> 중 하나, Color 는 글자색(#RRGGBB)을 직접 정하고 싶을 때만 쓴다.</summary>
/// <param name="Plain">true 면 알약 배경 없이 글자만 그린다 (숫자 열용).</param>
public sealed record DashboardChip(string Text, ChipTone Tone = ChipTone.Neutral, string? Tooltip = null, string? Color = null, string Column = DashboardColumns.Status, bool Plain = false);

/// <summary>
/// 모듈이 대시보드에 정보를 내놓는 선택 인터페이스. 대시보드는 다른 모듈을 직접 알지 못하고, 이 인터페이스를 구현한 모듈들에서만 정보를 모은다.
/// 모듈을 빼면 그 모듈의 표시만 사라진다. 모든 메서드는 UI 스레드에서 호출되며 가벼워야 한다.
/// </summary>
public interface IDashboardContributor
{
    /// <summary>이 캐릭터의 표 한 줄에 넣을 표시들 (각 표시의 Column 이 들어갈 열).</summary>
    IReadOnlyList<DashboardChip> ClientChips(string character) => [];

    /// <summary>상단 요약 띠에 붙일 표시들.</summary>
    IReadOnlyList<DashboardChip> SummaryChips() => [];
}
