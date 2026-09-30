namespace Argus.Core.Settings;

/// <summary>
/// 레드박싱 판정 기준. 전투 로그 모듈이 설정(키 "combatlog")에 저장하는 값 중 이 두 가지를 다른 모듈(프리뷰의 기능 테스트)이 읽기만 한다.
/// 모듈끼리 직접 참조하지 않고 저장소를 통해서만 공유한다.
/// </summary>
public sealed class CombatSurgeThresholds
{
    public const string SettingsKey = "combatlog";
    public int SurgeMinDps { get; set; } = 300;
    public double SurgeRatio { get; set; } = 3.0;
}
