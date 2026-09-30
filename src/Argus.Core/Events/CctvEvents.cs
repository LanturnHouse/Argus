namespace Argus.Core.Events;

/// <summary>CCTV 분석이 판정한 사건 하나 (함선 진입·이탈, 도킹·언독, 시그니처 변화 등).</summary>
/// <param name="Type">웹앱과 같은 이름: warp_in, warp_out, jump_in, jump_out, docked, undocked, appeared, disappeared, covop_in, covop_out, signature_created, signature_destroyed</param>
public sealed record CctvDetection(long Id, string Type, string Time, string? Character, string? Corporation, string? Ship, string Watcher);

/// <summary>CCTV 분석 모듈이 새 사건을 판정했을 때 발행한다. 알림·프리뷰 HUD 등이 구독해 쓸 수 있다 (모듈끼리는 이벤트로만 연결).</summary>
public sealed record CctvEventsDetected(IReadOnlyList<CctvDetection> Detections);
