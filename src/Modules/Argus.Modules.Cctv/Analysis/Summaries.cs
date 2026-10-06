using System.Text.RegularExpressions;

namespace Argus.Modules.Cctv;

public enum LiveStatus { Docked, Observed, Departed }

/// <summary>캐릭터 한 명의 가장 최근 상태.</summary>
public sealed record LatestState(string Character, LiveStatus Status, string Time, string Corporation, string Ship, string Source);

/// <summary>코퍼레이션별로 묶은 현재 인원.</summary>
public sealed class CorpGroup
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>"[HRED]" 꼴, 소속을 모르면 "—".</summary>
    public string Ticker { get; init; } = "";
    public int Detected { get; set; }
    public int Docked { get; set; }
    public int Observed { get; set; }
    public List<(string Ship, int Count)> Ships { get; } = [];
    public List<CorpMember> Members { get; } = [];
}

public sealed record CorpMember(string Name, List<(string Time, string Ship, string Source)> Sightings);

/// <summary>
/// 이벤트·현재 대상에서 대시보드 요약(현재 도킹 확인 / 감지·미도킹 / 성계 이탈)과 코퍼레이션별 현황을 만든다.
/// 기존 웹앱(app/page.tsx)의 집계 규칙을 그대로 옮겼다. events 는 최신순(내림차순)이어야 한다.
/// </summary>
public static class Summaries
{
    /// <summary>
    /// 코퍼레이션 티커로 받아들일 수 있는 글자인지: 1~5자의 영문·숫자(와 . - _)뿐이어야 한다.
    /// 비전 모델이 다른 줄의 함선·구조물 이름(예: "Raitaru*")을 콥 칸에 잘못 옮겨 적는 일이 있어 걸러 낸다.
    /// 받아들일 수 없으면 빈 문자열.
    /// </summary>
    public static string ValidTicker(string? value)
    {
        var t = Regex.Replace(Regex.Replace(value ?? "", @"^\[|\]$", ""), @"\s+", "");
        return Regex.IsMatch(t, @"^[A-Za-z0-9._\-]{1,5}$") ? t : "";
    }

    private static string CleanTicker(string? value) => ValidTicker(value).ToUpperInvariant();

    // OCR/비전이 헷갈리는 글자를 같은 것으로 접는다: G→6, O→0, I·L→1, S→5, B→8
    private static string Fold(string v) => v.Replace('G', '6').Replace('O', '0').Replace('I', '1').Replace('L', '1').Replace('S', '5').Replace('B', '8');

