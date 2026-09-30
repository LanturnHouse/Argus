namespace Argus.Core.Events;

/// <summary>한 캐릭터의 최근 전투 상태. 수치는 초당 값(최근 N초 평균), 태클은 '지금 걸려 있다고 보는가'.</summary>
/// <param name="DpsIn">내가 받는 피해 (초당)</param>
/// <param name="LogiIn">내가 받는 원격 수리·실드 충전 (초당 HP)</param>
/// <param name="NeutIn">내가 받는 에너지 뉴트럴라이즈 (초당 GJ)</param>
/// <param name="Disrupt">워프 디스럽터에 걸림</param>
/// <param name="Scram">워프 스크램블러에 걸림</param>
/// <param name="Hic">HIC 함선의 포인팅(디스럽트/스크램블)에 걸림</param>
/// <param name="SurgeAt">가장 최근 '레드박싱(받는 피해 레드박싱)'이 감지된 시각(<see cref="Environment.TickCount64"/>). 없으면 long.MinValue. 색조·단축키를 얼마나 유지할지는 받는 쪽이 정한다.</param>
public sealed record CombatSnapshot(string Character, double DpsIn, double LogiIn, double NeutIn, bool Disrupt, bool Scram, bool Hic, long SurgeAt = long.MinValue);

/// <summary>전투 로그 모듈이 주기적으로 발행한다. 프리뷰 HUD 등이 구독한다 (모듈끼리는 이벤트로만 연결).</summary>
public sealed record CombatStatsUpdated(IReadOnlyList<CombatSnapshot> Snapshots);
