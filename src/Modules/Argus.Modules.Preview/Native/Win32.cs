using System.Runtime.InteropServices;

namespace Argus.Modules.Preview.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Rect32
{
    public int Left, Top, Right, Bottom;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public Rect32(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public static Rect32 FromSize(int x, int y, int w, int h) => new(x, y, x + w, y + h);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point32 { public int X, Y; }

internal static class Win32
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4;
    public static readonly nint HWND_TOPMOST = -1;

    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint h, out Rect32 r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint h, out Rect32 r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point32 p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    /// <summary>Ctrl 키가 지금 눌려 있는가 (포커스가 없는 창에서도 실제 키 상태를 본다).</summary>
    public static bool CtrlDown => (GetAsyncKeyState(0x11) & 0x8000) != 0;
    public static bool ShiftDown => (GetAsyncKeyState(0x10) & 0x8000) != 0;
    [DllImport("user32.dll")] public static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);

    public static void AddExStyle(nint hwnd, long style) =>
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(GetWindowLongPtr(hwnd, GWL_EXSTYLE) | style));

    // ---- 모니터 (스냅, 화면 밖 보정용) ----
    private delegate bool MonitorEnumProc(nint hMon, nint hdc, ref Rect32 rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint data);

    /// <summary>모든 모니터의 영역 (가상 화면 물리 픽셀 좌표).</summary>
    public static List<Rect32> Monitors()
    {
        var list = new List<Rect32>();
        EnumDisplayMonitors(0, 0, (nint _, nint _, ref Rect32 r, nint _) => { list.Add(r); return true; }, 0);
        // 주 모니터(가상 화면 원점 0,0 을 포함하는 모니터)를 맨 앞으로 — 새 프리뷰는 주 모니터에 먼저 놓는다.
        var primary = list.FindIndex(m => m.Left <= 0 && m.Top <= 0 && m.Right > 0 && m.Bottom > 0);
        if (primary > 0) { var p = list[primary]; list.RemoveAt(primary); list.Insert(0, p); }
        return list;
    }
}

/// <summary>DWM 썸네일: 다른 창의 실시간 축소 화면을 우리 창 안에 그린다. (EVE-O Preview 와 같은 방식)</summary>
internal static class Dwm
{
    private const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public int Flags;
        public Rect32 Destination;
        public Rect32 Source;
        public byte Opacity;
        public bool Visible;
        public bool SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumb, ref DwmThumbnailProperties props);

    public static nint Register(nint destHwnd, nint srcHwnd) =>
        DwmRegisterThumbnail(destHwnd, srcHwnd, out var t) == 0 ? t : 0;

    public static void Unregister(nint thumb)
    {
        if (thumb != 0) DwmUnregisterThumbnail(thumb);
    }

    /// <summary>썸네일을 대상 창 클라이언트 영역 (0,0,w,h) 전체에 채운다. 원본은 제목 표시줄을 뺀 클라이언트 영역만 쓴다.</summary>
    public static void Update(nint thumb, int w, int h, double opacity, bool visible)
    {
        if (thumb == 0) return;
        var p = new DwmThumbnailProperties
        {
            Flags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
            Destination = new Rect32(0, 0, w, h),
            Opacity = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255),
            Visible = visible,
            SourceClientAreaOnly = true,
        };
        DwmUpdateThumbnailProperties(thumb, ref p);
    }
}
