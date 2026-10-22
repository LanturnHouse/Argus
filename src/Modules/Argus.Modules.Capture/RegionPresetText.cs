using System.Windows;

namespace Argus.Modules.Capture;

/// <summary>영역 프리셋을 화면에 보여 줄 때 쓰는 문구와, 창 크기가 다를 때의 확인 창. CCTV 페이지와 영역 선택기가 같이 쓴다.</summary>
internal static class RegionPresetText
{
    private static string Seconds(int ms) => $"{ms / 1000.0:0.##}초";

    private static string SaveLabel(SaveMode m) => m switch
    {
        SaveMode.None => "저장 안 함",
        SaveMode.FullClient => "전체 화면 저장",
        _ => "감시 영역만 저장",
    };

    /// <summary>영역 한 줄: "259 × 284 · 오른쪽 위 기준 · 2560 × 1440 화면".</summary>
    public static string RegionLine(RegionPreset p) =>
        $"{p.RoiW} × {p.RoiH} · {p.AnchorLabel} 기준 · " + (p.FrameW > 0 && p.FrameH > 0 ? $"{p.FrameW} × {p.FrameH} 화면" : "화면 크기 모름");

    /// <summary>설정 한 줄: "감도 12 / 3 · 주기 1초 · 알림 5초 · 알림음 · 감시 영역만 저장".</summary>
    public static string SettingsLine(RegionPreset p) =>
        $"감도 {p.PixelThreshold} / {p.MinChangedPixels} · 주기 {Seconds(p.IntervalMs)} · 알림 {p.AlertIntervalSec}초" + (p.Beep ? " · 알림음" : "") + $" · {SaveLabel(p.SaveMode)}";

    /// <summary>
    /// 프리셋을 이 크기의 클라이언트 화면에 적용해도 되는지 묻는다. 저장할 때와 크기가 같으면 묻지 않고 true.
    /// 크기가 다르면 경고하고, 그래도 적용하면 가장 가까운 모서리 기준으로 옮긴다. 창 크기를 모르면(최소화 등) 좌표 그대로 적용할지 묻는다.
    /// </summary>
    public static bool ConfirmFit(Window? owner, RegionPreset p, int frameW, int frameH)
    {
        if (frameW <= 0 || frameH <= 0)
            return Ask(owner, $"클라이언트 창 크기를 확인할 수 없습니다. 창이 최소화되어 있다면 복원한 뒤 다시 시도하세요.\n\n'{p.Name}' 프리셋을 저장할 때의 좌표 그대로 적용할까요?", "영역 프리셋");
        if (p.FitsAsIs(frameW, frameH)) return true;
        return Ask(owner, $"'{p.Name}' 프리셋은 {p.FrameW} × {p.FrameH} 화면에서 저장했는데, 지금 클라이언트 화면은 {frameW} × {frameH} 입니다.\n\n" +
                          $"그래도 적용하면 감시 영역을 가장 가까운 모서리({p.AnchorLabel}) 기준으로 옮겨 맞춥니다. 적용할까요?", "창 크기가 다릅니다");
    }

    private static bool Ask(Window? owner, string text, string title) =>
        (owner != null ? MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                       : MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) == MessageBoxResult.Yes;
}
