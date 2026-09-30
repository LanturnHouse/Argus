namespace Argus.Modules.Capture.StatusIcons;

public enum TackleKind { Disrupt, Scram, Hic }

/// <summary>찾아낸 원형 아이콘 하나.</summary>
/// <param name="Shape">태클 아이콘 기준 그림과의 모양 상관 (1 에 가까울수록 같은 모양). 태클 셋 중 가장 높은 값.</param>
/// <param name="Ring">테두리 고리의 평균 채도(0~255). 상태이상 아이콘은 진한 붉은 고리(약 190)이고, 웜홀 이펙트 같은 것은 훨씬 옅다(약 135).</param>
/// <param name="Kind">모양이 기준 이상이고 고리가 진할 때만 종류. 아니면 null (노스·ECM·웹·웜홀 같은 다른 것).</param>
public sealed record DetectedIcon(int X, int Y, int Radius, double Shape, int Ring, TackleKind? Kind);

/// <summary>
/// 화면 조각(BGRA)에서 붉은 원 테두리의 상태이상 아이콘을 찾아 태클 종류를 가려낸다. 순수 계산이라 화면 캡처와 무관하다.
/// 1) 붉은 픽셀 덩어리 중 원에 가까운 것을 찾고 2) 안쪽 모양이 태클 아이콘(디스럽터·HIC·스크램블은 같은 모양)과 닮은지 보고
/// 3) 닮았으면 위쪽 광채 색이 가장 가까운 기준 아이콘으로 종류를 정한다 (청록 = 디스럽터, 파랑 = 스크램블, 분홍 = HIC 포인팅).
/// </summary>
internal sealed class IconDetector
{
    private const int N = 30;   // 비교할 때 아이콘을 맞추는 크기
    private const int RingMinSaturation = 165;   // 태클 아이콘 고리는 191~200, 웜홀 이펙트는 131~139, 그 사이

    private readonly (TackleKind Kind, float[] Glyph, float[] Color)[] _refs;

    public IconDetector(IReadOnlyList<(TackleKind Kind, byte[] Bgra, int W, int H)> references)
    {
        _refs = [.. references.Select(r =>
        {
            var disc = ResizeBox(r.Bgra, r.W, r.H, N);
            return (r.Kind, GlyphVector(disc), ColorFeatures(disc));
        })];
    }

