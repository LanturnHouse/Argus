using System.Runtime.InteropServices;

namespace Argus.Modules.Preview;

internal static class KeyState
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    /// <summary>키보드 키(가상 키 코드) 또는 마우스 버튼이 지금 실제로 눌려 있는가.</summary>
    public static bool IsDown(bool mouse, int code)
    {
        var vk = !mouse ? code : (MouseButtonKind)code switch
        {
            MouseButtonKind.Middle => 0x04,
            MouseButtonKind.X1 => 0x05,
            MouseButtonKind.X2 => 0x06,
            _ => 0,
        };
        return vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public static Mods CurrentMods()
    {
        var m = Mods.None;
        if (IsDown(false, 0x11)) m |= Mods.Ctrl;
        if (IsDown(false, 0x12)) m |= Mods.Alt;
        if (IsDown(false, 0x10)) m |= Mods.Shift;
        if (IsDown(false, 0x5B) || IsDown(false, 0x5C)) m |= Mods.Win;
        return m;
    }
}

/// <summary>
/// 전역 키보드·마우스 저수준 훅. 전용 스레드에서 메시지 루프를 돌려서, UI 가 잠깐 멈춰도 시스템 전체의 입력이 끊기지 않게 한다.
/// 훅 콜백은 가벼워야 하므로 판단만 하고 실제 동작은 <see cref="HotkeyMatcher.Triggered"/> 쪽에서 다른 스레드로 넘긴다.
/// </summary>
internal sealed class InputHooks : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;
    private const int WM_QUIT = 0x12;

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)] private struct KbdHook { public uint VkCode, ScanCode, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHook { public int X, Y; public uint MouseData, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private readonly HotkeyMatcher _matcher;
    private readonly HookProc _kbdProc, _mouseProc;   // 델리게이트를 붙잡아 두지 않으면 GC 가 수거해서 훅이 죽는다
    private Thread? _thread;
    private uint _threadId;
    private nint _kbdHook, _mouseHook;

    public bool Installed { get; private set; }

    public InputHooks(HotkeyMatcher matcher)
    {
        _matcher = matcher;
        _kbdProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "Argus 단축키 훅" };
        _thread.Start();
        ready.Wait(3000);
    }

    private void Loop(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandle(null);
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        Installed = _kbdHook != 0 && _mouseHook != 0;
        ready.Set();
        while (GetMessage(out _, 0, 0, 0) > 0) { }
        if (_kbdHook != 0) UnhookWindowsHookEx(_kbdHook);
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
    }

    private nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                var kind = (int)wParam switch
                {
                    WM_KEYDOWN or WM_SYSKEYDOWN => InputKind.KeyDown,
                    WM_KEYUP or WM_SYSKEYUP => InputKind.KeyUp,
                    _ => (InputKind?)null,
                };
                if (kind is { } k)
                {
                    var data = Marshal.PtrToStructure<KbdHook>(lParam);
                    if (_matcher.Handle(new InputEvent(k, (int)data.VkCode, KeyState.CurrentMods()))) return 1;
                }
            }
            catch { /* 예외는 삼키고 입력은 그대로 통과시킨다 (훅이 죽으면 시스템 입력이 막힌다) */ }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            try
            {
                var msg = (int)wParam;
                if (msg is WM_MBUTTONDOWN or WM_MBUTTONUP or WM_XBUTTONDOWN or WM_XBUTTONUP)   // 이동 같은 잦은 메시지는 바로 통과
                {
                    var data = Marshal.PtrToStructure<MouseHook>(lParam);
                    var down = msg is WM_MBUTTONDOWN or WM_XBUTTONDOWN;
                    MouseButtonKind? button = msg is WM_MBUTTONDOWN or WM_MBUTTONUP
                        ? MouseButtonKind.Middle
                        : (data.MouseData >> 16) == 1 ? MouseButtonKind.X1 : (data.MouseData >> 16) == 2 ? MouseButtonKind.X2 : null;
                    if (button is { } b &&
                        _matcher.Handle(new InputEvent(down ? InputKind.MouseDown : InputKind.MouseUp, (int)b, KeyState.CurrentMods()))) return 1;
                }
            }
            catch { }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread?.Join(1000);
        _threadId = 0;
    }
}
