using System.Text.Json.Nodes;

namespace Argus.Modules.Cctv;

/// <summary>타임라인 분류 (필터 버튼).</summary>
public enum EventCategory { Warp, DockUndock, Jump, Covop, Signature, Other }

/// <summary>사건 종류별 화면 표시 (이름, 분류, 색, 설명). 판정 규칙 설명은 판정 근거 창에 쓴다.</summary>
public static class EventPresentation
{
    public static string Label(string type) => type switch
    {
        "warp_in" => "워프인", "warp_out" => "워프아웃", "docked" => "도킹", "undocked" => "언독", "jump_in" => "점프인", "jump_out" => "점프아웃",
        "appeared" => "오버뷰 인", "disappeared" => "오버뷰 아웃", "covop_in" => "코옵인", "covop_out" => "코옵아웃",
        "signature_created" or "signature_destroyed" => "시그니처",
        "watch_paused" => "일시중지", "watch_resumed" => "재시작", "region_changed" => "영역 변경", _ => "기타",
    };

    public static EventCategory Category(string type) => type switch
    {
        "warp_in" or "warp_out" => EventCategory.Warp,
        "docked" or "undocked" => EventCategory.DockUndock,
        "jump_in" or "jump_out" => EventCategory.Jump,
        "covop_in" or "covop_out" => EventCategory.Covop,
        "signature_created" or "signature_destroyed" => EventCategory.Signature,
        _ => EventCategory.Other,
    };

    public static string CategoryLabel(EventCategory c) => c switch
    {
        EventCategory.Warp => "워프", EventCategory.DockUndock => "도킹/언독", EventCategory.Jump => "점프", EventCategory.Covop => "코옵", EventCategory.Signature => "시그니처", _ => "기타",
    };

    /// <summary>색 (#RRGGBB): 들어옴/나감/도킹/점프/코옵/시그니처를 구분한다.</summary>
    public static string Color(string type) => type switch
    {
        "warp_in" => "#6BA0FF", "warp_out" => "#F5C15A", "docked" => "#6FD6A0", "undocked" => "#5CC8C0", "jump_in" or "jump_out" => "#B79CFF",
        "covop_in" or "covop_out" => "#9AA3B5", "signature_created" => "#6FD6A0", "signature_destroyed" => "#FF8A8F", "watch_paused" => "#F5C15A", "watch_resumed" => "#6FD6A0", "region_changed" => "#8EC5FF", "appeared" => "#8EC5FF", "disappeared" => "#C9A5A5", _ => "#8B93A5",
    };

    /// <summary>"확정" / "추정" (워프·도킹·언독 판정만).</summary>
    public static string? Verification(EventRow e)
    {
        if (e.Type is not ("warp_in" or "warp_out" or "docked" or "undocked")) return null;
        var v = e.Details["verification"]?.GetValue<string>();
        return v == "confirmed" ? "확정" : v == "estimated" ? "추정" : null;
    }

    /// <summary>한 줄 설명: 함선 · 도킹 수 변화 · 속도.</summary>
    public static string Detail(EventRow e)
    {
        if (CctvStore.IsMarker(e.Type)) return MarkerDetail(e);
        if (e.Type is "signature_created" or "signature_destroyed")
            return $"{(e.Type == "signature_created" ? "생성" : "소멸")} · {(e.Details["name"]?.ToString() is { Length: > 0 } n ? n : "미확인 시그니처")}";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(e.Ship)) parts.Add(e.Ship!);
        if (e.Type is "docked" or "undocked" && AsNumber(e.Details["dockCountBefore"]) is { } bd && AsNumber(e.Details["dockCountAfter"]) is { } ad)
            parts.Add($"도킹 수 {bd:0} → {ad:0}");
        if (e.Speed is { } sp && !double.IsNaN(sp)) parts.Add($"{sp:N0} m/s");
        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    /// <summary>타임라인 표식(일시중지 · 재시작 · 영역 변경) 의 제목.</summary>
    public static string MarkerTitle(EventRow e) => e.Type switch { "watch_paused" => "감시 일시중지", "watch_resumed" => "감시 재시작", _ => "인식 영역 변경" };

