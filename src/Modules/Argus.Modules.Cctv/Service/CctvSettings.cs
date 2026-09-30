namespace Argus.Modules.Cctv;

/// <summary>CCTV 모듈 설정 (설정 키 "cctv").</summary>
public sealed class CctvSettings
{
    /// <summary>비전 모델(Ollama) 설정.</summary>
    public VisionSettings Vision { get; set; } = new();

    /// <summary>분석할 스크린샷 폴더. 비워 두면 화면 감시 캡처가 저장하는 폴더를 쓴다.</summary>
    public string ImageFolder { get; set; } = "";

    /// <summary>폴더를 몇 초마다 확인하는가.</summary>
    public int ScanSeconds { get; set; } = 2;

    /// <summary>읽을 이미지가 모두 끝난 뒤 모델을 내리기까지 기다리는 시간(초). 0 이면 바로 내린다. 이미지가 몇 초 간격으로 계속 들어올 때 올렸다 내렸다 하지 않게 하는 유예다.</summary>
    public int UnloadAfterIdleSeconds { get; set; } = 20;

    /// <summary>타임라인에 한 번에 불러올 이벤트 수.</summary>
    public int TimelineLimit { get; set; } = 250;

    public void Normalize()
    {
        Vision ??= new VisionSettings();
        if (string.IsNullOrWhiteSpace(Vision.Host)) Vision.Host = "http://127.0.0.1:11434";
        if (string.IsNullOrWhiteSpace(Vision.Model)) Vision.Model = "qwen2.5vl:7b";
        Vision.TimeoutSeconds = Math.Clamp(Vision.TimeoutSeconds, 10, 600);
        if (string.IsNullOrWhiteSpace(Vision.KeepAlive)) Vision.KeepAlive = "5m";
        ScanSeconds = Math.Clamp(ScanSeconds, 1, 30);
        UnloadAfterIdleSeconds = Math.Clamp(UnloadAfterIdleSeconds, 0, 600);
        TimelineLimit = Math.Clamp(TimelineLimit, 50, 2000);
    }
}
