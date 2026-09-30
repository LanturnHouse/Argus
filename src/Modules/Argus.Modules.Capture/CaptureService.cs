using System.Collections.Concurrent;
using System.IO;
using Argus.Core.Clients;
using Argus.Core.Events;
using Argus.Core.Modules;
using Argus.Modules.Capture.Native;

namespace Argus.Modules.Capture;

public sealed record DetectionRecord(DateTime At, int ChangedPixels, string? SavedPath);

/// <summary>클라이언트별 캡처 세션, 설정, 감시 루프를 관리한다.</summary>
public sealed class CaptureService : IDisposable
{
    private const string SettingsKey = "capture";

    private sealed class Entry
    {
        public required EveClient Client;
        public WgcSession? Session;
        public required ClientCaptureConfig Config;
        public required ClientMonitor Monitor;
    }

    private readonly IModuleContext _ctx;
    private readonly CaptureSettings _settings;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly Dictionary<string, List<DetectionRecord>> _log = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _subs = [];

    public CaptureService(IModuleContext ctx)
    {
        _ctx = ctx;
        _settings = ctx.Settings.Load<CaptureSettings>(SettingsKey);
        if (string.IsNullOrWhiteSpace(_settings.OutputFolder))
            _settings.OutputFolder = ctx.DataDirectory("argus.capture");
    }

    public void Start()
    {
        Sync(_ctx.Clients.Current);
        _subs.Add(_ctx.Events.Subscribe<ClientsChanged>(e => Sync(e.Clients)));
        _subs.Add(_ctx.Events.Subscribe<RegionChanged>(OnRegionChanged));
    }

    public string OutputFolder
    {
        get => _settings.OutputFolder;
        set { _settings.OutputFolder = value; Save(); OutputFolderChanged?.Invoke(); }
    }

    public event Action? OutputFolderChanged;

    public IReadOnlyList<string> Characters => [.. _entries.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];

    public event Action? ClientsUpdated;
    public event Action<string>? DetectionLogged;

    private void OnRegionChanged(RegionChanged e)
    {
        lock (_log)
        {
            if (!_log.TryGetValue(e.Character, out var list)) _log[e.Character] = list = [];
            list.Insert(0, new DetectionRecord(e.At, e.ChangedPixels, e.SavedPath));
            if (list.Count > 200) list.RemoveRange(200, list.Count - 200);
        }
        DetectionLogged?.Invoke(e.Character);
    }

    /// <summary>최근 감지 기록 (최신순).</summary>
    public IReadOnlyList<DetectionRecord> GetLog(string character)
    {
        lock (_log) return _log.TryGetValue(character, out var l) ? [.. l] : [];
    }

    public ClientCaptureConfig? GetConfig(string character) => _entries.TryGetValue(character, out var e) ? e.Config : null;
    public bool IsRunning(string character) => _entries.TryGetValue(character, out var e) && e.Monitor.IsRunning;
    public string GetStatus(string character) => _entries.TryGetValue(character, out var e) ? e.Monitor.Status : "";

    public void StartMonitor(string character) { if (_entries.TryGetValue(character, out var e)) e.Monitor.Start(); }
    public void StopMonitor(string character) { if (_entries.TryGetValue(character, out var e)) e.Monitor.Stop(); }

    /// <summary>미리보기/영역 지정용 프레임. 백그라운드 스레드에서 호출할 것.</summary>
    internal Frame? Grab(string character)
    {
        if (!_entries.TryGetValue(character, out var e)) return null;
        var s = GetOrCreateSession(e);
        return s != null && !s.IsMinimized ? s.Grab() : null;
    }

    public bool IsMinimized(string character) => _entries.TryGetValue(character, out var e) && e.Session?.IsMinimized == true;

    public void Save()
    {
        lock (_sync)
        {
            _settings.Clients = [.. _entries.Values.Select(e => e.Config)];
            _ctx.Settings.Save(SettingsKey, _settings);
        }
    }

    private WgcSession? GetOrCreateSession(Entry e)
    {
        lock (_sync)
        {
            if (e.Session is { IsAlive: false }) { e.Session.Dispose(); e.Session = null; }
            return e.Session ??= WgcSession.TryCreate(e.Client.Hwnd);
        }
    }

    private void Sync(IReadOnlyList<EveClient> allClients)
    {
        // 같은 캐릭터명의 창이 여러 개면 첫 번째만 쓴다.
        var clients = allClients.GroupBy(c => c.Character, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        lock (_sync)
        {
            var present = clients.ToDictionary(c => c.Character, StringComparer.OrdinalIgnoreCase);

            foreach (var (name, entry) in _entries)
            {
                if (!present.TryGetValue(name, out var c))
                {
                    // 클라이언트 종료: 감시와 세션을 정리하고 설정은 남긴다.
                    entry.Monitor.Stop();
                    entry.Session?.Dispose();
                    _entries.TryRemove(name, out _);
                }
                else if (c.Hwnd != entry.Client.Hwnd)
                {
                    // 재접속으로 창이 바뀜: 세션만 새로 만든다.
                    entry.Session?.Dispose();
                    entry.Session = null;
                    entry.Client = c;
                }
            }

            foreach (var c in clients)
            {
                if (_entries.ContainsKey(c.Character)) continue;
                var cfg = _settings.Clients.FirstOrDefault(x => string.Equals(x.Character, c.Character, StringComparison.OrdinalIgnoreCase))
                          ?? new ClientCaptureConfig { Character = c.Character };
                Entry? entry = null;
                var monitor = new ClientMonitor(c.Character, () => entry is null ? null : GetOrCreateSession(entry),
                    cfg, () => _settings.OutputFolder, _ctx.Events);
                entry = new Entry { Client = c, Config = cfg, Monitor = monitor };
                _entries[c.Character] = entry;
            }
        }
        ClientsUpdated?.Invoke();
    }

    public void Dispose()
    {
        foreach (var d in _subs) d.Dispose();
        foreach (var e in _entries.Values) { e.Monitor.Stop(); e.Session?.Dispose(); }
        _entries.Clear();
        Save();
    }
}