    /// <summary>
    /// 콥 티커가 조금 다르게 읽힌 것들을 가장 많이 나온 철자로 모은다 (같은 길이이고 헷갈리는 글자를 접은 모양이 같은 것끼리).
    /// 반환: 읽은 티커 → 대표 티커.
    /// </summary>
    public static Func<string?, string> CorporationCanonicalizer(IEnumerable<EventRow> events, IEnumerable<CurrentObject> objects)
    {
        var counts = new Dictionary<string, int>();
        foreach (var v in events.Select(e => e.Corporation).Concat(objects.Select(o => o.Corporation)))
        {
            var t = CleanTicker(v);
            if (t.Length > 0) counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        var aliases = new Dictionary<string, string>();
        foreach (var ticker in counts.Keys)
            aliases[ticker] = counts.Keys.Where(c => c.Length == ticker.Length && Fold(c) == Fold(ticker)).OrderByDescending(c => counts[c]).FirstOrDefault() ?? ticker;
        return value =>
        {
            var clean = CleanTicker(value);
            return aliases.TryGetValue(clean, out var a) ? a : clean.Length > 0 ? clean : "미확인";
        };
    }

    public static Dictionary<string, LatestState> BuildLatestStates(IReadOnlyList<EventRow> events, IReadOnlyList<CurrentObject> objects, Func<string?, string> canonical)
    {
        var identities = new Dictionary<string, (string Corporation, string Ship, string Time)>();
        void Remember(string character, string? corporation, string? ship, string time)
        {
            identities.TryGetValue(character, out var prev);
            identities[character] = (
                !string.IsNullOrEmpty(corporation) ? canonical(corporation) : prev.Corporation ?? "미확인",
                !string.IsNullOrEmpty(ship) ? ship : prev.Ship ?? "미확인 함선",
                string.CompareOrdinal(time, prev.Time ?? "") > 0 ? time : prev.Time ?? time);
        }
        foreach (var o in objects) Remember(o.Character, o.Corporation, o.Ship, o.LastSeenAt);
        foreach (var e in events.Reverse()) if (!string.IsNullOrEmpty(e.Character) && !e.Type.StartsWith("signature_")) Remember(e.Character!, e.Corporation, e.Ship, e.Time);

        var latest = new Dictionary<string, LatestState>();
        void Commit(string character, LiveStatus status, string time, string? corporation, string? ship, string source)
        {
            if (latest.TryGetValue(character, out var prev) && string.CompareOrdinal(prev.Time, time) >= 0) return;
            identities.TryGetValue(character, out var id);
            latest[character] = new LatestState(character, status, time, !string.IsNullOrEmpty(corporation) ? canonical(corporation) : id.Corporation ?? "미확인", !string.IsNullOrEmpty(ship) ? ship : id.Ship ?? "미확인 함선", source);
        }
        foreach (var o in objects) Commit(o.Character, LiveStatus.Observed, o.LastSeenAt, o.Corporation, o.Ship, o.WatcherLabel);
        foreach (var e in events)
        {
            if (string.IsNullOrEmpty(e.Character) || e.Type.StartsWith("signature_")) continue;
            LiveStatus? status = e.Type == "docked" ? LiveStatus.Docked
                : e.Type is "warp_in" or "jump_in" or "undocked" or "appeared" or "covop_in" ? LiveStatus.Observed
                : e.Type is "jump_out" or "disappeared" or "covop_out" ? LiveStatus.Departed : null; // 워프아웃은 같은 성계 안의 이동이라 이탈로 세지 않는다
            if (status != null) Commit(e.Character!, status.Value, e.Time, e.Corporation, e.Ship, e.WatcherLabel ?? "미지정 눈깔");
        }
        return latest;
    }

    public static List<CorpGroup> BuildCorpGroups(IReadOnlyList<EventRow> events, IReadOnlyList<CurrentObject> objects, Func<string?, string> canonical)
    {
        var active = new Dictionary<string, (string Character, string Ship, string Corporation, LiveStatus Status, string Time, string Source)>();
        foreach (var o in objects) active[o.Character] = (o.Character, o.Ship ?? "미확인 함선", o.Corporation ?? "미확인", LiveStatus.Observed, o.LastSeenAt, o.WatcherLabel);
        var decided = new HashSet<string>();
        foreach (var e in events)
        {
            if (string.IsNullOrEmpty(e.Character) || e.Type.StartsWith("signature_") || active.ContainsKey(e.Character!)) continue;
            if (!decided.Add(e.Character!)) continue;
            if (e.Type == "docked") active[e.Character!] = (e.Character!, e.Ship ?? "미확인 함선", e.Corporation ?? "미확인", LiveStatus.Docked, e.Time, e.WatcherLabel ?? "미지정 눈깔");
        }

        var groups = new Dictionary<string, CorpGroup>();
        foreach (var member in active.Values)
        {
            var ticker = canonical(member.Corporation);
            var key = ticker.ToUpperInvariant();
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new CorpGroup { Key = key, Name = ticker == "미확인" ? "소속 미확인" : ticker, Ticker = ticker == "미확인" ? "—" : $"[{ticker}]" };
            group.Detected++;
            if (member.Status == LiveStatus.Docked) group.Docked++; else group.Observed++;
            var at = group.Ships.FindIndex(s => s.Ship == member.Ship);
            if (at >= 0) group.Ships[at] = (member.Ship, group.Ships[at].Count + 1); else group.Ships.Add((member.Ship, 1));

            // 같은 캐릭터가 같은 함선으로 반복 감지된 것은 한 번만 보여 준다.
            var history = events.Where(e => e.Character == member.Character && !string.IsNullOrEmpty(e.Ship))
                .GroupBy(e => e.Ship).Select(g => g.First()).Select(e => (Time(e.Time), e.Ship ?? "미확인 함선", e.WatcherLabel ?? "미지정 눈깔")).ToList();
            group.Members.Add(new CorpMember(member.Character, history.Count > 0 ? history : [(Time(member.Time), member.Ship, member.Source)]));
        }
        return [.. groups.Values.OrderByDescending(g => g.Detected).ThenBy(g => g.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>"2026-09-19T01:28:00.000" → "01:28:00".</summary>
    public static string Time(string iso) => iso.Length >= 19 ? iso.Substring(11, 8) : iso;
}
