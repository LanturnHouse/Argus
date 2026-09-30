namespace Argus.Core.Combat;

/// <summary>
/// 레드박싱 판정: 최근 3초의 받는 DPS 가 기준치 이상이고, 그 직전 30초 평균의 N배 이상일 때 레드박싱이다.
/// (직전 30초 평균에는 조용했던 구간도 0 으로 들어가므로 갑자기 세게 맞기 시작하면 잡힌다.)
/// 한 번 감지된 뒤에는 받는 DPS 가 기준 아래로 내려갔다 올라와야 새 레드박싱으로 센다 (같은 전투를 계속 새 레드박싱으로 세지 않게).
/// 전투 로그 모듈(실제 로그)과 프리뷰의 기능 테스트(시뮬레이션)가 같은 규칙을 쓰도록 여기에 둔다.
/// </summary>
public sealed class SurgeDetector
{
    private const int RecentSeconds = 3, BaselineSeconds = 30, KeepSeconds = 60;

    private readonly Queue<(long At, double Amount)> _damage = new();
    private bool _armed = true;

    /// <summary>가장 최근 레드박싱이 감지된 시각(밀리초). 없으면 long.MinValue.</summary>
    public long SurgeAt { get; private set; } = long.MinValue;

    /// <summary>받은 피해 하나를 기록한다 (nowMs: 그 시각).</summary>
    public void Add(long nowMs, double amount) => _damage.Enqueue((nowMs, amount));

    /// <summary>지금 시각 기준으로 레드박싱인지 판정한다. 새로 감지되면 <see cref="SurgeAt"/> 이 갱신된다.</summary>
    public void Update(long nowMs, double minDps, double ratio)
    {
        while (_damage.Count > 0 && nowMs - _damage.Peek().At > KeepSeconds * 1000L) _damage.Dequeue();

        var recent = SumBetween(nowMs - RecentSeconds * 1000L, nowMs) / RecentSeconds;
        if (recent < minDps) { _armed = true; return; }   // 기준 아래로 내려갔다 → 다음 레드박싱을 다시 감지할 수 있다
        if (!_armed) return;
        var baseline = SumBetween(nowMs - (RecentSeconds + BaselineSeconds) * 1000L, nowMs - RecentSeconds * 1000L) / BaselineSeconds;
        if (recent < baseline * ratio) return;
        SurgeAt = nowMs;
        _armed = false;
    }

    private double SumBetween(long from, long to)
    {
        double sum = 0;
        foreach (var (at, amount) in _damage) if (at > from && at <= to) sum += amount;
        return sum;
    }
}
