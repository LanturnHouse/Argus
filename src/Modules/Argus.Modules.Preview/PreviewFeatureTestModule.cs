using Argus.Core.Modules;

namespace Argus.Modules.Preview;

/// <summary>설정 > 프리뷰 맨 아래 '기능 테스트' 항목. 프리뷰 모듈의 하위 항목으로 표시되며, 이 모듈을 빼면 시험 항목만 사라진다.</summary>
public sealed class PreviewFeatureTestModule : IArgusModule
{
    public string Id => "argus.preview.featuretest";
    public string DisplayName => "기능 테스트";
    public string? SettingsParentId => "argus.preview";

    public Task StartAsync(IModuleContext context, CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;

    public object? CreateSettingsView() => PreviewModule.Current is { } svc ? new FeatureTestPanel(svc) : null;
}
