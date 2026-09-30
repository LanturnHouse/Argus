using System.Collections.Concurrent;

namespace Argus.Core.Events;

public interface IEventBus
{
    void Publish<T>(T evt);
    IDisposable Subscribe<T>(Action<T> handler);
}

public sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();

    public void Publish<T>(T evt)
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        Delegate[] snapshot;
        lock (list) snapshot = [.. list];
        foreach (var h in snapshot)
        {
            try { ((Action<T>)h)(evt); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[EventBus] {typeof(T).Name} handler failed: {ex}"); } // 구독자 오류가 발행자에게 전파되지 않게
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        var list = _handlers.GetOrAdd(typeof(T), _ => []);
        lock (list) list.Add(handler);
        return new Unsub(() => { lock (list) list.Remove(handler); });
    }

    private sealed class Unsub(Action a) : IDisposable { public void Dispose() => a(); }
}
