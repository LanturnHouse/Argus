using System.Net;
using System.Text.RegularExpressions;

namespace Argus.Modules.CombatLog;

public enum LogLanguage { Korean, English }
public enum CombatKind { DamageIn, LogiIn, NeutIn, CapGain, Tackle }
public enum TackleType { None, Scram, Disrupt }

/// <summary>
/// 로그 한 줄에서 읽은 사건. Amount: 피해·수리량(HP) 또는 캐패시터 양(GJ). 태클이면 종류와 시전자가 HIC 함선인지.
/// NeutIn = 내 캐패시터가 빠진 양(받은 뉴트 + 노스 피해), CapGain = 내 캐패시터가 채워진 양(내가 노스페라투로 빤 양 + 원격 캐패시터 전송 받음).
/// </summary>
public readonly record struct CombatEvent(CombatKind Kind, double Amount, TackleType Tackle = TackleType.None, bool Hic = false);

/// <summary>
/// EVE 전투 로그(Gamelogs) 한 줄을 해석한다. 로그는 클라이언트 언어로 기록되므로 언어별 문구표를 쓴다.
/// 한국어는 이 PC 의 실제 로그로 검증했다. 영어는 알려진 형식에 맞춘 것이라 실제 영어 로그로는 확인하지 못했다.
/// 버블(범위 교란)은 로그에 남지 않아 다루지 않는다. 내가 입히는 피해는 HUD 에 쓰지 않아 읽지 않는다.
/// </summary>
public static class CombatLogParser
{
    private static readonly Regex Header = new(@"^\s*(청취자|Listener):\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Combat = new(@"^\[ \d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2} \] \(combat\) (.*)$", RegexOptions.Compiled);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ShipName = new("<localized hint=\"([^\"]*)\">([^<]*)", RegexOptions.Compiled);

    // 한국어
    private static readonly Regex KoIn = new(@"^([\d,]+) 의 피해를 .+?에 의해 입음", RegexOptions.Compiled);
    private static readonly Regex KoLogi = new(@"^([\d,]+): 원격 (?:장갑 수리|실드 충전|선체 수리)받음", RegexOptions.Compiled);
    private static readonly Regex KoNeut = new(@"^-([\d,]+) GJ: 에너지 (?:뉴트럴라이즈|흡수 당함)", RegexOptions.Compiled);   // 뉴트 + 노스 피해: 내 캡이 빠지는 양
    private static readonly Regex KoNosGain = new(@"^\+([\d,]+) GJ: 에너지 흡수(?! 당함)", RegexOptions.Compiled);          // 내가 노스로 빤 양
    private static readonly Regex KoCapIn = new(@"^([\d,]+): 캐패시터 원격 전송받음", RegexOptions.Compiled);                  // 원격 캡 전송을 받은 양 (내가 보낸 `캐패시터 원격 전송-` 은 제외)
    private static readonly Regex KoTackle = new(@"^워프 (스크램블|디스럽트) 시도:?\s*시전자 - .*?, 대상 - 당신\s*$", RegexOptions.Compiled);

    // 영어 (미검증)
    private static readonly Regex EnIn = new(@"^([\d,]+) from .+", RegexOptions.Compiled);
    private static readonly Regex EnLogi = new(@"^([\d,]+) remote (?:armor|shield|hull) (?:repaired|boosted) by ", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnNeut = new(@"^-([\d,]+) GJ energy (?:neutralized|drained to)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnNosGain = new(@"^\+([\d,]+) GJ energy drained from", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnCapIn = new(@"^([\d,]+) remote capacitor (?:transmitted|transferred) by ", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnTackle = new(@"^warp (scramble|disrupt)\b.*\byou\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // HIC(헤비 인터딕터) 함선. 로그는 이름을 '<localized hint="다른 언어 이름">표시 이름*' 으로 적으며 두 언어의 순서가 줄마다 다르므로 둘 다 본다.
    private static readonly HashSet<string> HicShips = new(StringComparer.OrdinalIgnoreCase)
    {
        "onyx", "broadsword", "phobos", "devoter",
        "오닉스", "브로드소드", "포보스", "디보우터", "디보터",
    };

    /// <summary>로그 머리글 줄이면 청취자(캐릭터) 이름과 언어를 돌려준다.</summary>
    public static bool TryParseHeader(string line, out string name, out LogLanguage language)
    {
        var m = Header.Match(line.TrimEnd('\r'));
        name = m.Success ? m.Groups[2].Value : "";
        language = m.Success && m.Groups[1].Value == "Listener" ? LogLanguage.English : LogLanguage.Korean;
        return m.Success;
    }

    /// <summary>전투 사건이면 해석해서 돌려주고, 아니면 null (대화, 알림, 빗나감, 내가 건 원격 수리 등).</summary>
    public static CombatEvent? Parse(string line, LogLanguage language)
    {
        var m = Combat.Match(line.TrimEnd('\r'));
        if (!m.Success) return null;
        var raw = m.Groups[1].Value;
        var plain = WebUtility.HtmlDecode(Tags.Replace(raw, "")).Trim();
        return language == LogLanguage.Korean ? ParseKo(raw, plain) : ParseEn(raw, plain);
    }

    private static CombatEvent? ParseKo(string raw, string plain)
    {
        if (KoIn.Match(plain) is { Success: true } i) return new(CombatKind.DamageIn, Num(i));
        if (KoLogi.Match(plain) is { Success: true } l) return new(CombatKind.LogiIn, Num(l));
        if (KoNeut.Match(plain) is { Success: true } n) return new(CombatKind.NeutIn, Num(n));
        if (KoNosGain.Match(plain) is { Success: true } g) return new(CombatKind.CapGain, Num(g));
        if (KoCapIn.Match(plain) is { Success: true } c) return new(CombatKind.CapGain, Num(c));
        if (KoTackle.Match(plain) is { Success: true } t) return Tackle(raw, t.Groups[1].Value == "스크램블");
        return null;
    }

    private static CombatEvent? ParseEn(string raw, string plain)
    {
        if (EnIn.Match(plain) is { Success: true } i) return new(CombatKind.DamageIn, Num(i));
        if (EnLogi.Match(plain) is { Success: true } l) return new(CombatKind.LogiIn, Num(l));
        if (EnNeut.Match(plain) is { Success: true } n) return new(CombatKind.NeutIn, Num(n));
        if (EnNosGain.Match(plain) is { Success: true } g) return new(CombatKind.CapGain, Num(g));
        if (EnCapIn.Match(plain) is { Success: true } c) return new(CombatKind.CapGain, Num(c));
        if (EnTackle.Match(plain) is { Success: true } t) return Tackle(raw, t.Groups[1].Value.StartsWith("scram", StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static double Num(Match m) => double.Parse(m.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);

    private static CombatEvent Tackle(string raw, bool scram) =>
        new(CombatKind.Tackle, 0, scram ? TackleType.Scram : TackleType.Disrupt, CasterIsHic(raw));

    /// <summary>'시전자 - … , 대상 - …' 에서 시전자 쪽의 함선 이름이 HIC 인가.</summary>
    internal static bool CasterIsHic(string raw)
    {
        var start = raw.IndexOf("시전자 -", StringComparison.Ordinal);
        if (start < 0) start = 0;
        var end = raw.IndexOf(", 대상 -", start, StringComparison.Ordinal);
        if (end < 0) end = raw.Length;
        foreach (Match s in ShipName.Matches(raw[start..end]))
            foreach (var name in new[] { s.Groups[1].Value, s.Groups[2].Value })
                if (HicShips.Contains(name.Trim().TrimEnd('*').Trim())) return true;
        return false;
    }
}
