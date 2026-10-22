namespace Argus.Modules.Capture;

public enum SaveMode { None, SelectedArea, FullClient }

public sealed class ClientCaptureConfig
{
    public string Character { get; set; } = "";

    /// <summary>무엇을 감시 중인지 사용자가 적는 메모 (예: 적 스트럭처 200km).</summary>
    public string Note { get; set; } = "";

    /// <summary>감시 영역(클라이언트 영역 픽셀 좌표). 반드시 지정해야 감시를 시작할 수 있다.</summary>
    public int RoiX { get; set; }
    public int RoiY { get; set; }
    public int RoiW { get; set; }
    public int RoiH { get; set; }

    /// <summary>감시 영역을 지정할 때의 클라이언트 화면 크기 (0 이면 모름). 영역 프리셋으로 저장할 때 다른 크기의 창에 옮기는 기준이 된다.</summary>
    public int RoiFrameW { get; set; }
    public int RoiFrameH { get; set; }

    public int IntervalMs { get; set; } = 1000;
    public int AlertIntervalSec { get; set; } = 5;

    /// <summary>채널 하나가 이 값보다 크게 바뀌면 "변한 픽셀"로 센다. 작을수록 민감.</summary>
    public int PixelThreshold { get; set; } = 12;
    /// <summary>변한 픽셀이 이 개수 이상이면 변화로 판정한다. 작을수록 민감.</summary>
    public int MinChangedPixels { get; set; } = 3;

    public SaveMode SaveMode { get; set; } = SaveMode.SelectedArea;
    public bool Beep { get; set; } = true;

    public bool HasRoi => RoiW > 0 && RoiH > 0;

    /// <summary>창 크기가 바뀌어 영역이 프레임 밖으로 나가면 잘라 맞춘 영역을 반환한다.</summary>
    public (int X, int Y, int W, int H) ResolveRoi(int frameW, int frameH)
    {
        var x = Math.Clamp(RoiX, 0, frameW - 1);
        var y = Math.Clamp(RoiY, 0, frameH - 1);
        return (x, y, Math.Clamp(RoiW, 1, frameW - x), Math.Clamp(RoiH, 1, frameH - y));
    }
}

/// <summary>
/// 영역 프리셋: 감시 영역과 감시 설정(감도 · 주기 · 알림 · 저장 범위)을 이름 붙여 저장해 두고, 아무 클라이언트에나 한 번에 적용한다.
/// 메모는 감시 대상마다 다르므로 담지 않는다.
/// </summary>
public sealed class RegionPreset
{
    public string Name { get; set; } = "";

    /// <summary>감시 영역 (저장할 때의 클라이언트 화면 픽셀 좌표).</summary>
    public int RoiX { get; set; }
    public int RoiY { get; set; }
    public int RoiW { get; set; }
    public int RoiH { get; set; }

    /// <summary>저장할 때의 클라이언트 화면 크기 (0 이면 모름: 다른 크기의 창에도 좌표를 그대로 쓴다).</summary>
    public int FrameW { get; set; }
    public int FrameH { get; set; }

    public int IntervalMs { get; set; } = 1000;
    public int AlertIntervalSec { get; set; } = 5;
    public int PixelThreshold { get; set; } = 12;
    public int MinChangedPixels { get; set; } = 3;
    public SaveMode SaveMode { get; set; } = SaveMode.SelectedArea;
    public bool Beep { get; set; } = true;

    private bool KnowsFrame => FrameW > 0 && FrameH > 0;

    /// <summary>영역이 오른쪽 · 아래쪽 가장자리에 더 가까운가 (가까운 모서리 기준으로 옮길 때 쓴다).</summary>
    private bool NearRight => KnowsFrame && FrameW - (RoiX + RoiW) < RoiX;
    private bool NearBottom => KnowsFrame && FrameH - (RoiY + RoiH) < RoiY;

    /// <summary>
    /// 이 크기의 화면에 그대로 맞는가: 저장할 때와 같은 크기이거나(창 테두리 반올림 등 2px 이내 차이는 같은 크기로 본다), 저장할 때 크기를 모르는 경우.
    /// </summary>
    public bool FitsAsIs(int frameW, int frameH) => !KnowsFrame || (Math.Abs(FrameW - frameW) <= SizeTolerance && Math.Abs(FrameH - frameH) <= SizeTolerance);

    private const int SizeTolerance = 2;

    /// <summary>기준 모서리 이름 (예: "오른쪽 위").</summary>
    public string AnchorLabel => !KnowsFrame ? "왼쪽 위" : $"{(NearRight ? "오른쪽" : "왼쪽")} {(NearBottom ? "아래" : "위")}";

    /// <summary>
    /// 다른 크기의 화면에 옮긴 영역. EVE 의 창(오버뷰 등)은 클라이언트 창 크기가 바뀌어도 커지지 않고 가장자리에 붙어 있으므로,
    /// 영역에서 가장 가까운 모서리까지의 거리를 그대로 두고 옮긴다. 화면 밖으로 나가는 부분은 잘라 맞춘다.
    /// </summary>
    public (int X, int Y, int W, int H) FitTo(int frameW, int frameH)
    {
        if (frameW <= 0 || frameH <= 0) return (RoiX, RoiY, RoiW, RoiH);
        var x = NearRight ? frameW - (FrameW - RoiX) : RoiX;
        var y = NearBottom ? frameH - (FrameH - RoiY) : RoiY;
        x = Math.Clamp(x, 0, frameW - 1);
        y = Math.Clamp(y, 0, frameH - 1);
        return (x, y, Math.Clamp(RoiW, 1, frameW - x), Math.Clamp(RoiH, 1, frameH - y));
    }

    /// <summary>클라이언트의 지금 감시 영역과 설정으로 만든다.</summary>
    public static RegionPreset From(string name, ClientCaptureConfig c, int frameW, int frameH) => new()
    {
        Name = name, RoiX = c.RoiX, RoiY = c.RoiY, RoiW = c.RoiW, RoiH = c.RoiH, FrameW = frameW, FrameH = frameH,
        IntervalMs = c.IntervalMs, AlertIntervalSec = c.AlertIntervalSec, PixelThreshold = c.PixelThreshold, MinChangedPixels = c.MinChangedPixels,
        SaveMode = c.SaveMode, Beep = c.Beep,
    };

    /// <summary>클라이언트에 적용한다: 영역은 그 화면 크기에 맞춰 옮기고(frameW/H 가 0 이면 좌표 그대로), 감시 설정은 그대로 덮어쓴다.</summary>
    public void ApplyTo(ClientCaptureConfig c, int frameW, int frameH)
    {
        (c.RoiX, c.RoiY, c.RoiW, c.RoiH) = FitTo(frameW, frameH);
        c.RoiFrameW = frameW > 0 ? frameW : FrameW;
        c.RoiFrameH = frameH > 0 ? frameH : FrameH;
        c.IntervalMs = IntervalMs; c.AlertIntervalSec = AlertIntervalSec;
        c.PixelThreshold = PixelThreshold; c.MinChangedPixels = MinChangedPixels;
        c.SaveMode = SaveMode; c.Beep = Beep;
    }
}

public sealed class CaptureSettings
{
    public string OutputFolder { get; set; } = "";
    public List<ClientCaptureConfig> Clients { get; set; } = [];
    public List<RegionPreset> RegionPresets { get; set; } = [];
}
