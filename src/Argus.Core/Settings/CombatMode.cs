using Argus.Core.Events;

namespace Argus.Core.Settings;

/// <summary>
/// 전투 모드 / 비전투 모드. 비전투 모드에서는 전투 로그 읽기, 프리뷰 수치 표시, 레드박싱을 모두 멈춰 리소스를 아낀다.
/// 사이드바에서 사용자가 바꾸며, 마지막 선택을 기억한다. 각 모듈은 시작할 때 <see cref="Load"/> 로 현재 값을 읽고 <see cref="CombatModeChanged"/> 로 바뀜을 받는다.
/// </summary>
public static class CombatMode
{
    private const string Key = "combatmode";

    private sealed class State { public bool Active { get; set; } }

    /// <summary>저장된 모드. 저장된 적이 없으면 비전투(false).</summary>
    public static bool Load(ISettingsStore settings) => settings.Load<State>(Key).Active;

    /// <summary>모드를 저장하고 모두에게 알린다.</summary>
    public static void Set(ISettingsStore settings, IEventBus bus, bool active)
    {
        try { settings.Save(Key, new State { Active = active }); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[CombatMode] 저장 실패: {ex.Message}"); }   // 저장 실패가 모드 전환을 막지 않게
        bus.Publish(new CombatModeChanged(active));
    }
}
