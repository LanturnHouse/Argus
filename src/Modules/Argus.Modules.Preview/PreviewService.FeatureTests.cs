using Argus.Core.Combat;
using Argus.Core.Events;
using Argus.Core.Settings;

namespace Argus.Modules.Preview;

/// <summary>
/// 수치 시험: 초기 수치에서 피크 수치까지 천천히 오르고(상승), 피크에서 잠시 머물고(유지), 다시 초기 수치로 내려온다(하강).
/// 받는 DPS · 받는 LOGI · 받는 뉴트 중 고른 항목에 같은 값이 표시된다. 받는 DPS 를 고르면 급히 오르는 순간 실제와 같은 규칙으로 레드박싱도 일어난다.
/// </summary>
public sealed record TestRampSpec(bool DpsIn, bool Logi, bool Neut, double Initial, double Peak, double RiseSeconds, double HoldSeconds, double FallSeconds)
{
    public double TotalSeconds => RiseSeconds + HoldSeconds + FallSeconds;

    /// <summary>시작 후 t 초에서의 값 (직선으로 오르고 내린다).</summary>
    public double ValueAt(double t)
    {
        if (t < RiseSeconds) return Lerp(Initial, Peak, RiseSeconds <= 0 ? 1 : t / RiseSeconds);
        t -= RiseSeconds;
        if (t < HoldSeconds) return Peak;
        t -= HoldSeconds;
        if (t < FallSeconds) return Lerp(Peak, Initial, FallSeconds <= 0 ? 1 : t / FallSeconds);
        return Initial;
    }

    private static double Lerp(double a, double b, double f) => a + (b - a) * Math.Clamp(f, 0, 1);
}

/// <summary>
/// 기능 시험(설정 > 프리뷰 > 기능 테스트): 실제 전투 없이 프리뷰 HUD 의 레드박싱 · 수치를 시험한다.
/// 시험 값은 실제 전투 로그 값 위에 겹쳐서 적용되고(시험이 우선), 끝나면 저절로 실제 값으로 돌아간다.
/// 레드박싱 시험은 진짜 레드박싱과 똑같이 레드박싱 전환 단축키도 켠다.
/// </summary>
public sealed partial class PreviewService
{
    private sealed class FeatureTest
    {
        public long SurgeAt = long.MinValue;
        public TestRampSpec? Ramp;
        public long RampStart;
        // 수치 시험 중 받는 DPS 가 급히 오르면 실제와 같은 규칙으로 레드박싱이 감지되게 하는 시뮬레이션
        public SurgeDetector? Sim;
        public long LastFeed;
        public CombatSurgeThresholds? Thresholds;
    }

    private readonly Dictionary<string, FeatureTest> _featureTests = new(StringComparer.OrdinalIgnoreCase);

    public bool FeatureTestActive => _featureTests.Count > 0;

    // 시험 곡선이 매끄럽게 움직이도록 시험 중에만 도는 전용 타이머 (일반 150ms 주기 작업은 낮은 우선순위라 바쁠 때 밀릴 수 있다).
    private System.Windows.Threading.DispatcherTimer? _testTimer;

