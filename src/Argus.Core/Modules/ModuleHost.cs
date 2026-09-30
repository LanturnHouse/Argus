using Argus.Core.Clients;
using Argus.Core.Events;
using Argus.Core.Settings;

namespace Argus.Core.Modules;

public sealed record ModuleFailed(string ModuleId, Exception Error);

public sealed class ModuleContext(IClientRegistry clients, IEventBus events, ISettingsStore settings, string dataRoot) : IModuleContext
{
    public IClientRegistry Clients => clients;
    public IEventBus Events => events;
    public ISettingsStore Settings => settings;

    public string DataDirectory(string moduleId)
    {
        var dir = Path.Combine(dataRoot, moduleId);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed class ModuleHost(IModuleContext context)
{
    private readonly List<IArgusModule> _modules = [];
    private CancellationTokenSource? _cts;

    public IReadOnlyList<IArgusModule> Modules => _modules;

    public void Register(IArgusModule module) => _modules.Add(module);

    public async Task StartAllAsync()
    {
        _cts = new CancellationTokenSource();
        foreach (var m in _modules)
        {
            try { await m.StartAsync(context, _cts.Token); }
            catch (Exception ex) { context.Events.Publish(new ModuleFailed(m.Id, ex)); } // 한 모듈 실패가 전체를 막지 않음
        }
    }

    public async Task StopAllAsync()
    {
        _cts?.Cancel();
        foreach (var m in Enumerable.Reverse(_modules))
        {
            try { await m.StopAsync(); } catch { /* 종료 중 오류는 무시 */ }
        }
    }
}
