namespace Argus.Core.Events;

/// <summary>화면의 상태이상 아이콘에서 읽은, 한 캐릭터의 태클 상태.</summary>
/// <param name="Disrupt">워프 디스럽터 아이콘이 떠 있음</param>
/// <param name="Scram">워프 스크램블러 아이콘이 떠 있음</param>
/// <param name="Hic">HIC 포인팅 아이콘이 떠 있음</param>
public sealed record TackleIconState(string Character, bool Disrupt, bool Scram, bool Hic);

/// <summary>상태이상 인식 모듈이 주기적으로 발행한다. 화면을 실제로 읽은 클라이언트만 들어 있다 (읽지 못한 클라이언트는 빠지므로 받는 쪽은 로그 등 다른 정보를 그대로 쓰면 된다).</summary>
public sealed record TackleIconsUpdated(IReadOnlyList<TackleIconState> States);
