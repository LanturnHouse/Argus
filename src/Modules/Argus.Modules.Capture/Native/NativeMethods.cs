using System.Runtime.InteropServices;

namespace Argus.Modules.Capture.Native;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT pt);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out RECT rect, int size);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    internal static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int RoGetActivationFactory(nint classId, ref Guid iid, out nint factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int WindowsCreateString(string src, int length, out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int WindowsDeleteString(nint hstring);

    internal readonly record struct CropRect(int X, int Y, int Width, int Height);

    /// <summary>캡처 텍스처(창 프레임 기준) 안에서 클라이언트 영역의 위치를 계산한다. 실패하면 전체를 반환.</summary>
    internal static CropRect GetClientCropRect(nint hwnd, int texW, int texH)
    {
        var full = new CropRect(0, 0, texW, texH);
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        if (!GetClientRect(hwnd, out var client)) return full;
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin)) return full;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) != 0) return full;

        int x = origin.X - frame.Left, y = origin.Y - frame.Top;
        int w = client.Right - client.Left, h = client.Bottom - client.Top;
        if (x < 0 || y < 0 || w <= 0 || h <= 0) return full;
        w = Math.Min(w, texW - x);
        h = Math.Min(h, texH - y);
        return w <= 0 || h <= 0 ? full : new CropRect(x, y, w, h);
    }
}
