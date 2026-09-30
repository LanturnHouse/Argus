using Argus.Core.Modules;

namespace Argus.Modules.Cctv;

public sealed class CctvModule : IArgusModule
{
    public string Id => "argus.cctv";
    public string DisplayName => "CCTV";
    public Task StartAsync(IModuleContext context, CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}
