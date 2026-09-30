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

    public int IntervalMs { get; set; } = 1000;
    public int AlertIntervalSec { get; set; } = 5;

    /// <summary>채널 하나가 이 값보다 크게 바뀌면 "변한 픽셀"로 센다. 작을수록 민감.</summary>
    public int PixelThreshold { get; set; } = 12;
    /// <summary>변한 픽셀이 이 개수 이상이면 변화로 판정한다. 작을수록 민감.</summary>
    public int MinChangedPixels { get; set; } = 3;

    public SaveMode SaveMode { get; set; } = SaveMode.SelectedArea;
    public bool Beep { get; set; } = true;

    public bool HasRoi => RoiW > 0 && RoiH > 0;
}

public sealed class CaptureSettings
{
    public string OutputFolder { get; set; } = "";
    public List<ClientCaptureConfig> Clients { get; set; } = [];
}
