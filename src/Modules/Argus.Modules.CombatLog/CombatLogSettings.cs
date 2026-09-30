namespace Argus.Modules.CombatLog;

public sealed class CombatLogSettings
{
    /// <summary>수치(DPS 등)를 계산하는 최근 구간(초). 이 구간의 합을 초로 나눈 평균이다.</summary>
    public int WindowSeconds { get; set; } = 10;

    /// <summary>레드박싱 판정: 최근 3초의 받는 DPS 가 이 값 이상이어야 한다.</summary>
    public int SurgeMinDps { get; set; } = 300;

    /// <summary>레드박싱 판정: 최근 3초의 받는 DPS 가 그 직전 30초 평균의 이 배수 이상이어야 한다.</summary>
    public double SurgeRatio { get; set; } = 3.0;

    /// <summary>전투 로그 폴더. 비어 있으면 문서\EVE\logs\Gamelogs.</summary>
    public string LogFolder { get; set; } = "";

    public const int MinWindow = 3, MaxWindow = 60, MinSurgeDps = 50, MaxSurgeDps = 5000;
    public const double MinSurgeRatio = 1.5, MaxSurgeRatio = 10;
}
