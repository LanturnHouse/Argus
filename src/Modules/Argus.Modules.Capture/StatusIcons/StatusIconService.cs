using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Argus.Core.Clients;
using Argus.Core.Events;
using Argus.Core.Modules;
using Argus.Core.Settings;
using Argus.Modules.Capture.Native;

namespace Argus.Modules.Capture.StatusIcons;

/// <summary>설정 화면의 '지금 어떻게 읽히는지' 확인용: 마지막으로 읽은 조각과 찾아낸 아이콘.</summary>
internal sealed record StatusIconProbe(string Status, RegionFrame? Frame, IReadOnlyList<DetectedIcon> Icons, TackleIconState? State);

/// <summary>
/// 클라이언트 화면 아래 가운데의 상태이상 아이콘 줄을 주기적으로 읽어 태클 상태(디스럽터·스크램블·HIC 포인팅)를 알린다.
/// 로그는 태클이 걸린 순간 한 줄만 남기지만, 아이콘은 걸려 있는 동안 계속 떠 있으므로 유지와 풀림을 정확히 알 수 있다.
/// 창 캡처(WGC)로 읽기 때문에 다른 창에 가려져 있어도 되고, 최소화된 클라이언트는 읽지 못한다.
/// </summary>
public sealed class StatusIconService : IDisposable
{
    internal const string SettingsKey = "statusicons";

    private sealed class Debounce
    {
        public bool On;
        private int _run;

        /// <summary>보였는가/안 보였는가를 넣으면 지금 켜져 있어야 하는지를 돌려준다. 반대 상태가 연속으로 충분히 이어져야 바뀐다.</summary>
        public bool Update(bool seen, int onFrames, int offFrames)
        {
            if (seen == On) { _run = 0; return On; }
            if (++_run >= (seen ? onFrames : offFrames)) { On = seen; _run = 0; }
            return On;
        }
    }

    private sealed class Entry
    {
        public required EveClient Client;
        public WgcSession? Session;
        public readonly Debounce[] Kinds = [new(), new(), new()];
        public StatusIconProbe Probe = new("대기", null, [], null);
        public long StateAt;   // 마지막으로 태클 상태를 읽은 시각 (TickCount64)
    }

    private readonly IModuleContext _ctx;
    private readonly IconDetector _detector;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private IDisposable? _modeSub;
    private volatile bool _combatMode;   // 비전투 모드에서는 화면을 읽지 않는다 (캡처 세션도 닫는다)

    internal StatusIconSettings Settings { get; }
    internal bool CombatMode => _combatMode;

    public StatusIconService(IModuleContext ctx)
    {
        _ctx = ctx;
        Settings = ctx.Settings.Load<StatusIconSettings>(SettingsKey);
        Settings.Normalize();
        _detector = new IconDetector(LoadReferences());
        _combatMode = Core.Settings.CombatMode.Load(ctx.Settings);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _modeSub = _ctx.Events.Subscribe<CombatModeChanged>(e => _combatMode = e.Active);
        _ = Task.Run(() => LoopAsync(ct));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _modeSub?.Dispose();
        lock (_lock)
        {
            foreach (var e in _entries.Values) e.Session?.Dispose();
            _entries.Clear();
        }
    }

    internal void Save() => _ctx.Settings.Save(SettingsKey, Settings);

