using System.Windows;
using System.Windows.Threading;
using Argus.Core.Clients;
using Argus.Core.Modules;

namespace Argus.Modules.Preview;

public sealed record HotkeyWarning(string Message, string[] Actions);

/// <summary>
/// 프리뷰 프리셋의 단축키를 실제 키보드·마우스 입력에 연결한다: 사이클 다음/이전, 클라이언트별 바로 이동.
/// 단축키 자체는 활성 프리셋에 저장되어 있고(<see cref="PreviewService"/>), 프리셋을 바꾸면 그 프리셋의 단축키가 적용된다.
/// 창을 앞으로 가져올 뿐 다른 창에 입력을 보내지는 않는다.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int CaptureTimeoutMs = 10_000;

    private readonly IModuleContext _ctx;
    private readonly PreviewService _preview;
    private readonly Dispatcher _ui;
    private readonly HotkeyMatcher _matcher = new();
    private readonly InputHooks _hooks;
    private readonly object _switchLock = new();
    private readonly DispatcherTimer _captureTimeout = new() { Interval = TimeSpan.FromMilliseconds(CaptureTimeoutMs) };
    private volatile (string Character, long At)[] _surgeOrder = [];   // 레드박싱 전환 대상 (먼저 감지된 순)
    private volatile string[] _cycleOrder = [];   // 훅 스레드에서 읽으므로 UI 스레드가 만든 스냅샷을 쓴다
    private Action? _captureDone;

    public bool HooksInstalled => _hooks.Installed;

    public HotkeyService(IModuleContext ctx, PreviewService preview)
    {
        _ctx = ctx;
        _preview = preview;
        _ui = Application.Current.Dispatcher;
        _matcher.InScope = InScope;
        _matcher.Triggered = action => _ = Task.Run(() => Perform(action));   // 훅 스레드는 바로 돌려보낸다
        _hooks = new InputHooks(_matcher);
        _captureTimeout.Tick += (_, _) => CancelCapture();
    }

    public void Start()
    {
        _preview.Changed += Publish;
        Publish();
        _hooks.Start();
    }

    // ---------- 범위: EVE 클라이언트나 Argus 가 맨 앞일 때만 ----------

    private bool InScope()
    {
        if (!_preview.Settings.HotkeysOnlyWhenEveActive) return true;
        var fg = WindowFocus.Foreground;
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return true;
        return _ctx.Clients.Current.Any(c => c.Hwnd == fg);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    // ---------- 활성 프리셋 → 훅에 반영 ----------

    /// <summary>지금 적용되는 단축키 목록 (활성 프리셋, 실행 중인 클라이언트만).</summary>
    /// <param name="includeSurge">true 면 레드박싱 전환 키를 지금 동작 여부와 상관없이 넣는다 (겹침 경고용). false 면 레드박싱 후 일정 시간에만 넣는다 — 평소에는 그 키를 가로채지 않는다.</param>
    internal List<HotkeyBinding> CurrentBindings(bool includeSurge = false)
    {
        var preset = _preview.ActivePreset;
        var list = new List<HotkeyBinding>();
        if (preset.CycleNext is { IsEmpty: false } n) list.Add(new HotkeyBinding { Action = HotkeyActions.Next, Trigger = n.Clone() });
        if (preset.CyclePrev is { IsEmpty: false } p) list.Add(new HotkeyBinding { Action = HotkeyActions.Prev, Trigger = p.Clone() });
        foreach (var c in _preview.Clients())
            if (c.Layout.Hotkey is { IsEmpty: false } h) list.Add(new HotkeyBinding { Action = HotkeyActions.Jump(c.Character), Trigger = h.Clone() });
        if (preset.SurgeHotkey is { IsEmpty: false } sg && (includeSurge || _preview.SurgeOrder().Count > 0))
            list.Add(new HotkeyBinding { Action = HotkeyActions.Surge, Trigger = sg.Clone() });
        return list;
    }

    private void Publish()
    {
        _matcher.Enabled = _preview.Settings.HotkeysEnabled;
        _matcher.AllowExtraMods = _preview.Settings.HotkeysAllowExtraMods;
        _matcher.Bindings = [.. CurrentBindings()];
        _cycleOrder = [.. _preview.Clients().Where(c => c.Layout.InCycle).Select(c => c.Character)];
        _surgeOrder = [.. _preview.SurgeOrder()];
    }

    // ---------- 전환 ----------

    private EveClient? Running(string character) =>
        _ctx.Clients.Current.FirstOrDefault(c => string.Equals(c.Character, character, StringComparison.OrdinalIgnoreCase));

    internal void Perform(string action)
    {
        // 빠르게 연속으로 눌러도 앞의 전환이 끝난 뒤 그 결과를 기준으로 다음 대상을 고른다.
        lock (_switchLock)
        {
            var target = ResolveTarget(action, WindowFocus.Foreground);
            if (target == null) return;
            // 레드박싱 전환 단축키로 그 클라이언트로 옮겼으면 그 레드박싱 이벤트는 설정한 시간이 남아 있어도 끝낸다.
            if (_ctx.Clients.Activate(target) && action == HotkeyActions.Surge)
                _ui.BeginInvoke(() => _preview.DismissSurge(target.Character));
        }
    }

    internal EveClient? ResolveTarget(string action, nint foreground)
    {
        if (action.StartsWith(HotkeyActions.JumpPrefix))
            return Running(action[HotkeyActions.JumpPrefix.Length..]);

        if (action == HotkeyActions.Surge) return ResolveSurgeTarget(foreground);

        var next = action == HotkeyActions.Next;
        if (!next && action != HotkeyActions.Prev) return null;

        // 목록 순서대로, 사이클에 포함되어 있고 지금 실행 중인 클라이언트
        var list = _cycleOrder.Select(Running).OfType<EveClient>().DistinctBy(c => c.Hwnd).ToList();
        if (list.Count == 0) return null;
        int cur = list.FindIndex(c => c.Hwnd == foreground);
        if (cur < 0) return next ? list[0] : list[^1];      // 사이클 밖(다른 클라이언트나 Argus)에서 누르면 처음/마지막부터
        if (list.Count == 1) return null;
        return list[(cur + (next ? 1 : -1) + list.Count) % list.Count];
    }

    /// <summary>
    /// 레드박싱 전환: 지금 레드박싱이 살아 있는(단축키 동작 시간 안인) 클라이언트를 먼저 감지된 순서로 돌린다.
    /// 앞에 있는 창이 그 목록 밖이면 가장 먼저 감지된 쪽으로, 목록 안이면 그다음 순서로 간다.
    /// </summary>
    internal EveClient? ResolveSurgeTarget(nint foreground)
    {
        var window = _preview.Settings.SurgeSeconds * 1000L;
        var now = Environment.TickCount64;
        var list = _surgeOrder.Where(x => now - x.At <= window).Select(x => Running(x.Character)).OfType<EveClient>().DistinctBy(c => c.Hwnd).ToList();
        if (list.Count == 0) return null;
        var cur = list.FindIndex(c => c.Hwnd == foreground);
        if (cur < 0) return list[0];
        return list.Count == 1 ? null : list[(cur + 1) % list.Count];
    }

    // ---------- 겹침 경고 ----------

    public string ActionLabel(string action)
    {
        if (action == HotkeyActions.Next) return "사이클 다음";
        if (action == HotkeyActions.Prev) return "사이클 이전";
        if (action == HotkeyActions.Surge) return "레드박싱 전환";
        return $"{action[HotkeyActions.JumpPrefix.Length..]} 바로 이동";
    }

    /// <summary>같은 단축키를 둘 이상이 쓰거나, 보조키 포함 관계로 겹치거나, EVE 기본 키와 겹칠 때 (활성 프리셋 기준).</summary>
    public List<HotkeyWarning> Warnings()
    {
        var result = new List<HotkeyWarning>();
        var bs = CurrentBindings(includeSurge: true);
        var allowExtra = _preview.Settings.HotkeysAllowExtraMods;
        for (int i = 0; i < bs.Count; i++)
            for (int j = i + 1; j < bs.Count; j++)
            {
                HotkeyBinding a = bs[i], b = bs[j];
                if (a.Trigger.Same(b.Trigger))
                    result.Add(new($"같은 단축키 [{a.Trigger}] 를 두 동작이 함께 쓰고 있습니다: '{ActionLabel(a.Action)}', '{ActionLabel(b.Action)}'. 먼저 지정한 쪽만 동작합니다.", [a.Action, b.Action]));
                else if (allowExtra && a.Trigger.SameBase(b.Trigger))
                {
                    HotkeyBinding? lo = (a.Trigger.Mods & b.Trigger.Mods) == a.Trigger.Mods ? a : (b.Trigger.Mods & a.Trigger.Mods) == b.Trigger.Mods ? b : null;
                    if (lo == null) continue;
                    var hi = lo == a ? b : a;
                    result.Add(new($"[{hi.Trigger}] ('{ActionLabel(hi.Action)}') 는 [{lo.Trigger}] ('{ActionLabel(lo.Action)}') 에 보조키가 더해진 조합입니다. 보조키를 누른 채로도 동작하는 옵션이 켜져 있어서, {lo.Trigger} 를 Ctrl 등과 함께 누르면 '{ActionLabel(hi.Action)}' 쪽이 우선 동작합니다.", [lo.Action, hi.Action]));
                }
            }
        foreach (var b in bs.Where(b => !b.Trigger.IsMouse && b.Trigger.Mods == Mods.None && b.Trigger.Key is >= 0x70 and <= 0x77))
            result.Add(new($"[{b.Trigger}] ('{ActionLabel(b.Action)}') 는 EVE 의 기본 모듈 단축키(F1~F8)와 겹칩니다. EVE 가 앞에 있을 때 이 키는 전환에 쓰이고 EVE 로는 전달되지 않습니다.", [b.Action]));
        return result;
    }

    // ---------- 단축키 지정 (다음 입력을 받는다) ----------

    private static readonly HashSet<int> BareKeysAllowed = [.. Enumerable.Range(0x70, 24), .. Enumerable.Range(0x60, 16), 0x13, 0x91];   // F1~F24, 숫자패드, Pause, ScrollLock

    /// <summary>다음에 누른 키/마우스 버튼을 <paramref name="apply"/> 로 넘긴다. 끝나면(지정·Esc·시간 초과) done 이 호출된다.</summary>
    public void BeginCapture(Action<HotkeyTrigger> apply, Action<string> hint, Action done)
    {
        EndCapture();
        _captureDone = done;
        _captureTimeout.Stop(); _captureTimeout.Start();
        _matcher.Capture = t =>
        {
            if (t == null) { _ui.BeginInvoke(EndCapture); return true; }   // Esc
            if (!t.IsMouse && t.Mods == Mods.None && !BareKeysAllowed.Contains(t.Key))
            {
                _ui.BeginInvoke(() => hint("글자·숫자 같은 일반 키는 채팅을 막으므로 Ctrl/Alt/Shift 와 함께 누르세요. (F1~F24, 숫자패드는 그대로 가능)"));
                return false;
            }
            _ui.BeginInvoke(() => { apply(t); EndCapture(); });
            return true;
        };
    }

    public void CancelCapture() => EndCapture();

    private void EndCapture()
    {
        _captureTimeout.Stop();
        _matcher.Capture = null;
        var d = _captureDone; _captureDone = null;
        d?.Invoke();
    }

    public void Dispose()
    {
        _preview.Changed -= Publish;
        _captureTimeout.Stop();
        _hooks.Dispose();
    }
}
