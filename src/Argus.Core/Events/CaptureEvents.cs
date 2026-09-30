namespace Argus.Core.Events;

/// <summary>감시 영역에서 변화가 감지되어 알림 조건을 통과했을 때 발행된다. Alerts 등 다른 모듈이 구독한다.</summary>
public sealed record RegionChanged(string Character, DateTime At, int ChangedPixels, string? SavedPath, bool Beep);