    /// <summary>표식 제목 뒤에 붙이는, 실제로 일시중지/재시작을 누른 시각 (타임라인의 시각은 CCTV 이미지 기준이라 따로 보여 준다). 없으면 null.</summary>
    public static string? MarkerSuffix(EventRow e)
    {
        var (key, word) = e.Type switch { "watch_paused" => ("pausedAt", "일시중지"), "watch_resumed" => ("resumedAt", "재시작"), _ => (null, null) };
        if (key == null || e.Details[key]?.ToString() is not { Length: >= 19 } iso) return null;
        // 이벤트 시각과 날짜가 다르면 날짜도 보여 준다.
        var sameDay = e.Time.Length >= 10 && iso.StartsWith(e.Time[..10]);
        return $"{(sameDay ? "" : iso.Substring(5, 5) + " ")}{iso.Substring(11, 8)} 에 {word}";
    }

    private static string MarkerDetail(EventRow e)
    {
        if (e.Type == "watch_paused") return "분석을 멈췄습니다. 자리를 잡은 뒤 재시작 지점을 고르세요.";
        if (e.Type == "region_changed") return "이 지점부터 새 인식 영역으로 분석합니다.";
        var image = e.Details["startImage"]?.ToString();
        var from = string.IsNullOrEmpty(image) ? "지금 이후 촬영분부터" : $"{image} 부터";
        return e.Details["rollback"]?.GetValue<bool>() == true ? $"{from} · 이미 분석한 구간부터 다시 시작 (그 앞의 결과는 유지)" : $"{from} 이어서 분석";
    }

    /// <summary>JSON 숫자를 double 로 (정수로 만든 값이든 읽어 온 값이든).</summary>
    internal static double? AsNumber(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    /// <summary>판정 근거 창에 보여 줄 '판정 규칙' 설명.</summary>
    public static string Rule(EventRow e)
    {
        var estimated = e.Details["verification"]?.GetValue<string>() == "estimated";
        return e.Type switch
        {
            "warp_in" => estimated ? "첫 관측 속도가 고속이지만 이후 감속은 아직 확인되지 않아 추정합니다." : "첫 관측 속도가 높고 다음 유효 관측에서 감속을 확인했습니다.",
            "warp_out" => estimated ? "오버뷰 이탈 전 마지막 속도가 높지만 가속 변화는 확인되지 않아 추정합니다." : "오버뷰 이탈 전 마지막 속도가 높고, 이전 관측보다 상승했거나 처음 저속이었다가 올라갔습니다.",
            "docked" => "오버뷰 이탈과 도킹 수 증가가 6초 안에 일치했습니다.",
            "undocked" => estimated ? "오버뷰에 저속으로 등장했지만 도킹 수 감소는 아직 확인되지 않아 언독 추정입니다." : "오버뷰 진입과 도킹 수 감소가 6초 안에 일치했습니다.",
            "jump_in" => "게이트 감시에서 저속으로 처음 관측됐습니다.",
            "jump_out" => "게이트 감시에서 오버뷰 행이 사라졌습니다 (마지막 속도는 워프 속도 미만).",
            "covop_in" or "covop_out" => "코버트 클로킹이 가능한 함선이라 워프·점프·도킹을 화면만으로 구분하기 어려워 코옵인/코옵아웃으로 묶어 기록했습니다.",
            "appeared" => "오버뷰에 나타났지만 첫 속도를 읽지 못했거나 도킹 수 근거가 없어 '나타남'으로만 기록했습니다.",
            "disappeared" => "오버뷰에서 사라졌지만 워프 속도나 도킹 수 근거가 없어 '사라짐'으로만 기록했습니다.",
            "signature_created" or "signature_destroyed" => "프로빙 창의 시그니처 ID 목록을 전후 스캔과 비교했습니다 (소멸은 두 번 연속 누락될 때만 인정).",
            _ => "전후 프레임의 지정 영역을 비교해 변화를 감지했습니다.",
        };
    }

    // ---------- 프로빙 시그니처 ----------

    /// <summary>"코즈믹" 으로 시작하면 아직 스캔하지 않은 시그니처 (비전 모델이 나머지 글자를 흐릿하게 읽어도 이것만으로 충분하다).</summary>
    public static (string Name, string Group, bool Unscanned) SignatureFields(string? rawName, string? rawGroup)
    {
        var name = string.IsNullOrEmpty(rawName) ? "미확인" : rawName!;
        var unscanned = name.TrimStart().StartsWith("코즈믹");
        return (name, unscanned ? "" : string.IsNullOrEmpty(rawGroup) ? "미확인" : rawGroup!, unscanned);
    }
}
