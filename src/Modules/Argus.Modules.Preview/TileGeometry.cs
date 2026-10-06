using Argus.Modules.Preview.Native;

namespace Argus.Modules.Preview;

public enum DragZone { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>프리뷰 타일의 이동·크기 조절·자석 붙이기 계산. 화면과 무관한 순수 함수라 따로 검증할 수 있다.</summary>
internal static class TileGeometry
{
    public const int EdgeSize = 8;      // 가장자리 잡기 영역 (물리 px)
    public const int SnapDistance = 10; // 자석 거리
    public const int MinWidth = 120;
    public const int MaxWidth = 1600;

    /// <summary>창 안의 좌표(물리 px)가 어느 영역인지. 편집 모드에서 커서와 드래그 방식을 정한다.</summary>
    public static DragZone HitTest(int x, int y, int w, int h)
    {
        var e = Math.Min(EdgeSize, Math.Min(w, h) / 3);
        bool l = x < e, r = x >= w - e, t = y < e, b = y >= h - e;
        return (l, r, t, b) switch
        {
            (true, _, true, _) => DragZone.TopLeft,
            (_, true, true, _) => DragZone.TopRight,
            (true, _, _, true) => DragZone.BottomLeft,
            (_, true, _, true) => DragZone.BottomRight,
            (true, _, _, _) => DragZone.Left,
            (_, true, _, _) => DragZone.Right,
            (_, _, true, _) => DragZone.Top,
            (_, _, _, true) => DragZone.Bottom,
            _ => DragZone.Move,
        };
    }

    /// <summary>비율(aspect = 가로/세로)을 유지하며 크기를 바꾼다. 잡은 쪽의 반대편 모서리는 고정된다.</summary>
    public static Rect32 Resize(Rect32 start, DragZone zone, int dx, int dy, double aspect)
    {
        bool left = zone is DragZone.Left or DragZone.TopLeft or DragZone.BottomLeft;
        bool right = zone is DragZone.Right or DragZone.TopRight or DragZone.BottomRight;
        bool top = zone is DragZone.Top or DragZone.TopLeft or DragZone.TopRight;
        bool bottom = zone is DragZone.Bottom or DragZone.BottomLeft or DragZone.BottomRight;

        double w = start.Width;
        double byX = left ? w - dx : right ? w + dx : w;
        double byY = (top || bottom) ? (start.Height + (bottom ? dy : -dy)) * aspect : w;
        // 모서리는 더 크게 늘린 쪽을 따른다. 위/아래 가장자리만 잡았을 때는 세로 기준.
        double nw = (left || right) && (top || bottom) ? Math.Max(byX, byY)
                  : (left || right) ? byX : byY;
        nw = Math.Clamp(nw, MinWidth, MaxWidth);
        int W = (int)Math.Round(nw), H = (int)Math.Round(nw / aspect);

        int x = left ? start.Right - W : start.Left;
        int y = top ? start.Bottom - H : start.Top;
        return Rect32.FromSize(x, y, W, H);
    }

    /// <summary>이동 중인 사각형을 다른 타일·모니터 가장자리에 붙인다 (가까울 때만).</summary>
    public static (int X, int Y) SnapMove(Rect32 r, IReadOnlyList<Rect32> others, IReadOnlyList<Rect32> monitors)
    {
        int w = r.Width, h = r.Height;
        int bestX = r.Left, bestY = r.Top;
        int dxBest = SnapDistance + 1, dyBest = SnapDistance + 1;

        void TryX(int cand) { var d = Math.Abs(cand - r.Left); if (d < dxBest) { dxBest = d; bestX = cand; } }
        void TryY(int cand) { var d = Math.Abs(cand - r.Top); if (d < dyBest) { dyBest = d; bestY = cand; } }

        foreach (var o in others)
        {
            // 세로로 가까운 타일만 좌우 붙이기의 대상이 된다 (멀리 있는 타일에 끌려가지 않게).
            bool nearV = r.Top < o.Bottom + SnapDistance && r.Bottom > o.Top - SnapDistance;
            bool nearH = r.Left < o.Right + SnapDistance && r.Right > o.Left - SnapDistance;
            if (nearV) { TryX(o.Left - w); TryX(o.Right); TryX(o.Left); TryX(o.Right - w); }
            if (nearH) { TryY(o.Top - h); TryY(o.Bottom); TryY(o.Top); TryY(o.Bottom - h); }
        }
        foreach (var m in monitors)
        {
            TryX(m.Left); TryX(m.Right - w);
            TryY(m.Top); TryY(m.Bottom - h);
        }
        return (bestX, bestY);
    }

    /// <summary>어느 모니터에도 충분히 걸치지 않으면(모니터 구성이 바뀐 경우) false.</summary>
    public static bool IsOnScreen(Rect32 r, IReadOnlyList<Rect32> monitors)
    {
        foreach (var m in monitors)
        {
            int ox = Math.Min(r.Right, m.Right) - Math.Max(r.Left, m.Left);
            int oy = Math.Min(r.Bottom, m.Bottom) - Math.Max(r.Top, m.Top);
            if (ox >= 40 && oy >= 30) return true;
        }
        return false;
    }

    public static bool Intersects(Rect32 a, Rect32 b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    /// <summary>비어 있는 자리를 찾는다: 모니터 왼쪽 위에서부터 타일 크기 단위로 오른쪽, 아래로 훑는다.</summary>
    public static (int X, int Y) FindFreeSlot(Rect32 monitor, int w, int h, IReadOnlyList<Rect32> occupied)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        const int margin = 20;
        for (int y = monitor.Top + margin; y + h <= monitor.Bottom; y += h)
            for (int x = monitor.Left + margin; x + w <= monitor.Right; x += w)
            {
                var cand = Rect32.FromSize(x, y, w, h);
                if (!occupied.Any(o => Intersects(cand, o))) return (x, y);
            }
        return (monitor.Left + margin, monitor.Top + margin);
    }
}
