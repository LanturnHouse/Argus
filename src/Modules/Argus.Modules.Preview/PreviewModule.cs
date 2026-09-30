using System.Windows;
using Argus.Core.Clients;
using Argus.Core.Dashboard;
using Argus.Core.Modules;

namespace Argus.Modules.Preview;

public sealed class PreviewModule : IArgusModule, IDashboardContributor
{
    /// <summary>같은 어셈블리의 하위 설정 항목(기능 테스트)이 프리뷰 서비스를 찾는 자리.</summary>
    internal static PreviewService? Current { get; private set; }

    private PreviewService? _service;
    private HotkeyService? _hotkeys;
    private IClientRegistry? _clients;

    public string Id => "argus.preview";
    public string Icon => "";
    public string DisplayName => "프리뷰";

    public Task StartAsync(IModuleContext context, CancellationToken ct)
    {
        _clients = context.Clients;
        // 창은 UI 스레드에서 만들어야 한다.
        Application.Current.Dispatcher.Invoke(() =>
        {
            _service = new PreviewService(context);
            Current = _service;
            _service.Start();
            _hotkeys = new HotkeyService(context, _service);
            _hotkeys.Start();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Application.Current?.Dispatcher.Invoke(() => { Current = null; _hotkeys?.Dispose(); _service?.Dispose(); });
        return Task.CompletedTask;
    }

    public object? CreateView() => _service is null || _clients is null || _hotkeys is null ? null : new PreviewView(_service, _clients, _hotkeys);

    // ---------- 대시보드 ----------

    public IReadOnlyList<DashboardChip> ClientChips(string character)
    {
        if (_service is null) return [];
        var info = _service.Clients().FirstOrDefault(c => string.Equals(c.Character, character, StringComparison.OrdinalIgnoreCase));
        if (info == null) return [];
        var chips = new List<DashboardChip>
        {
            _service.CycleNumber(character) is { } n
                ? new($"사이클 {n}", ChipTone.Accent, "다음/이전 전환 순서", Column: DashboardColumns.Cycle)
                : new("사이클 제외", ChipTone.Neutral, "다음/이전 전환에 포함되지 않음", Column: DashboardColumns.Cycle),
        };
        if (info.Layout.Hotkey is { IsEmpty: false } h) chips.Add(new($"⌨ {h}", ChipTone.Neutral, "이 클라이언트로 바로 가는 단축키", Column: DashboardColumns.Cycle));
        if (!info.Layout.Visible) chips.Add(new("프리뷰 숨김", ChipTone.Warn, "이 프리셋에서 프리뷰를 숨겼습니다"));
        return chips;
    }

    public IReadOnlyList<DashboardChip> SummaryChips()
    {
        if (_service is null) return [];
        var cycle = _service.Clients().Count(c => c.Layout.InCycle);
        return
        [
            new($"프리셋 '{_service.ActivePreset.Name}'", ChipTone.Accent, "지금 적용 중인 프리뷰 레이아웃 프리셋"),
            _service.Settings.Enabled ? new("프리뷰 켜짐", ChipTone.Good) : new("프리뷰 꺼짐", ChipTone.Neutral),
            new($"사이클 {cycle}개", ChipTone.Neutral, "다음/이전 전환에 포함된 클라이언트 수"),
        ];
    }

    public object? CreateSettingsView() => _service is null || _hotkeys is null ? null : new PreviewSettingsView(_service);
}
