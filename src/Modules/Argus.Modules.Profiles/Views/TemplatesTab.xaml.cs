using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Argus.Modules.Profiles;

public partial class TemplatesTab : UserControl, IReloadable
{
    private readonly ProfilesService _svc;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public TemplatesTab(ProfilesService svc)
    {
        _svc = svc;
        InitializeComponent();
        _svc.Changed += () => Dispatcher.BeginInvoke(Reload);  // 폴더/설정파일이 바뀌면 전체 갱신
        _timer.Tick += (_, _) => ReloadChars(keepSelection: true);
        Loaded += (_, _) => _timer.Start();   // 실행 중 표시를 주기적으로 갱신
        Unloaded += (_, _) => _timer.Stop();
    }

    public void Reload()
    {
        ReloadFolders();
        ReloadChars(keepSelection: true);
        ReloadTemplates();
    }

    // ---- EVE 설정 폴더 (변경은 설정 페이지에서) ----

    private void ReloadFolders()
    {
        var current = _svc.SettingsFolder;
        FolderLabel.Text = current ?? "EVE 설정 폴더를 찾지 못했습니다. 설정 페이지에서 지정하세요.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    // ---- 캐릭터 목록 ----

    private void ReloadChars(bool keepSelection)
    {
        var selectedId = keepSelection ? (CharList.SelectedItem as CharRow)?.Id : null;
        var rows = _svc.ListCharFiles().Select(f => new CharRow
        {
            File = f,
            Name = _svc.Names.Display(f.Id),
            Detail = CharRow.Format(_svc, f),
            State = CharRow.StateText(_svc.GetRunState(f.Id)),
            Warn = CharRow.WarnText(f),
        }).ToList();

        // 2초마다 갱신되므로 내용이 같으면 다시 그리지 않는다(선택/스크롤 유지).
        if (CharList.ItemsSource is List<CharRow> old && old.Count == rows.Count
            && old.Zip(rows).All(p => p.First.Id == p.Second.Id && p.First.State == p.Second.State && p.First.Name == p.Second.Name && p.First.Detail == p.Second.Detail))
            return;

        CharList.ItemsSource = rows;
        CharEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedId != null) CharList.SelectedItem = rows.FirstOrDefault(r => r.Id == selectedId);
        UpdateForm();
    }

    private void Char_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (CharList.SelectedItem is CharRow r && string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = r.Name.StartsWith("ID ") ? "" : r.Name; // 이름 입력 편의: 캐릭터 이름을 기본값으로
        UpdateForm();
    }

    private void UpdateForm()
    {
        var r = CharList.SelectedItem as CharRow;
        SelectedText.Text = r == null ? "왼쪽에서 캐릭터를 선택하세요." : $"선택: {r.Name}  ({r.Detail})";
        SaveBtn.IsEnabled = r != null && !string.IsNullOrWhiteSpace(NameBox.Text);
        SaveNote.Text = r?.State == "실행 중"
            ? "이 캐릭터는 실행 중입니다. 저장되는 내용은 마지막으로 디스크에 기록된 시점의 파일입니다."
            : r?.Warn is { Length: > 0 } w ? w : "";
    }

    // ---- 저장 ----

    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        NameHint.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateForm();
    }

    private void Name_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SaveBtn.IsEnabled) Save_Click(sender, e);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CharList.SelectedItem is not CharRow row) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;

        var existing = _svc.FindTemplateByName(name);
        if (existing != null)
        {
            var used = _svc.PresetsUsing(existing.Id);
            var msg = $"'{existing.Name}' 이름의 설정파일이 이미 있습니다. 새 내용으로 갱신할까요?"
                    + (used.Count > 0 ? $"\n\n이 설정파일을 쓰는 프리셋에도 모두 반영됩니다:\n· {string.Join("\n· ", used)}" : "");
            if (MessageBox.Show(msg, "설정파일 갱신", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }

        try
        {
            _svc.SaveTemplate(name, row.File);
            NameBox.Clear();
            SaveNote.Text = $"'{name}' 저장했습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("저장하지 못했습니다.\n" + ex.Message, "설정파일 저장", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- 저장된 설정파일 ----

    private void ReloadTemplates()
    {
        var rows = _svc.Data.Templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TemplateRow { Info = t }).ToList();
        TemplateList.ItemsSource = rows;
        TemplateEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TemplateCount.Text = rows.Count == 0 ? "" : $"{rows.Count}개";
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TemplateInfo t }) return;
        if (MessageBox.Show($"'{t.Name}' 설정파일을 삭제할까요?", "삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var used = _svc.DeleteTemplate(t.Id);
        if (used.Count > 0)
            MessageBox.Show($"다음 프리셋이 이 설정파일을 사용 중이라 삭제할 수 없습니다.\n\n· {string.Join("\n· ", used)}\n\n프리셋에서 먼저 바꾸거나 삭제하세요.",
                "삭제할 수 없음", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
