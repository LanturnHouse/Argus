using Argus.Core.Modules;

namespace Argus.Modules.Capture.StatusIcons;

/// <summary>
/// 화면의 상태이상 아이콘(스크램블·디스럽터·HIC 포인팅)을 읽어 태클 상태를 이벤트로 알린다. 프리뷰 HUD 가 이를 받아 리본에 쓴다.
/// 창 캡처 코드를 재사용하려고 화면 감시 캡처 모듈과 같은 어셈블리에 있지만, 서로는 이벤트로만 통하고 모듈 등록도 따로다.
/// </summary>
public sealed class StatusIconModule : IArgusModule
{
    private StatusIconService? _service;

    public string Id => "argus.statusicons";
    public string DisplayName => "상태이상 인식";
    public string? SettingsParentId => "argus.preview";

    public Task StartAsync(IModuleContext context, CancellationToken ct)
    {
        _service = new StatusIconService(context);
        _service.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _service?.Dispose();
        return Task.CompletedTask;
    }

    public object? CreateSettingsView() => _service is { } s ? new StatusIconSettingsView(s) : null;
}
