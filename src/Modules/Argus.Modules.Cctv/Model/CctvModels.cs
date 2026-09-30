using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace Argus.Modules.Cctv;

/// <summary>감시 위치의 종류. 스트럭쳐는 도킹 숫자와 오버뷰를 함께 보고 도킹/언독을, 웜홀·게이트는 점프인/아웃을 판정한다.</summary>
public enum WatchType { Structure, Gate }

/// <summary>인식 영역의 종류: 오버뷰 목록, 프로빙 스캐너 결과, 도킹 인원 숫자.</summary>
public enum RegionKind { Overview, Probe, Dock }

public static class Names
{
    public static string Db(this WatchType t) => t == WatchType.Structure ? "structure" : "gate";
    public static WatchType ToWatchType(string s) => s == "structure" ? WatchType.Structure : WatchType.Gate;
    public static string Db(this RegionKind k) => k switch { RegionKind.Overview => "overview", RegionKind.Probe => "probe", _ => "dock" };
    public static RegionKind ToRegionKind(string s) => s switch { "overview" => RegionKind.Overview, "probe" => RegionKind.Probe, _ => RegionKind.Dock };

    /// <summary>화면에 보이는 한국어 이름.</summary>
    public static string Label(this RegionKind k) => k switch { RegionKind.Overview => "오버뷰", RegionKind.Probe => "프로빙 창", _ => "도킹 숫자" };
    public static string Label(this WatchType t) => t == WatchType.Structure ? "스트럭쳐" : "웜홀 / 게이트";
}

/// <summary>스크린샷 안의 인식 영역. 좌표는 이미지 크기에 대한 퍼센트(0~100).</summary>
public sealed record RegionDef(RegionKind Kind, double X, double Y, double W, double H);

/// <summary>감시 눈깔 하나: 캐릭터 + 감시 타입 + 인식 영역들.</summary>
public sealed record Watcher(string Id, string Label, string Character, WatchType WatchType, bool Enabled, int RegionVersion, IReadOnlyList<RegionDef> Regions);

/// <summary>스크린샷 파일 하나 (파일명 CCTV{yyyyMMddHHmmss}{fff}_{캐릭터}.png 에서 읽은 정보).</summary>
public sealed record ImageRow(long Id, string FilePath, string Filename, string Character, string CaptureKey, string CapturedAt);

// ---------- 인식 결과 (웹앱의 observation payload 와 같은 JSON 모양) ----------

public sealed class OverviewRow
{
    public string Distance { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ship { get; set; } = "";
    public string Corporation { get; set; } = "";
    public double? Speed { get; set; }
    public string Raw { get; set; } = "";
    public double Confidence { get; set; }
    /// <summary>그 행의 평균 밝기(0~255). 함선이 사라지기 직전 행이 어두워지는 것을 알아채는 데 쓴다.</summary>
    public double? Brightness { get; set; }
}

public sealed class SignatureRow
{
    public string Id { get; set; } = "";
    public string Distance { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Raw { get; set; } = "";
    public double Confidence { get; set; }
}

public sealed class RegionFields
{
    public List<string> Lines { get; set; } = [];
    public List<OverviewRow>? OverviewRows { get; set; }
    public bool? OverviewDetected { get; set; }
    public List<SignatureRow>? Signatures { get; set; }
    public bool? ProbeDetected { get; set; }
    public int? DockCount { get; set; }
}

public sealed class SourceBox
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class RegionPayload
{
    public RegionFields Fields { get; set; } = new();
    public int? RegionIndex { get; set; }
    public SourceBox? SourceBox { get; set; }
    public string? SourceCropPath { get; set; }
    /// <summary>이 영역을 읽은 방법: "vision"(모델이 읽음) 또는 "reused"(같은 그림이라 앞의 결과를 재사용).</summary>
    public string? Method { get; set; }
}

/// <summary>한 이미지의 한 영역을 읽은 결과.</summary>
public sealed class Observation
{
    public string WatcherId { get; set; } = "";
    public RegionKind Kind { get; set; }
    public double? Confidence { get; set; }
    public RegionPayload Payload { get; set; } = new();
}

/// <summary>판정한 사건 (이벤트). Details 는 판정 근거(사유, 확정/추정, 속도, 도킹 수 등).</summary>
public sealed class EventDraft
{
    public string Time { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Character { get; set; }
    public string? Corporation { get; set; }
    public string? Ship { get; set; }
    public double? Speed { get; set; }
    public string WatcherId { get; set; } = "";
    public double? Confidence { get; set; }
    public long ImageId { get; set; }
    public long? PreviousImageId { get; set; }
    public JsonObject Details { get; set; } = new();
}

/// <summary>저장된 이벤트 (화면 표시용).</summary>
public sealed record EventRow(long Id, string Time, string Type, string? Character, string? Corporation, string? Ship, double? Speed, double? Confidence,
    long? ImageId, long? PreviousImageId, JsonObject Details, string? WatcherId, string? WatcherLabel, string? Filename, string? PreviousFilename);

/// <summary>지금 오버뷰에 있는 대상 (추적 중인 상태).</summary>
public sealed record CurrentObject(string WatcherId, string WatcherLabel, string Character, string? Ship, string? Corporation, string? Distance, double? Speed,
    double? Confidence, string LastSeenAt, long ImageId, string? EntryType, string? FirstSeenAt);

/// <summary>지금 프로빙 창에 있는 시그니처.</summary>
public sealed record CurrentSignature(string WatcherId, string WatcherLabel, string Id, string? Name, string? Group, string? Distance, double? Confidence, string FirstSeenAt, string LastSeenAt, long ImageId);

/// <summary>스트럭쳐 감시 위치별 감지된 최고 도킹 수.</summary>
public sealed record DockPeak(string WatcherId, string WatcherLabel, int PeakCount, string CapturedAt, long ImageId, long ObservationId, string Filename);

/// <summary>처리 대기열 요약.</summary>
public sealed record ProcessingCounts(int Pending, int Processing, int Processed, int Failed);

/// <summary>영역 인식이 실패한 것(헤더를 못 찾음 등)을 사용자에게 알리는 경고.</summary>
public sealed record RegionWarning(string WatcherId, string WatcherLabel, RegionKind Kind, string Message, string Filename);

public static class CctvJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, Options); } catch { return default; }
    }

    private static readonly JsonSerializerOptions CompactOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JsonObject 를 한 줄 JSON 으로 (한글을 이스케이프하지 않는다).</summary>
    public static string Compact(JsonObject obj) => obj.ToJsonString(CompactOptions);

    public static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); } catch { return new JsonObject(); }
    }
}
