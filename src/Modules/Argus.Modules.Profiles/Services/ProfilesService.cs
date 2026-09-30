using System.IO;
using System.Text.Json;
using Argus.Core.Settings;

namespace Argus.Modules.Profiles;

/// <summary>
/// 설정파일(템플릿) 저장, 프리셋 관리, 프리셋 적용/되돌리기.
/// EVE 폴더는 "선택한 캐릭터 파일 읽기"와 "적용 시 덮어쓰기"에만 접근한다.
/// </summary>
public sealed class ProfilesService
{
    private const string SettingsKey = "profiles";
    private const int KeepBackups = 20;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ISettingsStore _store;
    private readonly string _dataDir;
    private readonly Func<IReadOnlyList<string>> _runningNames;
    private readonly object _lock = new();

    public ProfilesService(ISettingsStore store, string dataDir, EsiNames names, Func<IReadOnlyList<string>> runningCharacterNames)
    {
        _store = store;
        _dataDir = dataDir;
        Names = names;
        _runningNames = runningCharacterNames;
        Data = store.Load<ProfilesData>(SettingsKey);
        Directory.CreateDirectory(TemplatesDir);
        Directory.CreateDirectory(BackupsDir);
    }

    public EsiNames Names { get; }
    public ProfilesData Data { get; }

    /// <summary>템플릿/프리셋이 바뀌었을 때 (UI 갱신용).</summary>
    public event Action? Changed;

    private string TemplatesDir => Path.Combine(_dataDir, "templates");
    private string BackupsDir => Path.Combine(_dataDir, "backups");
    public string TemplatePath(string templateId) => Path.Combine(TemplatesDir, templateId + ".dat");

    // ---------- EVE 설정 폴더 ----------

    public List<EveSettingsFolder> DiscoverFolders()
    {
        var found = EveSettingsLocator.Discover();
        // 직접 지정한 폴더가 목록에 없으면 추가한다.
        if (!string.IsNullOrEmpty(Data.SettingsFolder) && Directory.Exists(Data.SettingsFolder)
            && found.All(f => !SamePath(f.Path, Data.SettingsFolder)))
            found.Add(new EveSettingsFolder(Data.SettingsFolder, "직접 지정 / " + Path.GetFileName(Data.SettingsFolder)));
        return found;
    }

    /// <summary>현재 사용할 EVE 설정 폴더. 저장된 선택 → 자동 탐지 순.</summary>
    public string? SettingsFolder
    {
        get
        {
            if (!string.IsNullOrEmpty(Data.SettingsFolder) && Directory.Exists(Data.SettingsFolder)) return Data.SettingsFolder;
            return EveSettingsLocator.PickDefault(EveSettingsLocator.Discover())?.Path;
        }
    }

    public void SetSettingsFolder(string path)
    {
        Data.SettingsFolder = path;
        Save();
    }

    public List<CharFile> ListCharFiles() => SettingsFolder is { } f ? EveSettingsLocator.ListCharFiles(f) : [];

    // ---------- 실행 중 판정 ----------

    /// <summary>
    /// 이 캐릭터의 EVE 클라이언트가 실행 중인가. 창 제목의 이름과 ESI 이름을 대조한다.
    /// 클라이언트가 실행 중인데 이 ID 의 이름을 모르면 안전을 위해 Unknown 으로 본다.
    /// </summary>
    public RunState GetRunState(long charId)
    {
        var running = _runningNames();
        if (running.Count == 0) return RunState.NotRunning;
        var name = Names.Get(charId);
        if (name == null) return RunState.Unknown;
        return running.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ? RunState.Running : RunState.NotRunning;
    }

    // ---------- 설정파일(템플릿) ----------

    public TemplateInfo? FindTemplateByName(string name) =>
        Data.Templates.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public TemplateInfo? GetTemplate(string id) => Data.Templates.FirstOrDefault(t => t.Id == id);

