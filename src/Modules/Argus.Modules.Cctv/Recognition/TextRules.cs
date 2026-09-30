using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Argus.Modules.Cctv;

/// <summary>인식한 글자를 값으로 바꾸는 규칙들 (속도, 프로빙 ID, 비전 모델 응답 보정). 기존 웹앱의 numbers.mjs · vision.mjs 규칙을 그대로 옮겼다.</summary>
public static class TextRules
{
    /// <summary>
    /// 오버뷰 속도 글자를 숫자(m/s)로. EVE 는 천 단위에 쉼표, 소수점에 점을 쓰는데 OCR 이 쉼표를 점으로 읽기도 한다.
    /// 마지막 묶음이 세 자리면 천 단위 구분으로 보고(0.xxx 는 제외), 짧은 소수는 소수로 둔다.
    /// </summary>
    public static double? ParseOverviewSpeed(string? value)
    {
        var text = Regex.Replace(value ?? "", @"(\d)\s*([,.])\s*(?=\d)", "$1$2");
        var token = Regex.Match(text, @"-?\d+(?:[,.]\d+)*").Value;
        if (token.Length == 0) return null;
        token = token.TrimStart('-');

        var groups = Regex.Split(token, "[,.]");
        var separators = Regex.Matches(token, "[,.]");
        var finalGroup = groups[^1];
        var integerPart = string.Concat(groups[..^1]);
        var decimalPoint = separators.Count > 0 && separators[^1].Value == "." && (finalGroup.Length <= 2 || (integerPart == "0" && finalGroup.Length == 3));
        var normalized = decimalPoint ? $"{integerPart}.{finalGroup}" : string.Concat(groups);
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && !double.IsNaN(speed) && !double.IsInfinity(speed) ? speed : null;
    }

    public static double? ParseOverviewSpeed(double value) => double.IsNaN(value) || double.IsInfinity(value) ? null : Math.Abs(value);

    private static string CleanText(string? value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();

    private static readonly Regex ProbeIdPattern = new(@"([A-Z0-9]{3})-\S{1,3}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 시그니처 ID 는 "AHQ-5" 꼴(앞 3글자 + 1~3글자)인데, 뒤쪽이 프레임마다 가장 자주 틀리게 읽혀 신원 매칭을 깬다.
    /// 앞 3글자가 훨씬 안정적이고 한 성계의 시그니처 목록 안에서는 충분히 유일하므로 그것만 ID 로 쓴다 (뒤쪽은 'XXX-###' 꼴인지 확인하는 닻으로만 쓰고 버린다).
    /// </summary>
    public static string? NormalizeProbeId(string? rawText)
    {
        var m = ProbeIdPattern.Match((rawText ?? "").ToUpperInvariant());
        if (!m.Success) return null;
        // 접두어는 게임에서 항상 글자이므로 흔한 숫자 오독을 되돌린다.
        return m.Groups[1].Value.Replace('0', 'O').Replace('1', 'I').Replace('5', 'S').Replace('8', 'B');
    }

    /// <summary>OCR 원문 줄에서 그 이름 뒤에 나오는 속도 후보(쉼표·점이 있는 숫자)들 중 가장 큰 값.</summary>
    internal static double? SpeedFromOcrLine(string rawText, string name)
    {
        var parts = CleanText(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var pattern = new Regex(string.Join(@"\s*", parts.Select(Regex.Escape)), RegexOptions.IgnoreCase);
        foreach (var line in Regex.Split(rawText ?? "", @"\r?\n"))
        {
            var m = pattern.Match(line);
            if (!m.Success) continue;
            var suffix = line[(m.Index + m.Length)..];
            var candidates = Regex.Matches(suffix, @"-?\d+(?:[,.]\d+)*").Select(t => t.Value).Where(t => t.IndexOfAny([',', '.']) >= 0)
                .Select(t => ParseOverviewSpeed(t)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (candidates.Count > 0) return candidates.Max();
        }
        return null;
    }

    private static string S(JsonNode? n) => n switch { null => "", JsonValue v when v.TryGetValue<string>(out var s) => s, var o => o.ToString() };

    private static double? SpeedOf(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return ParseOverviewSpeed(d);
            if (v.TryGetValue<string>(out var s)) return ParseOverviewSpeed(s);
        }
        return null;
    }

    /// <summary>
    /// 비전 모델의 JSON 응답을 영역 종류별 필드로 바꾼다. 응답이 없거나 모양이 틀리면 fallback(Tesseract 결과)을 그대로 돌려준다.
    /// 속도: 모델이 "1,775,962"를 1.775962 처럼 작은 소수로 돌려줘도 OCR 원문 줄의 큰 속도로 바로잡는다.
    /// </summary>
    public static RegionFields ShapeVisionFields(RegionKind kind, JsonObject? vision, RegionFields fallback, string rawText = "")
    {
        if (vision == null) return fallback;

        if (kind == RegionKind.Probe)
        {
            if (vision["rows"] is not JsonArray rows) return fallback;
            var signatures = rows.OfType<JsonObject>().Select(r => new { Id = NormalizeProbeId(S(r["id"])), Row = r }).Where(x => x.Id != null)
                .Select(x => new SignatureRow { Id = x.Id!, Distance = CleanText(S(x.Row["distance"])), Name = CleanText(S(x.Row["name"])), Group = CleanText(S(x.Row["group"])), Raw = "", Confidence = 0.9 }).ToList();
            return Copy(fallback, f => { f.Signatures = signatures; f.ProbeDetected = IsTrue(vision["visible"]) || signatures.Count > 0; });
        }

        if (kind == RegionKind.Overview)
        {
            if (vision["rows"] is not JsonArray rows) return fallback;
            var overview = rows.OfType<JsonObject>().Select(r =>
            {
                var modelSpeed = SpeedOf(r["speed"]);
                var name = CleanText(S(r["name"]));
                var ocrSpeed = SpeedFromOcrLine(rawText, name);
                return new OverviewRow
                {
                    Distance = CleanText(S(r["distance"])), Name = name, Ship = CleanText(S(r["ship"])), Corporation = CleanText(S(r["corporation"])),
                    Speed = ocrSpeed >= 10_000 && (modelSpeed == null || modelSpeed < 10_000) ? ocrSpeed : modelSpeed, Raw = "", Confidence = 0.9,
                };
            }).Where(r => r.Name.Length > 0).ToList();
            return Copy(fallback, f => { f.OverviewRows = overview; f.OverviewDetected = IsTrue(vision["visible"]) || overview.Count > 0; });
        }

        if (kind == RegionKind.Dock)
        {
            if (vision["count"] is JsonValue v && v.TryGetValue<double>(out var c) && !double.IsNaN(c) && !double.IsInfinity(c))
                return Copy(fallback, f => f.DockCount = (int)c);
            return fallback;
        }

        return fallback;
    }

    private static bool IsTrue(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static RegionFields Copy(RegionFields src, Action<RegionFields> change)
    {
        var copy = new RegionFields
        {
            Lines = src.Lines, OverviewRows = src.OverviewRows, OverviewDetected = src.OverviewDetected,
            Signatures = src.Signatures, ProbeDetected = src.ProbeDetected, DockCount = src.DockCount,
        };
        change(copy);
        return copy;
    }
}
