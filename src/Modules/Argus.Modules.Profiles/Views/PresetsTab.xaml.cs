using System.Windows;
using System.Windows.Controls;

namespace Argus.Modules.Profiles;

public partial class PresetsTab : UserControl, IReloadable
{
    private readonly ProfilesService _svc;
    private Preset? _editing;          // null 이면 새 프리셋
    private bool _loading;
    private List<PresetRow> _rows = [];

    public PresetsTab(ProfilesService svc)
    {
        _svc = svc;
        InitializeComponent();
        _svc.Changed += () => Dispatcher.BeginInvoke(OnServiceChanged);
    }

    public void Reload()
    {
        if (IsDirty()) { OnServiceChanged(); return; }
        ReloadList();
        LoadEditor(_editing);
    }

    private bool IsDirty()
    {
        if (!string.Equals(NameBox.Text.Trim(), _editing?.Name ?? "", StringComparison.Ordinal)) return true;
        var now = _rows.Where(r => r.Included).Select(r => (r.CharId, r.Template?.Id)).OrderBy(x => x.CharId);
        var saved = (_editing?.Entries ?? []).Select(e => (e.CharId, (string?)e.TemplateId)).OrderBy(x => x.CharId);
        return !now.SequenceEqual(saved);
    }

    private void ReloadList()
    {
        _loading = true;
        var selectedId = _editing?.Id;
        var presets = _svc.Data.Presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        PresetList.ItemsSource = presets;
        PresetList.SelectedItem = presets.FirstOrDefault(p => p.Id == selectedId);
        ListEmpty.Visibility = presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    /// <summary>다른 곳(설정파일 탭 등)에서 데이터가 바뀌면 편집 중인 내용을 잃지 않고 설정파일 목록만 새로 넣는다.</summary>
    private void OnServiceChanged()
    {
        ReloadList();
        var templates = _svc.Data.Templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var r in _rows)
        {
            var keepId = r.Template?.Id;
            r.Templates = templates;
            r.Template = templates.FirstOrDefault(t => t.Id == keepId);
        }
        NoTemplates.Visibility = templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 목록 ----

    private void Preset_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetList.SelectedItem is not Preset p) return;
        LoadEditor(p);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _loading = true; PresetList.SelectedItem = null; _loading = false;
        LoadEditor(null);
        NameBox.Focus();
    }

    // ---- 편집기 ----

    private void LoadEditor(Preset? preset)
    {
        _editing = preset;
        _loading = true;
        NameBox.Text = preset?.Name ?? "";
        NameHint.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteBtn.IsEnabled = preset != null;
        SaveBtn.Content = preset == null ? "프리셋 생성" : "프리셋 저장";
        Note.Text = "";
        _loading = false;

        var templates = _svc.Data.Templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        NoTemplates.Visibility = templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 설정 폴더의 캐릭터 + (폴더에 파일이 없어도) 이 프리셋에 이미 들어 있는 캐릭터
        var files = _svc.ListCharFiles();
        var rows = new List<PresetRow>();
        foreach (var f in files)
        {
            var entry = preset?.Entries.FirstOrDefault(x => x.CharId == f.Id);
            rows.Add(new PresetRow
            {
                CharId = f.Id,
                Name = _svc.Names.Display(f.Id),
                Detail = CharRow.Format(f),
                Templates = templates,
                Included = entry != null,
                Template = entry == null ? null : templates.FirstOrDefault(t => t.Id == entry.TemplateId),
            });
        }
        foreach (var entry in preset?.Entries.Where(x => files.All(f => f.Id != x.CharId)) ?? [])
        {
            rows.Add(new PresetRow
            {
                CharId = entry.CharId,
                Name = _svc.Names.Display(entry.CharId),
                Detail = $"ID {entry.CharId}  ·  설정 폴더에 파일 없음 (적용하면 새로 생성)",
                Templates = templates,
                Included = true,
                Template = templates.FirstOrDefault(t => t.Id == entry.TemplateId),
            });
        }
        // 포함된 캐릭터를 위로
        _rows = [.. rows.OrderByDescending(r => r.Included).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        Rows.ItemsSource = _rows;
    }

    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        NameHint.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 저장 / 삭제 ----

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Note.Text = "프리셋 이름을 입력하세요."; return; }

        var same = _svc.FindPresetByName(name);
        if (same != null && same.Id != _editing?.Id) { Note.Text = $"'{name}' 이름의 프리셋이 이미 있습니다."; return; }

        var chosen = _rows.Where(r => r.Included).ToList();
        if (chosen.Count == 0) { Note.Text = "캐릭터를 하나 이상 선택하세요."; return; }
        var missing = chosen.FirstOrDefault(r => r.Template == null);
        if (missing != null) { Note.Text = $"'{missing.Name}' 에 적용할 설정파일을 선택하세요."; return; }

        var preset = _editing ?? new Preset();
        preset.Name = name;
        preset.Entries = [.. chosen.Select(r => new PresetEntry { CharId = r.CharId, TemplateId = r.Template!.Id })];
        _editing = preset;
        _svc.SavePreset(preset);
        Note.Text = $"'{name}' 저장했습니다. ({chosen.Count}명)";
        SaveBtn.Content = "프리셋 저장";
        DeleteBtn.IsEnabled = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        if (MessageBox.Show($"'{_editing.Name}' 프리셋을 삭제할까요?\n(설정파일은 삭제되지 않습니다.)", "프리셋 삭제",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _svc.DeletePreset(_editing.Id);
        LoadEditor(null);
    }
}
