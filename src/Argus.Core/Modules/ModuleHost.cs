using Argus.Core.Clients;
using Argus.Core.Dashboard;
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

public sealed class ModuleHost(IModuleContext context) : IDashboardContributor
{
    private readonly List<IArgusModule> _modules = [];
    private readonly List<(string Name, Exception Error)> _failures = [];
    private CancellationTokenSource? _cts;

    public IReadOnlyList<IArgusModule> Modules => _modules;

    public void Register(IArgusModule module) => _modules.Add(module);

    public async Task StartAllAsync()
    {
        _cts = new CancellationTokenSource();
        foreach (var m in _modules)
        {
            try { await m.StartAsync(context, _cts.Token); }
            catch (Exception ex) // 한 모듈 실패가 전체를 막지 않음
            {
                lock (_failures) _failures.Add((m.DisplayName, ex));
                System.Diagnostics.Trace.WriteLine($"[ModuleHost] {m.Id} 시작 실패: {ex}");
                context.Events.Publish(new ModuleFailed(m.Id, ex));
            }
        }
    }

    /// <summary>모듈 뷰 생성처럼 모듈 코드를 부르는 곳을 감싼다: 예외는 실패로 기록하고 null 을 돌려준다.</summary>
    public object? TryCreate(IArgusModule m, Func<IArgusModule, object?> create)
    {
        try { return create(m); }
        catch (Exception ex)
        {
            lock (_failures) _failures.Add((m.DisplayName, ex));
            System.Diagnostics.Trace.WriteLine($"[ModuleHost] {m.Id} 화면 생성 실패: {ex}");
            return null;
        }
    }

    public IReadOnlyList<DashboardChip> SummaryChips()
    {
        (string Name, Exception Error)[] failures;
        lock (_failures) failures = [.. _failures];
        if (failures.Length == 0) return [];
        return [new DashboardChip($"모듈 오류 {failures.Length}", ChipTone.Bad, string.Join("\n", failures.Select(f => $"{f.Name}: {f.Error.Message}")))];
    }

    public async Task StopAllAsync()
    {
        _cts?.Cancel();
        foreach (var m in Enumerable.Reverse(_modules))
        {
            try { await m.StopAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[ModuleHost] {m.Id} 종료 오류: {ex.Message}"); } // 종료 중 오류는 기록만 하고 계속
        }
    }
}