    public IReadOnlyList<DetectedIcon> Detect(byte[] bgra, int w, int h, int minRadius, int maxRadius, double shapeThreshold)
    {
        var result = new List<DetectedIcon>();
        var mask = RedMask(bgra, w, h);
        mask = Close(mask, w, h);

        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;

            int minX = w, minY = h, maxX = -1, maxY = -1, area = 0;
            stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                int x = p % w, y = p / w;
                area++;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int q = ny * w + nx;
                        if (mask[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
                    }
            }

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (bw < minRadius * 2 || bw > maxRadius * 2 || bh < minRadius * 2 || bh > maxRadius * 2) continue;
            double aspect = (double)bw / bh;
            if (aspect < 0.8 || aspect > 1.25 || area < 0.35 * bw * bh) continue;

            int r = (bw + bh) / 4, cx = minX + bw / 2, cy = minY + bh / 2;
            var ring = RingSaturation(bgra, w, h, cx, cy, r);
            var (shape, kind) = Classify(bgra, w, h, cx, cy, r, shapeThreshold);
            if (ring < RingMinSaturation) kind = null;   // 상태이상 아이콘의 진한 붉은 고리가 아니면 태클로 보지 않는다
            result.Add(new DetectedIcon(cx, cy, r, shape, ring, kind));
        }
        return [.. result.OrderBy(i => i.X)];
    }

    // ---------- 원 찾기 ----------

    private static bool[] RedMask(byte[] bgra, int w, int h)
    {
        var mask = new bool[w * h];
        for (int i = 0; i < mask.Length; i++)
        {
            int b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2];
            Hsv(r, g, b, out var hue, out var sat, out var val);
            mask[i] = (hue < 10 || hue > 170) && sat > 110 && val > 70;   // 상태이상 아이콘의 어두운 붉은 원판
        }
        return mask;
    }

    /// <summary>OpenCV 와 같은 범위: 색상 0~179, 채도·명도 0~255.</summary>
    internal static void Hsv(int r, int g, int b, out int h, out int s, out int v)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max == 0 ? 0 : d * 255 / max;
        if (d == 0) { h = 0; return; }
        double hue;
        if (max == r) hue = 60.0 * (g - b) / d;
        else if (max == g) hue = 120.0 + 60.0 * (b - r) / d;
        else hue = 240.0 + 60.0 * (r - g) / d;
        if (hue < 0) hue += 360;
        h = (int)(hue / 2);
    }

    /// <summary>5×5 닫기(팽창 후 침식): 원판 안의 작은 구멍과 끊김을 메운다.</summary>
    private static bool[] Close(bool[] m, int w, int h) => Morph(Morph(m, w, h, dilate: true), w, h, dilate: false);

    private static bool[] Morph(bool[] src, int w, int h, bool dilate)
    {
        const int R = 2;
        var tmp = new bool[src.Length];
        var dst = new bool[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool acc = !dilate;
                for (int k = -R; k <= R && (dilate ? !acc : acc); k++)
                {
                    int xx = x + k;
                    bool v = xx < 0 || xx >= w ? !dilate : src[y * w + xx];   // 화면 밖은 팽창에서는 비어 있고 침식에서는 차 있는 것으로 (가장자리가 깎이지 않게)
                    if (dilate) { if (v) acc = true; } else if (!v) acc = false;
                }
                tmp[y * w + x] = acc;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool acc = !dilate;
                for (int k = -R; k <= R && (dilate ? !acc : acc); k++)
                {
                    int yy = y + k;
                    bool v = yy < 0 || yy >= h ? !dilate : tmp[yy * w + x];
                    if (dilate) { if (v) acc = true; } else if (!v) acc = false;
                }
                dst[y * w + x] = acc;
            }
        return dst;
    }

    /// <summary>반지름의 78%~98% 고리에 있는 픽셀의 평균 채도.</summary>
    private static int RingSaturation(byte[] bgra, int w, int h, int cx, int cy, int r)
    {
        double lo = 0.78 * r, hi = 0.98 * r;
        long sum = 0; int cnt = 0;
        for (int y = Math.Max(0, cy - r); y <= Math.Min(h - 1, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x <= Math.Min(w - 1, cx + r); x++)
            {
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d < lo || d > hi) continue;
                int i = (y * w + x) * 4;
                Hsv(bgra[i + 2], bgra[i + 1], bgra[i], out _, out var sat, out _);
                sum += sat; cnt++;
            }
        return cnt == 0 ? 0 : (int)(sum / cnt);
    }

    // ---------- 종류 가리기 ----------

    private (double Shape, TackleKind? Kind) Classify(byte[] bgra, int w, int h, int cx, int cy, int r, double shapeThreshold)
    {
        // 아이콘 원 안쪽을 정사각형으로 잘라 N×N 으로 맞춘다.
        int side = r * 2, x0 = cx - r, y0 = cy - r;
        var crop = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int sx = Math.Clamp(x0 + x, 0, w - 1), sy = Math.Clamp(y0 + y, 0, h - 1);
                Buffer.BlockCopy(bgra, (sy * w + sx) * 4, crop, (y * side + x) * 4, 4);
            }
        var disc = ResizeBox(crop, side, side, N);

        var glyph = GlyphVector(disc);
        double best = double.MinValue;
        foreach (var rf in _refs) best = Math.Max(best, Corr(glyph, rf.Glyph));
        if (best < shapeThreshold) return (best, null);

        // 모양이 태클이면 색이 가장 가까운 기준 아이콘의 종류로 정한다.
        var color = ColorFeatures(disc);
        TackleKind kind = _refs[0].Kind; double bestD = double.MaxValue;
        foreach (var rf in _refs)
        {
            double d = 0;
            for (int i = 0; i < color.Length; i++) d += (color[i] - rf.Color[i]) * (color[i] - rf.Color[i]);
            if (d < bestD) { bestD = d; kind = rf.Kind; }
        }
        return (best, kind);
    }

    /// <summary>안쪽 원판(반지름의 70%)의 밝기를 평균 0, 표준편차 1 로 맞춘 벡터.</summary>
    private static float[] GlyphVector(float[] disc)   // disc: N*N*3 (BGR)
    {
        var list = new List<float>();
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                if (!InDisc(x, y, 0.70)) continue;
                int i = (y * N + x) * 3;
                list.Add(0.114f * disc[i] + 0.587f * disc[i + 1] + 0.299f * disc[i + 2]);
            }
        var v = list.ToArray();
        float mean = v.Average();
        float sd = MathF.Sqrt(v.Sum(a => (a - mean) * (a - mean)) / v.Length) + 1e-6f;
        for (int i = 0; i < v.Length; i++) v[i] = (v[i] - mean) / sd;
        return v;
    }

    private static double Corr(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s / a.Length;
    }

    /// <summary>안쪽 원판에서 청록 / 파랑·보라 / 밝은 분홍 픽셀이 차지하는 비율.</summary>
    private static float[] ColorFeatures(float[] disc)
    {
        int total = 0, cyan = 0, blue = 0, pink = 0;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                if (!InDisc(x, y, 0.72)) continue;
                total++;
                int i = (y * N + x) * 3;
                Hsv((int)disc[i + 2], (int)disc[i + 1], (int)disc[i], out var hue, out var sat, out var val);
                if (sat > 40 && val > 100 && hue >= 72 && hue <= 100) cyan++;
                else if (sat > 40 && val > 100 && hue > 100 && hue <= 165) blue++;
                if (sat > 45 && val >= 225 && (hue <= 8 || hue >= 168)) pink++;
            }
        return [(float)cyan / total, (float)blue / total, (float)pink / total];
    }

    private static bool InDisc(int x, int y, double frac)
    {
        double c = (N - 1) / 2.0, dx = x - c, dy = y - c, r = frac * N / 2.0;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>정사각형 BGRA 를 N×N BGR(float)으로 줄이거나 늘린다 (칸 평균).</summary>
    internal static float[] ResizeBox(byte[] bgra, int w, int h, int n)
    {
        var dst = new float[n * n * 3];
        for (int y = 0; y < n; y++)
        {
            double fy0 = (double)y * h / n, fy1 = (double)(y + 1) * h / n;
            int y0 = (int)fy0, y1 = Math.Min(h, Math.Max(y0 + 1, (int)Math.Ceiling(fy1)));
            for (int x = 0; x < n; x++)
            {
                double fx0 = (double)x * w / n, fx1 = (double)(x + 1) * w / n;
                int x0 = (int)fx0, x1 = Math.Min(w, Math.Max(x0 + 1, (int)Math.Ceiling(fx1)));
                double b = 0, g = 0, r = 0; int cnt = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = (yy * w + xx) * 4;
                        b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; cnt++;
                    }
                int o = (y * n + x) * 3;
                dst[o] = (float)(b / cnt); dst[o + 1] = (float)(g / cnt); dst[o + 2] = (float)(r / cnt);
            }
        }
        return dst;
    }
}
