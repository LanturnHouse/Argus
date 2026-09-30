using Argus.Core.Modules;

namespace Argus.Modules.Profiles;

public sealed class ProfilesModule : IArgusModule
{
    private ProfilesService? _service;

    public string Id => "argus.profiles";
    public string Icon => "";
    public string DisplayName => "설정 프리셋";

    public Task StartAsync(IModuleContext context, CancellationToken ct)
    {
        var names = new EsiNames(context.Settings);
        _service = new ProfilesService(
            context.Settings,
            context.DataDirectory(Id),
            names,
            () => [.. context.Clients.Current.Select(c => c.Character)]);
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public object? CreateView() => _service is null ? null : new ProfilesView(_service);

    public object? CreateSettingsView() => _service is null ? null : new ProfilesSettingsView(_service);
}
