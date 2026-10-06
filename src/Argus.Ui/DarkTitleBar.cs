using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Argus.Ui;

/// <summary>창 제목 표시줄을 다크로 바꾼다. 창을 만든 직후(표시 전)에 한 번 부른다.</summary>
public static class DarkTitleBar
{
    public static void Apply(Window w) => w.SourceInitialized += (_, _) =>
    {
        try { var on = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref on, sizeof(int)); }
        catch { }
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
