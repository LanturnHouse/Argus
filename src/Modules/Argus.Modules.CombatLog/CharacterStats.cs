using Argus.Core.Combat;
using Argus.Core.Events;

namespace Argus.Modules.CombatLog;

/// <summary>한 캐릭터의 최근 전투 수치. 사건을 시각과 함께 쌓아 두고, 최근 N초 구간의 합으로 초당 값을 낸다.</summary>
internal sealed class CharacterStats(string character)
{
    /// <summary>가장 긴 구간(초). 이보다 오래된 사건은 버린다.</summary>
    private const int KeepSeconds = CombatLogSettings.MaxWindow;

    private readonly Queue<(long At, double Amount)> _in = new(), _logi = new(), _neut = new(), _capGain = new();
    private readonly SurgeDetector _surge = new();   // 레드박싱 판정 (프리뷰의 기능 테스트와 같은 규칙)

    public string Character { get; } = character;

    /// <summary>파싱한 사건을 반영한다. nowMs 는 사건을 읽은 시각(밀리초).</summary>
    public void Add(CombatEvent e, long nowMs)
    {
        switch (e.Kind)
        {
            case CombatKind.DamageIn: _in.Enqueue((nowMs, e.Amount)); _surge.Add(nowMs, e.Amount); break;
            case CombatKind.LogiIn: _logi.Enqueue((nowMs, e.Amount)); break;
            case CombatKind.NeutIn: _neut.Enqueue((nowMs, e.Amount)); break;
            case CombatKind.CapGain: _capGain.Enqueue((nowMs, e.Amount)); break;
        }
    }

    public CombatSnapshot Snapshot(long nowMs, double windowSeconds, double surgeMinDps = 300, double surgeRatio = 3)
    {
        var window = Math.Max(1, windowSeconds);
        var rate = Rate(_in, nowMs, window);
        _surge.Update(nowMs, surgeMinDps, surgeRatio);
        return new CombatSnapshot(Character,
            rate, Rate(_logi, nowMs, window),
            Rate(_neut, nowMs, window) - Rate(_capGain, nowMs, window),   // 이펙티브 뉴트량: 빠진 캡(뉴트 + 노스 피해) − 채워진 캡(내가 노스로 빤 양 + 원격 캡 전송 받음)
            _surge.SurgeAt);
    }

    private static double Rate(Queue<(long At, double Amount)> q, long nowMs, double windowSeconds)
    {
        while (q.Count > 0 && nowMs - q.Peek().At > KeepSeconds * 1000L) q.Dequeue();
        var from = nowMs - (long)(windowSeconds * 1000);
        double sum = 0;
        foreach (var (at, amount) in q) if (at >= from) sum += amount;
        return sum / windowSeconds;
    }
}
