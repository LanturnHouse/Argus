using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Argus.Core.Clients;
using Argus.Modules.Preview.Native;

namespace Argus.Modules.Preview;

/// <summary>
/// 클라이언트 하나의 프리뷰. 창 두 개로 이루어진다.
/// 1) 호스트 창: DWM 썸네일(실시간 축소 화면)을 그리고 마우스를 받는다.
/// 2) 오버레이 창: 호스트 위에 겹치는 투명·클릭 통과 창. HUD(이름, 활성 테두리 등)를 그린다.
///    DWM 썸네일은 창 내용 위에 합성되므로 HUD 는 별도 창이어야 보인다.
/// 좌표는 전부 가상 화면의 물리 픽셀이다 (DPI 가 달라도 저장한 위치가 어긋나지 않게).
/// </summary>
internal sealed class PreviewTile : IDisposable
{
    private const int WM_DPICHANGED = 0x02E0;

    private readonly PreviewService _svc;
    private readonly Window _host = new();
    private readonly HudView _hud = new();
    private Window? _overlay;
    private nint _hostHwnd, _overlayHwnd, _thumb;
    private Rect32 _bounds;
    private bool _wantVisible;
    private bool _isActive;

    // 드래그(편집 모드) 상태
    private DragZone _zone;
    private bool _dragging;
    private Point32 _dragCursor;
    private Rect32 _dragStart;
    private List<Rect32> _dragOthers = [];
    private List<Rect32> _dragMonitors = [];
    private List<(PreviewTile Tile, Rect32 Start)> _dragGroup = [];   // Ctrl+크기 조절 때 함께 조절되는 다른 프리뷰
    private bool _groupResized;

    public EveClient Client { get; private set; }
    public double Aspect { get; private set; } = 16.0 / 9;
    public bool IsActive => _isActive;
    public nint HostHwnd => _hostHwnd;
    public bool IsShown => _host.IsVisible;
    public Rect32 Bounds => _bounds;

    public PreviewTile(PreviewService svc, EveClient client)
    {
        _svc = svc;
        Client = client;
        _hud.SetName(client.Character);

        _host.WindowStyle = WindowStyle.None;
        _host.ResizeMode = ResizeMode.NoResize;
        _host.ShowInTaskbar = false;
        _host.ShowActivated = false;
        _host.Topmost = true;
        _host.Background = Brushes.Black;
        _host.Title = $"Argus 프리뷰 - {client.Character}";
        _host.Width = 210; _host.Height = 118;
        _host.Content = new Grid { Background = Brushes.Transparent }; // 투명 배경도 마우스 입력은 받는다
        _host.SourceInitialized += (_, _) => OnHostInitialized();
        _host.MouseLeftButtonDown += OnMouseDown;
        _host.MouseMove += OnMouseMove;
        _host.MouseLeftButtonUp += OnMouseUp;
        _host.MouseRightButtonUp += (_, _) => { };
    }

    private void OnHostInitialized()
    {
        _hostHwnd = new WindowInteropHelper(_host).Handle;
        Win32.AddExStyle(_hostHwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE); // Alt+Tab 에 안 뜨고, 클릭해도 포커스를 뺏지 않는다
        HwndSource.FromHwnd(_hostHwnd)?.AddHook(WndProc);
        _thumb = Dwm.Register(_hostHwnd, Client.Hwnd);
    }

