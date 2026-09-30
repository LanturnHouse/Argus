using Argus.Modules.Capture.Native;

namespace Argus.Modules.Capture;

/// <summary>이전 프레임의 영역과 비교해 "변한 픽셀 수"를 센다.</summary>
internal sealed class ChangeDetector
{
    private byte[]? _prev;
    private int _w, _h;

    public void Reset() => _prev = null;

    /// <summary>영역을 잘라낸 버퍼를 반환하고, 이전 영역과 비교한 변한 픽셀 수를 out 으로 준다. 첫 프레임은 0.</summary>
    public byte[] Compare(Frame frame, int rx, int ry, int rw, int rh, int threshold, out int changed)
    {
        var cur = Crop(frame, rx, ry, rw, rh);
        changed = 0;
        if (_prev != null && _w == rw && _h == rh)
        {
            var prev = _prev;
            for (int i = 0; i < cur.Length; i += 4)
            {
                if (Math.Abs(cur[i] - prev[i]) > threshold
                    || Math.Abs(cur[i + 1] - prev[i + 1]) > threshold
                    || Math.Abs(cur[i + 2] - prev[i + 2]) > threshold)
                    changed++;
            }
        }
        _prev = cur; _w = rw; _h = rh;
        return cur;
    }

    public static byte[] Crop(Frame f, int rx, int ry, int rw, int rh)
    {
        if (rx == 0 && ry == 0 && rw == f.Width && rh == f.Height) return f.Bgra;
        var dst = new byte[rw * rh * 4];
        for (int y = 0; y < rh; y++)
            Buffer.BlockCopy(f.Bgra, ((ry + y) * f.Width + rx) * 4, dst, y * rw * 4, rw * 4);
        return dst;
    }
}
