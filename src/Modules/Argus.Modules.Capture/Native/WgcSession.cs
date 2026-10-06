using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Argus.Modules.Capture.Native;

/// <summary>클라이언트 영역의 BGRA 프레임(stride = Width * 4).</summary>
internal sealed record Frame(byte[] Bgra, int Width, int Height);

/// <summary>
/// 창 하나에 대한 Windows Graphics Capture 세션. 다른 창에 가려져 있어도 캡처되며,
/// 완전히 최소화된 창은 새 프레임이 오지 않는다.
/// </summary>
internal sealed class WgcSession : IDisposable
{
    private static readonly object GpuLock = new();
    private static ID3D11Device? s_device;
    private static IDirect3DDevice? s_winrtDevice;

    private readonly nint _hwnd;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _lock = new();
    private ID3D11Texture2D? _staging;
    private Frame? _last;
    private Windows.Graphics.SizeInt32 _poolSize;

    public bool IsMinimized => NativeMethods.IsIconic(_hwnd);
    public bool IsAlive => NativeMethods.IsWindow(_hwnd);

    private WgcSession(nint hwnd, GraphicsCaptureItem item, Direct3D11CaptureFramePool pool, GraphicsCaptureSession session)
    {
        _hwnd = hwnd; _item = item; _pool = pool; _session = session;
        _poolSize = item.Size;
    }

    public static WgcSession? TryCreate(nint hwnd)
    {
        try
        {
            EnsureDevice();
            var item = CreateItemForWindow(hwnd);
            var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                s_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try { session.IsBorderRequired = false; } catch { /* 구형 Windows에서는 미지원 */ }
            session.StartCapture();
            return new WgcSession(hwnd, item, pool, session);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>가장 최근 프레임을 반환한다. 새 프레임이 없으면 직전 결과를 재사용한다.</summary>
    public Frame? Grab()
    {
        lock (_lock)
        {
            var latest = DrainLatest();

            // 세션을 막 만든 직후에는 첫 프레임이 비동기로 도착한다. 첫 프레임만 잠깐 기다린다.
            if (latest == null && _last == null)
            {
                var deadline = Environment.TickCount64 + FirstFrameTimeoutMs;
                while (latest == null && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(30);
                    latest = DrainLatest();
                }
            }
            if (latest == null) return _last;

            using (latest)
            {
                try { _last = Readback(latest) ?? _last; }
                catch { /* 디바이스 리셋 등 일시적 오류: 직전 프레임 유지 */ }
            }
            return _last;
        }
    }

    private const int FirstFrameTimeoutMs = 2000;

    private Direct3D11CaptureFrame? DrainLatest()
    {
        Direct3D11CaptureFrame? latest = null;
        while (_pool.TryGetNextFrame() is { } f)
        {
            latest?.Dispose();
            latest = f;
        }
        return latest;
    }

    private Frame? Readback(Direct3D11CaptureFrame frame)
    {
        var size = frame.ContentSize;
        if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
        {
            _poolSize = size;
            _pool.Recreate(s_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        }

        using var texture = GetTexture(frame.Surface);
        var desc = texture.Description;
        int texW = (int)desc.Width, texH = (int)desc.Height;

        // 창 전체(제목 표시줄 포함) 중 클라이언트 영역만 잘라낸다.
        var crop = NativeMethods.GetClientCropRect(_hwnd, texW, texH);
        int cw = crop.Width, ch = crop.Height;
        var bytes = new byte[cw * ch * 4];

        lock (GpuLock)
        {
            var ctx = s_device!.ImmediateContext;
            if (_staging == null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
            {
                _staging?.Dispose();
                var sd = desc;
                sd.Usage = ResourceUsage.Staging;
                sd.BindFlags = BindFlags.None;
                sd.CPUAccessFlags = CpuAccessFlags.Read;
                sd.MiscFlags = ResourceOptionFlags.None;
                _staging = s_device.CreateTexture2D(sd);
            }

            ctx.CopyResource(_staging, texture);
            var map = ctx.Map(_staging, 0, MapMode.Read);
            try
            {
                for (int y = 0; y < ch; y++)
                {
                    var src = map.DataPointer + (crop.Y + y) * (int)map.RowPitch + crop.X * 4;
                    Marshal.Copy(src, bytes, y * cw * 4, cw * 4);
                }
            }
            finally { ctx.Unmap(_staging, 0); }
        }
        return new Frame(bytes, cw, ch);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _session.Dispose();
            _pool.Dispose();
            _staging?.Dispose();
        }
    }

    // ---- 인터롭 ----

    private static void EnsureDevice()
    {
        lock (GpuLock)
        {
            if (s_device != null) return;
            var hr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                null, Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                null, out ID3D11Device? device);
            if (hr.Failure || device == null) throw new InvalidOperationException("D3D11 device creation failed");

            using var dxgi = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var winrt));
            s_winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(winrt);
            Marshal.Release(winrt);
            s_device = device;
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var iidInterop = typeof(IGraphicsCaptureItemInterop).GUID;
        Marshal.ThrowExceptionForHR(NativeMethods.WindowsCreateString(classId, classId.Length, out var hstr));
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.RoGetActivationFactory(hstr, ref iidInterop, out var factoryPtr));
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                var iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                var itemPtr = interop.CreateForWindow(hwnd, ref iidItem);
                try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.Release(factoryPtr); }
        }
        finally { NativeMethods.WindowsDeleteString(hstr); }
    }

    private static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iidAccess = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, in iidAccess, out var access));
            try
            {
                var iidTex = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C"); // ID3D11Texture2D
                nint tex;
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
                Marshal.ThrowExceptionForHR(getInterface(access, &iidTex, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally { Marshal.Release(access); }
        }
        finally { MarshalInterface<IDirect3DSurface>.DisposeAbi(surfacePtr); }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }
}