    /// <summary>지금 읽고 있는 캐릭터 이름들.</summary>
    internal IReadOnlyList<string> Characters { get { lock (_lock) return [.. _entries.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)]; } }

    /// <summary>이 캐릭터의 가장 최근 태클 상태 (최근 2초 안에 읽은 것만, 아니면 null).</summary>
    internal TackleIconState? Latest(string character)
    {
        lock (_lock) return _entries.TryGetValue(character, out var e) && Environment.TickCount64 - e.StateAt <= 2000 ? e.Probe.State : null;
    }

    internal StatusIconProbe? Probe(string character) { lock (_lock) return _entries.TryGetValue(character, out var e) ? e.Probe : null; }

    // ---------- 읽기 ----------

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[StatusIcons] {ex.Message}"); }

            try { await Task.Delay(_combatMode ? 1000 / Math.Clamp(Settings.Hz, StatusIconSettings.MinHz, StatusIconSettings.MaxHz) : 500, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_combatMode)
        {
            // 비전투 모드: 열어 둔 캡처 세션을 모두 닫고 아무것도 읽지 않는다.
            lock (_lock)
                foreach (var e in _entries.Values)
                {
                    e.Session?.Dispose(); e.Session = null;
                    e.Probe = new("비전투 모드 (읽지 않음)", null, [], null);
                }
            return;
        }
        SyncClients();
        var states = new List<TackleIconState>();

        List<Entry> entries;
        lock (_lock) entries = [.. _entries.Values];
        foreach (var e in entries)
        {
            if (!Settings.Enabled) { e.Probe = new("꺼짐", null, [], null); continue; }
            var state = Read(e);
            if (state != null) states.Add(state);
        }

        if (states.Count > 0) _ctx.Events.Publish(new TackleIconsUpdated(states));
    }

    private void SyncClients()
    {
        var clients = _ctx.Clients.Current.GroupBy(c => c.Character, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        lock (_lock)
        {
            foreach (var name in _entries.Keys.Where(n => clients.All(c => !string.Equals(c.Character, n, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _entries[name].Session?.Dispose();
                _entries.Remove(name);
            }
            foreach (var c in clients)
            {
                if (!_entries.TryGetValue(c.Character, out var e)) _entries[c.Character] = new Entry { Client = c };
                else if (e.Client.Hwnd != c.Hwnd) { e.Session?.Dispose(); e.Session = null; e.Client = c; }   // 재접속으로 창이 바뀜
            }
        }
    }

    private TackleIconState? Read(Entry e)
    {
        var s = Settings;
        if (e.Session is { IsAlive: false }) { e.Session.Dispose(); e.Session = null; }
        e.Session ??= WgcSession.TryCreate(e.Client.Hwnd);
        if (e.Session == null) { e.Probe = new("캡처 세션을 만들지 못했습니다", null, [], null); return null; }
        if (e.Session.IsMinimized) { e.Probe = new("창이 최소화되어 읽지 못합니다", e.Probe.Frame, e.Probe.Icons, null); return null; }

        var frame = e.Session.GrabRegion(s.RegionFor);
        if (frame == null) { e.Probe = new("프레임 대기 중", null, [], null); return null; }

        var icons = _detector.Detect(frame.Bgra, frame.Width, frame.Height, s.MinRadius, s.MaxRadius, s.ShapeThreshold);
        bool Seen(TackleKind k) => icons.Any(i => i.Kind == k);
        var state = new TackleIconState(e.Client.Character,
            Disrupt: e.Kinds[(int)TackleKind.Disrupt].Update(Seen(TackleKind.Disrupt), s.OnFrames, s.OffFrames),
            Scram: e.Kinds[(int)TackleKind.Scram].Update(Seen(TackleKind.Scram), s.OnFrames, s.OffFrames),
            Hic: e.Kinds[(int)TackleKind.Hic].Update(Seen(TackleKind.Hic), s.OnFrames, s.OffFrames));

        e.Probe = new StatusIconProbe($"읽는 중 · 아이콘 {icons.Count}개", frame, icons, state);
        e.StateAt = Environment.TickCount64;
        return state;
    }

    // ---------- 기준 아이콘 (내장 그림) ----------

    private static List<(TackleKind, byte[], int, int)> LoadReferences()
    {
        var list = new List<(TackleKind, byte[], int, int)>();
        foreach (var (kind, file) in new[] { (TackleKind.Disrupt, "disrupt"), (TackleKind.Hic, "hic"), (TackleKind.Scram, "scram") })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Argus.Modules.Capture.StatusIcons.Refs.{file}.png")
                               ?? throw new InvalidOperationException($"기준 아이콘 {file} 을 찾을 수 없습니다");
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bmp = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
            list.Add((kind, pixels, bmp.PixelWidth, bmp.PixelHeight));
        }
        return list;
    }
}
