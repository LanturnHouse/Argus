using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Argus.Core.Clients;

/// <summary>다른 프로그램의 창을 앞으로 가져온다 (클라이언트 전환용). 입력을 대신 보내지는 않는다.</summary>
public static class WindowFocus
{
    private const int SW_RESTORE = 9;

    /// <summary>최소화되어 있으면 복원하고, 창을 전면으로 가져온다. 성공하면 true.</summary>
    public static bool Activate(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        // Windows 는 전면 창이 아닌 프로세스의 SetForegroundWindow 를 막는다.
        // 현재 전면 창의 입력 스레드에 잠시 붙어서 전환 권한을 얻는다.
        var fg = GetForegroundWindow();
        var fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
        var current = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != current && AttachThreadInput(current, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(current, fgThread, false);
        }

        return WaitForeground(hwnd);
    }

    /// <summary>전환은 비동기로 반영된다. 결과를 판정하기 전에 잠깐(최대 150ms) 기다린다.</summary>
    private static bool WaitForeground(nint hwnd)
    {
        for (var i = 0; i < 15; i++)
        {
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(10);
        }
        return GetForegroundWindow() == hwnd;
    }

    public static nint Foreground => GetForegroundWindow();

    /// <summary>이 창이 EVE 클라이언트(exefile) 프로세스의 창인가. 로그인·캐릭터 선택 창처럼 아직 목록에 없는 창도 판별한다.</summary>
    public static bool IsEveWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("exefile", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