    /// <summary>모니터 DPI 가 바뀔 때 WPF 가 창 크기를 자동으로 바꾸는 것을 막고, 저장한 물리 픽셀 크기를 그대로 유지한다.</summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_DPICHANGED)
        {
            handled = true;
            SetBounds(_bounds);
        }
        return 0;
    }

    // ---------- 표시 ----------

    /// <summary>창을 만들고(처음 한 번) 지정한 위치에 보여준다.</summary>
    public void Show(Rect32 bounds)
    {
        _wantVisible = true;
        _bounds = bounds;
        if (!_host.IsVisible) _host.Show();
        EnsureOverlay();
        if (_overlay is { IsVisible: false }) _overlay.Show();
        SetBounds(bounds);
    }

    public void Hide()
    {
        _wantVisible = false;
        if (_host.IsVisible) _host.Hide();
        if (_overlay is { IsVisible: true }) _overlay.Hide();
    }

    public void SetVisible(bool visible, Rect32 bounds)
    {
        if (visible) Show(bounds);
        else { _bounds = bounds; Hide(); }
    }

    private void EnsureOverlay()
    {
        if (_overlay != null) return;
        _overlay = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Owner = _host, Content = _hud, Width = 210, Height = 118, IsHitTestVisible = false,
        };
        _overlay.SourceInitialized += (_, _) =>
        {
            _overlayHwnd = new WindowInteropHelper(_overlay).Handle;
            // 클릭 통과: 마우스는 아래의 호스트 창이 받는다.
            Win32.AddExStyle(_overlayHwnd, Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(_overlayHwnd)?.AddHook(WndProc);
        };
        _hud.SetEdit(_svc.EditMode);
        _hud.SetActive(_isActive);
    }

    public void SetBounds(Rect32 b)
    {
        _bounds = b;
        if (_hostHwnd == 0) return;
        Win32.SetWindowPos(_hostHwnd, Win32.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Win32.SWP_NOACTIVATE);
        if (_overlayHwnd != 0)
            Win32.SetWindowPos(_overlayHwnd, Win32.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Win32.SWP_NOACTIVATE);
        _hud.SetAspect(b.Width / (double)Math.Max(1, b.Height));
        UpdateThumbnail();
    }

    private void UpdateThumbnail()
    {
        if (_hostHwnd == 0 || _thumb == 0) return;
        Win32.GetClientRect(_hostHwnd, out var c);
        Dwm.Update(_thumb, c.Width, c.Height, _svc.Settings.Opacity, true);
    }

    public void ApplyOpacity() => UpdateThumbnail();

    // ---------- 상태 ----------

    public void SetActive(bool active)
    {
        _isActive = active;
        _hud.SetActive(active);
    }

    public void SetCombat(Argus.Core.Events.CombatSnapshot? snapshot, HudFlags flags) => _hud.SetCombat(snapshot, flags);

    public void SetSurge(bool tint, int flashMs, string? keyHint) => _hud.SetSurge(tint, flashMs, keyHint);
    internal HudView Hud => _hud;

    public void SetEditMode(bool edit)
    {
        _hud.SetEdit(edit);
        _host.Cursor = Cursors.Arrow;
    }

    /// <summary>클라이언트 창이 바뀌었으면(재접속) 썸네일을 새 창에 다시 연결한다.</summary>
    public void SetClient(EveClient client)
    {
        if (client.Hwnd == Client.Hwnd) { Client = client; return; }
        Client = client;
        if (_hostHwnd == 0) return;
        Dwm.Unregister(_thumb);
        _thumb = Dwm.Register(_hostHwnd, client.Hwnd);
        UpdateThumbnail();
    }

    /// <summary>원본 창의 최소화 여부와 화면 비율을 확인한다. 비율이 바뀌면 타일 높이를 맞추고 true 를 반환한다.</summary>
    public bool RefreshSource()
    {
        var minimized = Win32.IsIconic(Client.Hwnd);
        _hud.SetMinimized(minimized);
        if (minimized || _dragging) return false;

        if (!Win32.GetClientRect(Client.Hwnd, out var c) || c.Width <= 0 || c.Height <= 0) return false;
        var aspect = c.Width / (double)c.Height;
        var changed = Math.Abs(aspect - Aspect) > 0.01;
        Aspect = aspect;
        if (changed && _wantVisible && Math.Abs(_bounds.Width / (double)_bounds.Height - aspect) > 0.02)
        {
            SetBounds(Rect32.FromSize(_bounds.Left, _bounds.Top, _bounds.Width, (int)Math.Round(_bounds.Width / aspect)));
            return true;
        }
        return false;
    }

    /// <summary>원본 창의 비율을 알기 전에 쓰는 초기값을 실제 값으로 정한다.</summary>
    public void InitAspect()
    {
        if (Win32.GetClientRect(Client.Hwnd, out var c) && c.Width > 0 && c.Height > 0)
            Aspect = c.Width / (double)c.Height;
    }

    // ---------- 마우스 ----------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_svc.EditMode) return; // 편집 모드가 아니면 마우스를 놓을 때 창 전환
        if (Win32.ShiftDown) { _svc.ResetSize(this); e.Handled = true; return; }   // Shift + 클릭: 이 프리뷰를 기본 크기로
        Win32.GetCursorPos(out _dragCursor);
        Win32.GetWindowRect(_hostHwnd, out _dragStart);
        _zone = TileGeometry.HitTest(_dragCursor.X - _dragStart.Left, _dragCursor.Y - _dragStart.Top, _dragStart.Width, _dragStart.Height);
        _dragOthers = _svc.OtherBounds(this);
        _dragMonitors = Win32.Monitors();
        _dragGroup = [.. _svc.ScalableTiles(this).Select(t => (t, t.Bounds))];
        _groupResized = false;
        _dragging = true;
        _host.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Win32.GetCursorPos(out var cur);
        if (!_dragging)
        {
            if (_svc.EditMode)
            {
                Win32.GetWindowRect(_hostHwnd, out var r);
                _host.Cursor = CursorFor(TileGeometry.HitTest(cur.X - r.Left, cur.Y - r.Top, r.Width, r.Height));
            }
            return;
        }

        int dx = cur.X - _dragCursor.X, dy = cur.Y - _dragCursor.Y;
        Rect32 next;
        if (_zone == DragZone.Move)
        {
            var moved = Rect32.FromSize(_dragStart.Left + dx, _dragStart.Top + dy, _dragStart.Width, _dragStart.Height);
            var (sx, sy) = TileGeometry.SnapMove(moved, _dragOthers, _dragMonitors);
            next = Rect32.FromSize(sx, sy, moved.Width, moved.Height);
        }
        else
        {
            next = TileGeometry.Resize(_dragStart, _zone, dx, dy, Aspect);
            ResizeGroup(next.Width / (double)Math.Max(1, _dragStart.Width), Win32.CtrlDown);
        }
        SetBounds(next);
    }

    /// <summary>Ctrl 을 누른 채 크기를 조절하면 표시 중인 다른 프리뷰도 같은 비율로 조절한다 (왼쪽 위 모서리 고정). Ctrl 을 떼면 원래 크기로 돌아간다.</summary>
    private void ResizeGroup(double ratio, bool ctrl)
    {
        if (!ctrl && !_groupResized) return;
        foreach (var (tile, start) in _dragGroup)
        {
            if (!ctrl) { tile.SetBounds(start); continue; }
            var w = Math.Clamp((int)Math.Round(start.Width * ratio), TileGeometry.MinWidth, TileGeometry.MaxWidth);
            tile.SetBounds(Rect32.FromSize(start.Left, start.Top, w, (int)Math.Round(w / tile.Aspect)));
        }
        _groupResized = ctrl;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            _host.ReleaseMouseCapture();
            _svc.SaveBounds(this);
            if (_groupResized) foreach (var (tile, _) in _dragGroup) _svc.SaveBounds(tile);
            _dragGroup = [];
            _groupResized = false;
            return;
        }
        if (!_svc.EditMode) _svc.ActivateClient(Client); // 클릭 = 그 클라이언트로 전환
    }

    private static Cursor CursorFor(DragZone z) => z switch
    {
        DragZone.Left or DragZone.Right => Cursors.SizeWE,
        DragZone.Top or DragZone.Bottom => Cursors.SizeNS,
        DragZone.TopLeft or DragZone.BottomRight => Cursors.SizeNWSE,
        DragZone.TopRight or DragZone.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.SizeAll,
    };

    public void Dispose()
    {
        Dwm.Unregister(_thumb);
        _thumb = 0;
        try { _overlay?.Close(); } catch { /* 이미 닫힘 */ }
        try { _host.Close(); } catch { /* 이미 닫힘 */ }
    }
}
