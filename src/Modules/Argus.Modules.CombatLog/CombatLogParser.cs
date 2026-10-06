using System.Net;
using System.Text.RegularExpressions;

namespace Argus.Modules.CombatLog;

public enum LogLanguage { Korean, English }
public enum CombatKind { DamageIn, LogiIn, NeutIn, CapGain }

/// <summary>
/// 로그 한 줄에서 읽은 사건. Amount: 피해·수리량(HP) 또는 캐패시터 양(GJ).
/// NeutIn = 내 캐패시터가 빠진 양(받은 뉴트 + 노스 피해), CapGain = 내 캐패시터가 채워진 양(내가 노스페라투로 빤 양 + 원격 캐패시터 전송 받음).
/// </summary>
public readonly record struct CombatEvent(CombatKind Kind, double Amount);

/// <summary>
/// EVE 전투 로그(Gamelogs) 한 줄을 해석한다. 로그는 클라이언트 언어로 기록되므로 언어별 문구표를 쓴다.
/// 한국어는 이 PC 의 실제 로그로 검증했다. 영어는 알려진 형식에 맞춘 것이라 실제 영어 로그로는 확인하지 못했다.
/// 태클(스크램블·디스럽터·HIC)은 로그에 걸린 순간 한 줄만 남고 유지·해제는 기록되지 않아서 다루지 않는다. 버블도 로그에 남지 않는다. 내가 입히는 피해는 HUD 에 쓰지 않아 읽지 않는다.
/// </summary>
public static class CombatLogParser
{
    private static readonly Regex Header = new(@"^\s*(청취자|Listener):\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Combat = new(@"^\[ \d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2} \] \(combat\) (.*)$", RegexOptions.Compiled);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    // 한국어
    private static readonly Regex KoIn = new(@"^([\d,]+) 의 피해를 .+?에 의해 입음", RegexOptions.Compiled);
    private static readonly Regex KoLogi = new(@"^([\d,]+): 원격 (?:장갑 수리|실드 충전|선체 수리)받음", RegexOptions.Compiled);
    private static readonly Regex KoNeut = new(@"^-([\d,]+) GJ: 에너지 (?:뉴트럴라이즈|흡수 당함)", RegexOptions.Compiled);   // 뉴트 + 노스 피해: 내 캡이 빠지는 양
    private static readonly Regex KoNosGain = new(@"^\+([\d,]+) GJ: 에너지 흡수(?! 당함)", RegexOptions.Compiled);          // 내가 노스로 빤 양
    private static readonly Regex KoCapIn = new(@"^([\d,]+): 캐패시터 원격 전송받음", RegexOptions.Compiled);                  // 원격 캡 전송을 받은 양 (내가 보낸 `캐패시터 원격 전송-` 은 제외)

    // 영어 (미검증)
    private static readonly Regex EnIn = new(@"^([\d,]+) from .+", RegexOptions.Compiled);
    private static readonly Regex EnLogi = new(@"^([\d,]+) remote (?:armor|shield|hull) (?:repaired|boosted) by ", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnNeut = new(@"^-([\d,]+) GJ energy (?:neutralized|drained to)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnNosGain = new(@"^\+([\d,]+) GJ energy drained from", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnCapIn = new(@"^([\d,]+) remote capacitor (?:transmitted|transferred) by ", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        var plain = WebUtility.HtmlDecode(Tags.Replace(m.Groups[1].Value, "")).Trim();
        return language == LogLanguage.Korean ? ParseKo(plain) : ParseEn(plain);
    }

    private static CombatEvent? ParseKo(string plain)
    {
        if (KoIn.Match(plain) is { Success: true } i) return new(CombatKind.DamageIn, Num(i));
        if (KoLogi.Match(plain) is { Success: true } l) return new(CombatKind.LogiIn, Num(l));
        if (KoNeut.Match(plain) is { Success: true } n) return new(CombatKind.NeutIn, Num(n));
        if (KoNosGain.Match(plain) is { Success: true } g) return new(CombatKind.CapGain, Num(g));
        if (KoCapIn.Match(plain) is { Success: true } c) return new(CombatKind.CapGain, Num(c));
        return null;
    }

    private static CombatEvent? ParseEn(string plain)
    {
        if (EnIn.Match(plain) is { Success: true } i) return new(CombatKind.DamageIn, Num(i));
        if (EnLogi.Match(plain) is { Success: true } l) return new(CombatKind.LogiIn, Num(l));
        if (EnNeut.Match(plain) is { Success: true } n) return new(CombatKind.NeutIn, Num(n));
        if (EnNosGain.Match(plain) is { Success: true } g) return new(CombatKind.CapGain, Num(g));
        if (EnCapIn.Match(plain) is { Success: true } c) return new(CombatKind.CapGain, Num(c));
        return null;
    }

    private static double Num(Match m) => double.Parse(m.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
}
