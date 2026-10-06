namespace Argus.Core.Events;

/// <summary>전투 모드가 켜지거나(true) 꺼졌다(false, 비전투). 전투 로그·프리뷰 수치가 이를 따른다.</summary>
public sealed record CombatModeChanged(bool Active);
