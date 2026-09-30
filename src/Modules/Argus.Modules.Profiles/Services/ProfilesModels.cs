namespace Argus.Modules.Profiles;

/// <summary>Argus 안에 복사해 둔 캐릭터 설정파일 하나. 프리셋은 이름이 아니라 Id 로 참조한다.</summary>
public sealed class TemplateInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public long SourceCharId { get; set; }
    public string SourceCharName { get; set; } = "";
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public long Size { get; set; }

    public override string ToString() => Name; // ComboBox 표시용
}

/// <summary>프리셋의 한 줄: 이 캐릭터에는 이 설정파일을 적용한다.</summary>
public sealed class PresetEntry
{
    public long CharId { get; set; }
    public string TemplateId { get; set; } = "";
}

public sealed class Preset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<PresetEntry> Entries { get; set; } = [];

    public override string ToString() => Name;
}

public sealed class ProfilesData
{
    /// <summary>선택한 EVE 설정 폴더. 비어 있으면 자동 탐지 결과를 쓴다.</summary>
    public string SettingsFolder { get; set; } = "";
    public List<TemplateInfo> Templates { get; set; } = [];
    public List<Preset> Presets { get; set; } = [];
}

public sealed class NamesData
{
    public Dictionary<long, string> Names { get; set; } = [];
}

/// <summary>EVE 설정 폴더의 core_char_{ID}.dat 하나.</summary>
public sealed record CharFile(long Id, string Path, long Size, DateTime Modified);

public sealed record EveSettingsFolder(string Path, string Label);

public enum RunState { NotRunning, Running, Unknown }

/// <summary>적용 결과 한 줄. Ok 면 "O 이름", 아니면 "X 이름 | 사유".</summary>
public sealed record ApplyResult(long CharId, string Name, bool Ok, string Reason);

public sealed class BackupItem
{
    public long CharId { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>덮어쓰기 전에 원본 파일이 있었는가. false 면 되돌릴 때 새로 만든 파일을 지운다.</summary>
    public bool Existed { get; set; }
}

public sealed class BackupManifest
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string PresetName { get; set; } = "";
    public string SettingsFolder { get; set; } = "";
    public List<BackupItem> Items { get; set; } = [];
}
