using System.ComponentModel;

namespace Argus.Modules.Profiles;

/// <summary>EVE 설정 폴더의 캐릭터 파일 한 줄.</summary>
public sealed class CharRow
{
    public required CharFile File { get; init; }
    public long Id => File.Id;
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>"실행 중" / "실행 여부 확인 불가" / "".</summary>
    public string State { get; init; } = "";
    /// <summary>파일이 유난히 작을 때의 경고.</summary>
    public string Warn { get; init; } = "";

    public static string Format(ProfilesService svc, CharFile f)
    {
        var kb = f.Size / 1024.0;
        return $"ID {f.Id}  ·  {kb:N1} KB  ·  {f.Modified:MM-dd HH:mm}";
    }

    public static string StateText(RunState s) => s switch
    {
        RunState.Running => "실행 중",
        RunState.Unknown => "실행 여부 확인 불가",
        _ => "",
    };

    /// <summary>정상 설정파일은 100KB 안팎이다. 훨씬 작으면 설정이 덜 된 캐릭터일 수 있다.</summary>
    public static string WarnText(CharFile f) => f.Size < 20 * 1024 ? "파일이 작습니다 (설정이 덜 된 캐릭터일 수 있음)" : "";
}

public sealed class TemplateRow
{
    public required TemplateInfo Info { get; init; }
    public string Name => Info.Name;
    public string Detail => $"원본 {Info.SourceCharName}  ·  {Info.Size / 1024.0:N1} KB  ·  {Info.SavedAt:MM-dd HH:mm} 저장";
}

/// <summary>프리셋 편집기의 캐릭터 한 줄: 포함 여부 + 적용할 설정파일.</summary>
public sealed class PresetRow : INotifyPropertyChanged
{
    private bool _included;
    private TemplateInfo? _template;
    private IReadOnlyList<TemplateInfo> _templates = [];

    public required long CharId { get; init; }
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";

    public bool Included { get => _included; set { _included = value; Raise(nameof(Included)); } }
    public TemplateInfo? Template { get => _template; set { _template = value; Raise(nameof(Template)); } }
    public IReadOnlyList<TemplateInfo> Templates { get => _templates; set { _templates = value; Raise(nameof(Templates)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>적용 탭의 사전 확인 한 줄.</summary>
public sealed class PlanRow
{
    public required long CharId { get; init; }
    public string Name { get; init; } = "";
    public string TemplateName { get; init; } = "";
    public string Status { get; init; } = "";
    public bool Ok { get; init; }
}

public sealed class ResultRow(ApplyResult r)
{
    public bool Ok { get; } = r.Ok;
    public long CharId { get; } = r.CharId;
    public string Mark { get; } = r.Ok ? "O" : "X";
    public string Name { get; } = r.Name;
    public string Reason { get; } = r.Ok ? "" : "| " + r.Reason;
}
