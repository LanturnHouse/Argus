using System.IO;
using System.Text.RegularExpressions;
using Argus.Core.Clients;
using Argus.Core.Events;
using Argus.Core.Modules;

namespace Argus.Modules.CombatLog;

/// <summary>
/// 실행 중인 클라이언트마다 그 캐릭터의 현재 전투 로그를 이어서 읽어 수치를 계산하고, 주기적으로 <see cref="CombatStatsUpdated"/> 를 발행한다.
/// 로그 파일은 읽기만 하며 수정하지 않는다.
/// </summary>
public sealed class CombatLogService : IDisposable
{
    private const string SettingsKey = "combatlog";
    private const int PollMs = 250, PublishMs = 500, RescanMs = 5000, RetryUnknownMs = 30_000;

    private static readonly Regex FileName = new(@"^(\d{8}_\d{6})_(\d+)\.txt$", RegexOptions.Compiled);

    private readonly IModuleContext _ctx;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    // 폴더 색인: 캐릭터 ID → 가장 최근 로그 파일 이름, 캐릭터 ID → (이름, 언어)
    private readonly Dictionary<string, string> _newestByChar = [];
    private readonly Dictionary<string, (string Name, LogLanguage Language)> _names = [];
    private readonly Dictionary<string, long> _unknownRetryAt = [];   // 이름을 못 찾은 캐릭터를 다시 찾아볼 시각

    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);   // 캐릭터 이름 → 읽고 있는 파일과 수치
    private readonly Dictionary<string, CombatSnapshot> _latest = new(StringComparer.OrdinalIgnoreCase);   // 대시보드가 읽는 가장 최근 수치
    private long _lastRescan, _lastPublish;
    private bool _rescanNow = true;
    private IDisposable? _sub;

    public CombatLogSettings Settings { get; }

    /// <summary>이 캐릭터의 가장 최근 수치 (읽고 있지 않으면 null).</summary>
    public CombatSnapshot? Latest(string character) { lock (_lock) return _latest.GetValueOrDefault(character); }

    /// <summary>지금 로그를 읽고 있는 캐릭터 수.</summary>
    public int TrackedCount { get { lock (_lock) return _tracked.Count; } }

    private sealed class Tracked(string charId, LogTailer tailer, CharacterStats stats)
    {
        public string CharId = charId;
        public LogTailer Tailer = tailer;
        public CharacterStats Stats = stats;
        public long Lines, Events, LastEventAt;
    }

    public CombatLogService(IModuleContext ctx)
    {
        _ctx = ctx;
        Settings = ctx.Settings.Load<CombatLogSettings>(SettingsKey);
        Clamp();
    }

    private void Clamp()
    {
        Settings.WindowSeconds = Math.Clamp(Settings.WindowSeconds, CombatLogSettings.MinWindow, CombatLogSettings.MaxWindow);
        Settings.SurgeMinDps = Math.Clamp(Settings.SurgeMinDps, CombatLogSettings.MinSurgeDps, CombatLogSettings.MaxSurgeDps);
        Settings.SurgeRatio = Math.Clamp(Settings.SurgeRatio, CombatLogSettings.MinSurgeRatio, CombatLogSettings.MaxSurgeRatio);
    }

    public string FolderPath => string.IsNullOrWhiteSpace(Settings.LogFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Gamelogs")
        : Settings.LogFolder;

    public void Start()
    {
        _sub = _ctx.Events.Subscribe<ClientsChanged>(_ => _rescanNow = true);
        _ = Task.Run(LoopAsync);
    }

    // ---------- 설정 ----------

    public void Update(int? windowSeconds = null, string? logFolder = null, int? surgeMinDps = null, double? surgeRatio = null)
    {
        if (surgeMinDps is { } sd) Settings.SurgeMinDps = sd;
        if (surgeRatio is { } sr) Settings.SurgeRatio = sr;
        if (windowSeconds is { } w) Settings.WindowSeconds = w;
        if (logFolder != null)
        {
            Settings.LogFolder = logFolder;
            lock (_lock) { DropAll(); }
            _rescanNow = true;
        }
        Clamp();
        _ctx.Settings.Save(SettingsKey, Settings);
    }

    /// <summary>설정 화면에 보여줄 현재 상태: 캐릭터, 읽고 있는 파일, 읽은 줄/전투 사건 수.</summary>
    public List<(string Character, string File, long Lines, long Events, DateTime? LastEvent)> Status()
    {
        lock (_lock)
            return [.. _tracked.Select(kv => (kv.Key, System.IO.Path.GetFileName(kv.Value.Tailer.Path), kv.Value.Lines, kv.Value.Events,
                kv.Value.LastEventAt == 0 ? (DateTime?)null : DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64 - kv.Value.LastEventAt)))];
    }

    // ---------- 주기 작업 ----------

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                try { Tick(Environment.TickCount64); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[CombatLog] {ex}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void Tick(long now)
    {
        lock (_lock)
        {
            if (_rescanNow || now - _lastRescan >= RescanMs) { _rescanNow = false; _lastRescan = now; Retarget(now); }

            foreach (var t in _tracked.Values)
                foreach (var line in t.Tailer.ReadNewLines())
                {
                    t.Lines++;
                    if (CombatLogParser.Parse(line, t.Tailer.Language) is { } e) { t.Stats.Add(e, now); t.Events++; t.LastEventAt = now; }
                }

            if (now - _lastPublish >= PublishMs && _tracked.Count > 0)
            {
                _lastPublish = now;
                var snaps = _tracked.Values.Select(t => t.Stats.Snapshot(now, Settings.WindowSeconds, Settings.SurgeMinDps, Settings.SurgeRatio)).ToList();
                _latest.Clear();
                foreach (var s in snaps) _latest[s.Character] = s;
                _ctx.Events.Publish(new CombatStatsUpdated(snaps));
            }
        }
    }

    /// <summary>실행 중인 캐릭터마다 지금 읽어야 할 로그 파일을 정하고, 파일이 바뀌었거나 새로 필요하면 읽기 시작한다.</summary>
    private void Retarget(long now)
    {
        var running = _ctx.Clients.Current.Select(c => c.Character).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 종료된 클라이언트는 더 읽지 않는다.
        foreach (var name in _tracked.Keys.Where(n => !running.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _tracked[name].Tailer.Dispose();
            _tracked.Remove(name);
        }

        var folder = FolderPath;
        if (!Directory.Exists(folder)) return;
        IndexFolder(folder);

        foreach (var character in running)
        {
            var charId = FindCharId(folder, character, now);
            if (charId == null || !_newestByChar.TryGetValue(charId, out var file)) continue;
            var path = Path.Combine(folder, file);

            if (_tracked.TryGetValue(character, out var cur))
            {
                if (string.Equals(cur.Tailer.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                cur.Tailer.Dispose();   // 새 세션 파일로 넘어감: 처음부터 읽는다 (수치는 이어서 유지)
                if (TryOpen(path, character, fromEnd: false, out var lang, out var tailer)) { cur.Tailer = tailer!; cur.CharId = charId; }
                else _tracked.Remove(character);
            }
            else if (TryOpen(path, character, fromEnd: true, out var language, out var t))   // 처음 붙을 때는 이미 지나간 전투는 건너뛴다
                _tracked[character] = new Tracked(charId, t!, new CharacterStats(character));
        }
    }

    private bool TryOpen(string path, string character, bool fromEnd, out LogLanguage language, out LogTailer? tailer)
    {
        language = LogLanguage.Korean; tailer = null;
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                for (int i = 0; i < 12 && sr.ReadLine() is { } l; i++)
                    if (CombatLogParser.TryParseHeader(l, out _, out language)) break;
            tailer = new LogTailer(path, language, fromEnd);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>폴더의 파일 이름만 훑어 캐릭터 ID 별 가장 최근 로그를 찾는다 (파일 이름이 UTC 시각이라 이름순 = 시간순).</summary>
    private void IndexFolder(string folder)
    {
        _newestByChar.Clear();
        foreach (var path in Directory.EnumerateFiles(folder, "*.txt"))
        {
            var name = Path.GetFileName(path);
            var m = FileName.Match(name);
            if (!m.Success) continue;
            var id = m.Groups[2].Value;
            if (!_newestByChar.TryGetValue(id, out var best) || string.CompareOrdinal(name, best) > 0) _newestByChar[id] = name;
        }
    }

    /// <summary>캐릭터 이름 → 캐릭터 ID. 파일 머리글의 '청취자'로 알아내며, 한 번 알아낸 것은 기억한다.</summary>
    private string? FindCharId(string folder, string character, long now)
    {
        string? Known() => _names.FirstOrDefault(kv => string.Equals(kv.Value.Name, character, StringComparison.OrdinalIgnoreCase)).Key;
        if (Known() is { } id) return id;
        if (_unknownRetryAt.TryGetValue(character, out var retry) && now < retry) return null;

        // 아직 이름을 모르는 캐릭터 ID 들의 최근 파일 머리글을 읽는다 (한 번만).
        foreach (var (charId, file) in _newestByChar)
        {
            if (_names.ContainsKey(charId)) continue;
            try
            {
                using var fs = new FileStream(Path.Combine(folder, file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                for (int i = 0; i < 12 && sr.ReadLine() is { } l; i++)
                    if (CombatLogParser.TryParseHeader(l, out var name, out var lang)) { _names[charId] = (name, lang); break; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (Known() is { } found) return found;
        _unknownRetryAt[character] = now + RetryUnknownMs;
        return null;
    }

    private void DropAll()
    {
        foreach (var t in _tracked.Values) t.Tailer.Dispose();
        _tracked.Clear(); _names.Clear(); _newestByChar.Clear(); _unknownRetryAt.Clear();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _sub?.Dispose();
        lock (_lock) DropAll();
    }
}