    /// <summary>EVE 폴더의 캐릭터 파일을 Argus 안에 복사해 저장한다. 같은 이름이 있으면 그 설정파일을 갱신한다(Id 유지).</summary>
    public TemplateInfo SaveTemplate(string name, CharFile source)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("설정파일 이름을 입력하세요.");

        var existing = FindTemplateByName(name);
        var info = existing ?? new TemplateInfo { Name = name };
        var dest = TemplatePath(info.Id);
        var tmp = dest + ".tmp";
        File.Copy(source.Path, tmp, overwrite: true);
        File.Move(tmp, dest, overwrite: true);

        info.Name = name;
        info.SourceCharId = source.Id;
        info.SourceCharName = Names.Display(source.Id);
        info.SavedAt = DateTime.Now;
        info.Size = new FileInfo(dest).Length;
        if (existing == null) Data.Templates.Add(info);
        Save();
        return info;
    }

    /// <summary>이 설정파일을 쓰는 프리셋 이름들.</summary>
    public List<string> PresetsUsing(string templateId) =>
        [.. Data.Presets.Where(p => p.Entries.Any(e => e.TemplateId == templateId)).Select(p => p.Name)];

    /// <summary>삭제한다. 프리셋이 쓰고 있으면 삭제하지 않고 그 프리셋 이름들을 반환한다.</summary>
    public List<string> DeleteTemplate(string templateId)
    {
        var used = PresetsUsing(templateId);
        if (used.Count > 0) return used;
        var t = GetTemplate(templateId);
        if (t == null) return [];
        Data.Templates.Remove(t);
        try { File.Delete(TemplatePath(templateId)); } catch { /* 파일이 이미 없어도 무시 */ }
        Save();
        return [];
    }

    // ---------- 프리셋 ----------

    public Preset? FindPresetByName(string name) =>
        Data.Presets.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>새 프리셋을 추가하거나(Id 가 없으면) 기존 프리셋을 갱신한다.</summary>
    public void SavePreset(Preset preset)
    {
        preset.Name = preset.Name.Trim();
        var i = Data.Presets.FindIndex(p => p.Id == preset.Id);
        if (i >= 0) Data.Presets[i] = preset; else Data.Presets.Add(preset);
        Save();
    }

    public void DeletePreset(string presetId)
    {
        Data.Presets.RemoveAll(p => p.Id == presetId);
        Save();
    }

    // ---------- 적용 ----------

    /// <summary>
    /// 프리셋을 EVE 설정 폴더에 덮어쓴다. onlyCharIds 를 주면 그 캐릭터만 처리한다(실패 항목 재시도용).
    /// 실행 중인 캐릭터는 건너뛰고 X 로 보고한다. 덮어쓰기 전에 원본을 백업한다.
    /// </summary>
    public List<ApplyResult> Apply(Preset preset, IReadOnlyCollection<long>? onlyCharIds = null)
    {
        var results = new List<ApplyResult>();
        var folder = SettingsFolder;
        var manifest = new BackupManifest { PresetName = preset.Name, SettingsFolder = folder ?? "" };
        var backupDir = Path.Combine(BackupsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));

        foreach (var entry in preset.Entries)
        {
            if (onlyCharIds != null && !onlyCharIds.Contains(entry.CharId)) continue;
            var name = Names.Display(entry.CharId);

            if (folder == null) { results.Add(new(entry.CharId, name, false, "EVE 설정 폴더를 찾을 수 없음")); continue; }
            var template = GetTemplate(entry.TemplateId);
            if (template == null || !File.Exists(TemplatePath(template.Id)))
            { results.Add(new(entry.CharId, name, false, "설정파일을 찾을 수 없음")); continue; }

            switch (GetRunState(entry.CharId)) // 캐릭터마다 쓰기 직전에 확인한다
            {
                case RunState.Running: results.Add(new(entry.CharId, name, false, "실행 중 (건너뜀)")); continue;
                case RunState.Unknown: results.Add(new(entry.CharId, name, false, "실행 여부 확인 불가 (이름 조회 필요)")); continue;
            }

            var dest = EveSettingsLocator.CharFilePath(folder, entry.CharId);
            try
            {
                var existed = File.Exists(dest);
                var backupName = $"core_char_{entry.CharId}.dat";
                if (existed)
                {
                    Directory.CreateDirectory(backupDir);
                    File.Copy(dest, Path.Combine(backupDir, backupName), overwrite: true);
                }
                WriteAtomic(TemplatePath(template.Id), dest);
                manifest.Items.Add(new BackupItem { CharId = entry.CharId, FileName = backupName, Existed = existed });
                results.Add(new(entry.CharId, name, true, ""));
            }
            catch (Exception ex)
            {
                results.Add(new(entry.CharId, name, false, "쓰기 실패: " + ex.Message));
            }
        }

        if (manifest.Items.Count > 0)
        {
            Directory.CreateDirectory(backupDir);
            File.WriteAllText(Path.Combine(backupDir, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
            PruneBackups();
        }
        return results;
    }

    /// <summary>원자적 덮어쓰기: 같은 폴더의 임시 파일에 먼저 복사한 뒤 교체한다. 중간에 실패해도 원본은 그대로다.</summary>
    private static void WriteAtomic(string source, string dest)
    {
        var tmp = dest + ".argus-tmp";
        try
        {
            File.Copy(source, tmp, overwrite: true);
            if (new FileInfo(tmp).Length != new FileInfo(source).Length) throw new IOException("복사 크기 불일치");
            File.Move(tmp, dest, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 임시 파일 정리 실패는 무시 */ }
        }
    }

    // ---------- 되돌리기 ----------

    private string? LatestBackupDir() =>
        Directory.Exists(BackupsDir)
            ? Directory.GetDirectories(BackupsDir).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).OrderDescending().FirstOrDefault()
            : null;

    public BackupManifest? LastBackup()
    {
        var dir = LatestBackupDir();
        return dir == null ? null : JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));
    }

    public bool CanUndo => LatestBackupDir() != null;

    /// <summary>직전 적용을 되돌린다. 실행 중인 캐릭터는 건너뛰고 남겨 두어 나중에 다시 시도할 수 있다.</summary>
    public List<ApplyResult> UndoLast()
    {
        var results = new List<ApplyResult>();
        var dir = LatestBackupDir();
        if (dir == null) return results;
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")))!;

        var remaining = new List<BackupItem>();
        foreach (var item in manifest.Items)
        {
            var name = Names.Display(item.CharId);
            switch (GetRunState(item.CharId))
            {
                case RunState.Running: results.Add(new(item.CharId, name, false, "실행 중 (건너뜀)")); remaining.Add(item); continue;
                case RunState.Unknown: results.Add(new(item.CharId, name, false, "실행 여부 확인 불가 (이름 조회 필요)")); remaining.Add(item); continue;
            }

            try
            {
                var dest = EveSettingsLocator.CharFilePath(manifest.SettingsFolder, item.CharId);
                if (item.Existed) WriteAtomic(Path.Combine(dir, item.FileName), dest);
                else if (File.Exists(dest)) File.Delete(dest); // 적용 때 새로 만든 파일
                results.Add(new(item.CharId, name, true, ""));
            }
            catch (Exception ex)
            {
                results.Add(new(item.CharId, name, false, "복원 실패: " + ex.Message));
                remaining.Add(item);
            }
        }

        if (remaining.Count == 0) Directory.Delete(dir, recursive: true);
        else
        {
            manifest.Items = remaining; // 실패/보류 항목만 남긴다
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        }
        return results;
    }

    private void PruneBackups()
    {
        var dirs = Directory.GetDirectories(BackupsDir).OrderDescending().Skip(KeepBackups);
        foreach (var d in dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* 사용 중이면 다음에 정리 */ }
        }
    }

    // ---------- 저장 ----------

    private void Save()
    {
        lock (_lock) _store.Save(SettingsKey, Data);
        Changed?.Invoke();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