    private void EnsureTestTimer()
    {
        _testTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(50) };
        _testTimer.Tick -= OnTestTimer;
        _testTimer.Tick += OnTestTimer;
        if (!_testTimer.IsEnabled) _testTimer.Start();
    }

    private void OnTestTimer(object? sender, EventArgs e)
    {
        TickFeatureTests();
        RefreshSurge();
        if (_featureTests.Count == 0) _testTimer?.Stop();
    }

    /// <summary>실행 중인 클라이언트 이름들 (시험 대상 고르기용).</summary>
    public List<string> RunningCharacters() => [.. _tiles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    private List<string> Targets(string? character) =>
        character == null ? [.. _tiles.Keys] : _tiles.ContainsKey(character) ? [character] : [];

    private FeatureTest Test(string character)
    {
        if (!_featureTests.TryGetValue(character, out var t)) _featureTests[character] = t = new FeatureTest();
        return t;
    }

    /// <summary>레드박싱을 지금 일으킨다: 붉은 깜빡임, 전환 키 안내, 레드박싱 전환 단축키(설정된 경우)가 실제 레드박싱처럼 동작한다. character 가 null 이면 모든 클라이언트.</summary>
    public void TestSurge(string? character)
    {
        var now = Environment.TickCount64;
        foreach (var c in Targets(character)) Test(c).SurgeAt = now;
        ApplyFeatureTests();
    }

    /// <summary>수치를 초기 값에서 피크 값까지 올렸다가 내린다.</summary>
    public void TestRamp(string? character, TestRampSpec spec)
    {
        var now = Environment.TickCount64;
        var thresholds = _ctx.Settings.Load<CombatSurgeThresholds>(CombatSurgeThresholds.SettingsKey);   // 전투 로그 설정의 레드박싱 기준(최소 DPS, 배수)
        foreach (var c in Targets(character))
        {
            var t = Test(c);
            t.Ramp = spec; t.RampStart = now;
            t.Sim = null;
            if (spec.DpsIn)
            {
                // 시작 전 30초 동안은 초기 수치로 맞고 있었던 것으로 본다 (그래야 초기 → 피크 상승이 '레드박싱'인지 실제와 같이 판정된다).
                t.Sim = new SurgeDetector(); t.LastFeed = now; t.Thresholds = thresholds;
                for (int i = 1; i <= 33; i++) t.Sim.Add(now - i * 1000L, Math.Max(0, spec.Initial));
            }
        }
        ApplyFeatureTests();
    }

    public void StopFeatureTests()
    {
        _featureTests.Clear();
        ApplyFeatureTests();
        RefreshSurge(force: true);
    }

    private void ApplyFeatureTests()
    {
        foreach (var tile in _tiles.Values) ApplyCombat(tile);
        RefreshSurge();
        if (_featureTests.Count > 0) EnsureTestTimer();
    }

    /// <summary>실제 전투 로그 값(없을 수 있음)에 시험 값을 겹친 결과. 시험이 없으면 실제 값 그대로.</summary>
    private CombatSnapshot? SnapshotFor(string character)
    {
        var real = _combat.GetValueOrDefault(character);
        if (!_featureTests.TryGetValue(character, out var t)) return real;

        var now = Environment.TickCount64;
        var s = real ?? new CombatSnapshot(character, 0, 0, 0);
        if (t.Ramp is { } r)
        {
            var v = r.ValueAt((now - t.RampStart) / 1000.0);
            s = s with { DpsIn = r.DpsIn ? v : s.DpsIn, LogiIn = r.Logi ? v : s.LogiIn, NeutIn = r.Neut ? v : s.NeutIn };
        }
        if (t.SurgeAt > s.SurgeAt) s = s with { SurgeAt = t.SurgeAt };
        return s;
    }

    /// <summary>주기 작업: 진행 중인 수치 시험을 화면에 반영하고, 끝난 시험은 치워서 실제 값으로 돌아가게 한다.</summary>
    private void TickFeatureTests()
    {
        if (_featureTests.Count == 0) return;
        var now = Environment.TickCount64;
        var keep = Settings.SurgeSeconds * 1000L;
        foreach (var (name, t) in _featureTests.ToList())
        {
            if (t.Ramp is { } ramp && t.Sim != null && t.Thresholds != null)
            {
                // 이번 틱 동안 맞은 피해 = 그 시점의 받는 DPS × 경과 시간 → 실제 로그처럼 레드박싱 판정기에 넣는다.
                var dt = (now - t.LastFeed) / 1000.0;
                t.Sim.Add(now, Math.Max(0, ramp.ValueAt((now - t.RampStart) / 1000.0)) * dt);
                t.LastFeed = now;
                t.Sim.Update(now, t.Thresholds.SurgeMinDps, t.Thresholds.SurgeRatio);
                if (t.Sim.SurgeAt > t.SurgeAt) t.SurgeAt = t.Sim.SurgeAt;
            }
            if (t.Ramp is { } r && (now - t.RampStart) / 1000.0 >= r.TotalSeconds) t.Ramp = null;
            var surging = t.SurgeAt != long.MinValue && now - t.SurgeAt <= keep;
            if (t.Ramp == null && !surging) _featureTests.Remove(name);
        }
        foreach (var tile in _tiles.Values) ApplyCombat(tile);
    }
}
