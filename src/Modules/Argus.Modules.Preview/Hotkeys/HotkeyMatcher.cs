namespace Argus.Modules.Preview;

internal enum InputKind { KeyDown, KeyUp, MouseDown, MouseUp }

/// <summary>훅이 받은 입력 하나. Code 는 키보드면 가상 키 코드, 마우스면 <see cref="MouseButtonKind"/>.</summary>
internal readonly record struct InputEvent(InputKind Kind, int Code, Mods Mods)
{
    public bool IsMouse => Kind is InputKind.MouseDown or InputKind.MouseUp;
    public bool IsDown => Kind is InputKind.KeyDown or InputKind.MouseDown;
}

/// <summary>
/// 입력이 단축키와 맞는지 판단한다. 화면·훅과 무관해서 따로 시험할 수 있다.
/// 맞으면 그 입력을 삼키고(true) 동작 이름을 알린다. 누름을 삼켰으면 짝이 되는 뗌도 삼킨다 (브라우저 '뒤로' 같은 것이 뗄 때 실행되지 않게).
/// </summary>
internal sealed class HotkeyMatcher
{
    private static readonly HashSet<int> ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
    private const int VkEscape = 0x1B;

    private readonly HashSet<(bool Mouse, int Code)> _held = [];
    private readonly Func<bool, int, bool> _isPhysicallyDown;

    public HotkeyMatcher(Func<bool, int, bool>? isPhysicallyDown = null) => _isPhysicallyDown = isPhysicallyDown ?? KeyState.IsDown;

    public volatile bool Enabled = true;

    /// <summary>지정한 것보다 보조키가 더 눌려 있어도 동작한다 (더 많이 맞는 단축키가 우선).</summary>
    public volatile bool AllowExtraMods = true;
    public volatile HotkeyBinding[] Bindings = [];

    /// <summary>단축키가 맞고 범위(EVE/Argus 가 앞) 조건도 맞는지. 맞을 때만 호출된다.</summary>
    public Func<bool> InScope = () => true;

    /// <summary>지정 모드 입력을 받아도 되는지 (Argus 가 앞일 때만 true). 아니면 입력을 삼키지 않고 평소 단축키로 처리한다.</summary>
    public Func<bool> CaptureScope = () => true;

    /// <summary>동작이 발생했을 때 (훅 스레드에서 호출되므로 빨리 반환해야 한다).</summary>
    public Action<string>? Triggered;

    /// <summary>지정 모드: 다음 입력을 단축키로 받는다. null 은 Esc(취소). 반환값 true 면 지정 종료, false 면 계속 기다린다.</summary>
    public volatile Func<HotkeyTrigger?, bool>? Capture;

    /// <summary>입력을 처리한다. true 면 이 입력을 다른 프로그램에 전달하지 않는다.</summary>
    public bool Handle(InputEvent e)
    {
        var key = (e.IsMouse, e.Code);
        if (!e.IsDown) return _held.Remove(key);

        if (_held.Contains(key))
        {
            if (_isPhysicallyDown(e.IsMouse, e.Code)) return true;   // 키 반복: 계속 삼킨다
            _held.Remove(key);                                       // 뗌을 놓친 찌꺼기: 새 누름으로 본다
        }

        if (!e.IsMouse && ModifierKeys.Contains(e.Code)) return false;

        var cap = Capture;
        if (cap != null && CaptureScope())
        {
            HotkeyTrigger? t = !e.IsMouse && e.Code == VkEscape && e.Mods == Mods.None
                ? null
                : new HotkeyTrigger { Key = e.IsMouse ? 0 : e.Code, Mouse = e.IsMouse ? (MouseButtonKind)e.Code : MouseButtonKind.None, Mods = e.Mods };
            if (cap(t) && Capture == cap) Capture = null;
            _held.Add(key);
            return true;
        }

        if (!Enabled) return false;
        var probe = new HotkeyTrigger { Key = e.IsMouse ? 0 : e.Code, Mouse = e.IsMouse ? (MouseButtonKind)e.Code : MouseButtonKind.None, Mods = e.Mods };
        HotkeyBinding? best = null;
        var bestCount = -1;
        foreach (var b in Bindings)
        {
            var t = b.Trigger;
            if (t.IsEmpty || !t.SameBase(probe)) continue;
            if (AllowExtraMods ? (t.Mods & probe.Mods) != t.Mods : t.Mods != probe.Mods) continue;
            var count = HotkeyTrigger.ModCount(t.Mods);
            if (count > bestCount) { best = b; bestCount = count; }   // 보조키를 더 많이 요구하는(더 구체적인) 쪽이 우선. 완전히 같으면 먼저 등록된 쪽.
        }
        if (best != null)
        {
            if (!InScope()) return false;
            _held.Add(key);
            Triggered?.Invoke(best.Action);
            return true;
        }
        return false;
    }
}
