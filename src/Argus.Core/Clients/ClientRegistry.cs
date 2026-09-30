using System.Runtime.InteropServices;
using System.Text;
using Argus.Core.Events;

namespace Argus.Core.Clients;

/// <summary>실행 중인 EVE 클라이언트 하나. 캡처/프리뷰 모듈이 공유한다.</summary>
public sealed record EveClient(nint Hwnd, int ProcessId, string Character);

public sealed record ClientsChanged(IReadOnlyList<EveClient> Clients);

public interface IClientRegistry
{
    IReadOnlyList<EveClient> Current { get; }

    /// <summary>이 클라이언트 창을 앞으로 가져온다 (프리뷰 클릭, 단축키 전환 등 여러 모듈이 공유).</summary>
    bool Activate(EveClient client);
}

/// <summary>창 제목 "EVE - {캐릭터}" 를 폴링해 클라이언트 목록을 유지한다.</summary>
public sealed class ClientRegistry(IEventBus bus) : IClientRegistry, IDisposable
{
    private const string Prefix = "EVE - ";
    private readonly CancellationTokenSource _cts = new();
    private volatile IReadOnlyList<EveClient> _current = [];

    public IReadOnlyList<EveClient> Current => _current;

    public bool Activate(EveClient client) => WindowFocus.Activate(client.Hwnd);

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                var next = Scan();
                if (!next.Select(c => (c.Hwnd, c.Character)).SequenceEqual(_current.Select(c => (c.Hwnd, c.Character))))
                {
                    _current = next;
                    bus.Publish(new ClientsChanged(next));
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static List<EveClient> Scan()
    {
        var found = new List<EveClient>();
        EnumWindows((h, _) =>
        {
            var len = GetWindowTextLength(h);
            if (len <= Prefix.Length) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (!title.StartsWith(Prefix, StringComparison.Ordinal)) return true; // 로그인 전 "EVE" 창은 제외
            GetWindowThreadProcessId(h, out var pid);
            if (!IsEveProcess((int)pid)) return true; // EVE-O Preview 등 같은 제목의 썸네일 창 제외
            found.Add(new EveClient(h, (int)pid, title[Prefix.Length..].Trim()));
            return true;
        }, 0);
        return [.. found.OrderBy(c => c.Character, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsEveProcess(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName.Equals("exefile", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void Dispose() => _cts.Cancel();

    private delegate bool EnumProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
}
