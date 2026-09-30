namespace Argus.Modules.Capture.StatusIcons;

/// <summary>상태이상 아이콘 인식 설정. 읽을 영역은 클라이언트 화면 '아래 가운데'를 기준으로 한 픽셀 값이다 (EVE 의 HUD 와 아이콘 줄이 화면 아래 가운데에 붙어 있어서, 해상도가 달라도 같은 값이 맞는다).</summary>
public sealed class StatusIconSettings
{
    public const int MinHz = 1, MaxHz = 10;

    public bool Enabled { get; set; } = true;
    /// <summary>1초에 몇 번 읽는가.</summary>
    public int Hz { get; set; } = 5;

    /// <summary>아이콘 모양이 태클 아이콘과 이만큼(0~1) 이상 닮아야 태클 후보로 본다. 노스·ECM·웹·댐프너는 0.45 아래였고 태클 셋은 0.77 이상이었다.</summary>
    public double ShapeThreshold { get; set; } = 0.65;

    /// <summary>연속 몇 번 보이면 켜는가 / 연속 몇 번 안 보이면 끄는가 (순간적인 오인식·깜빡임 방지).</summary>
    public int OnFrames { get; set; } = 2;
    public int OffFrames { get; set; } = 3;

    // ---- 읽는 영역 (클라이언트 영역 기준, px) ----
    /// <summary>영역의 아래쪽 끝이 화면 아래에서 몇 px 위인가 / 영역의 높이 / 화면 가운데에서 좌우로 각각 몇 px.</summary>
    public int BottomOffset { get; set; } = 150;
    public int RegionHeight { get; set; } = 180;
    public int HalfWidth { get; set; } = 400;

    /// <summary>찾는 아이콘의 반지름 범위(px). EVE UI 배율에 따라 달라진다 (100% 에서 약 17).</summary>
    public int MinRadius { get; set; } = 9;
    public int MaxRadius { get; set; } = 30;

    public void Normalize()
    {
        Hz = Math.Clamp(Hz, MinHz, MaxHz);
        ShapeThreshold = Math.Clamp(ShapeThreshold, 0.3, 0.95);
        OnFrames = Math.Clamp(OnFrames, 1, 10);
        OffFrames = Math.Clamp(OffFrames, 1, 20);
        BottomOffset = Math.Clamp(BottomOffset, 0, 2000);
        RegionHeight = Math.Clamp(RegionHeight, 40, 800);
        HalfWidth = Math.Clamp(HalfWidth, 100, 2000);
        MinRadius = Math.Clamp(MinRadius, 4, 60);
        MaxRadius = Math.Clamp(MaxRadius, MinRadius + 1, 100);
    }

    /// <summary>클라이언트 크기에서 읽을 영역(왼쪽 위 x, y, 폭, 높이)을 계산한다.</summary>
    public (int X, int Y, int W, int H) RegionFor(int clientW, int clientH)
    {
        var w = Math.Min(HalfWidth * 2, clientW);
        var h = Math.Min(RegionHeight, clientH);
        var x = Math.Clamp(clientW / 2 - w / 2, 0, Math.Max(0, clientW - w));
        var y = Math.Clamp(clientH - BottomOffset - h, 0, Math.Max(0, clientH - h));
        return (x, y, w, h);
    }
}
