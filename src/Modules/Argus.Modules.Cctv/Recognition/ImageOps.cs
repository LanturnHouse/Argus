using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Argus.Modules.Cctv;

/// <summary>BGRA 픽셀 덩어리(행당 너비×4바이트).</summary>
public sealed record Bitmap32(byte[] Bgra, int Width, int Height)
{
    public int Stride => Width * 4;
}

/// <summary>이미지 읽기 · 자르기 · 저장. 영역을 잘라 비전 모델에 보낼 PNG 를 만드는 데 쓴다.</summary>
public static class ImageOps
{
    public static Bitmap32 Load(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource src = decoder.Frames[0];
        if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var px = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        return new Bitmap32(px, src.PixelWidth, src.PixelHeight);
    }

    /// <summary>파일 전체를 읽지 않고 이미지 크기만 읽는다. 읽을 수 없으면 null.</summary>
    public static (int Width, int Height)? Size(string path)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
        }
        catch { return null; }
    }

    public static Bitmap32 Crop(Bitmap32 src, int left, int top, int width, int height)
    {
        var dst = new byte[width * height * 4];
        for (int y = 0; y < height; y++) Buffer.BlockCopy(src.Bgra, ((top + y) * src.Width + left) * 4, dst, y * width * 4, width * 4);
        return new Bitmap32(dst, width, height);
    }

    public static byte[] EncodePng(Bitmap32 bmp)
    {
        var source = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null, bmp.Bgra, bmp.Stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>픽셀 내용의 64비트 해시(FNV-1a). 같은 화면 조각인지 빠르게 가리는 데 쓴다.</summary>
    public static ulong Hash(Bitmap32 bmp)
    {
        ulong h = 14695981039346656037UL;
        var b = bmp.Bgra;
        for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 1099511628211UL; }
        return h ^ ((ulong)bmp.Width << 32) ^ (uint)bmp.Height;
    }
}
