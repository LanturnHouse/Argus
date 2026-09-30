namespace Argus.Core.Events;

/// <summary>한 캐릭터의 최근 전투 수치. 수치는 초당 값(최근 N초 평균). 태클 상태는 여기 없다 (화면의 상태이상 아이콘에서 읽어 <see cref="TackleIconsUpdated"/> 로 따로 온다).</summary>
/// <param name="DpsIn">내가 받는 피해 (초당)</param>
/// <param name="LogiIn">내가 받는 원격 수리·실드 충전 (초당 HP)</param>
/// <param name="NeutIn">내가 받는 에너지 뉴트럴라이즈 (초당 GJ)</param>
/// <param name="SurgeAt">가장 최근 레드박싱(받는 피해 급증)이 감지된 시각(<see cref="Environment.TickCount64"/>). 없으면 long.MinValue. 색조·단축키를 얼마나 유지할지는 받는 쪽이 정한다.</param>
public sealed record CombatSnapshot(string Character, double DpsIn, double LogiIn, double NeutIn, long SurgeAt = long.MinValue);

/// <summary>전투 로그 모듈이 주기적으로 발행한다. 프리뷰 HUD 등이 구독한다 (모듈끼리는 이벤트로만 연결).</summary>
public sealed record CombatStatsUpdated(IReadOnlyList<CombatSnapshot> Snapshots);
