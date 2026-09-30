using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Argus.Modules.Profiles;

public partial class ApplyTab : UserControl, IReloadable
{
    private readonly ProfilesService _svc;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Preset? _selected;
    private List<long> _failed = [];

    public ApplyTab(ProfilesService svc)
    {
        _svc = svc;
        InitializeComponent();
        _svc.Changed += () => Dispatcher.BeginInvoke(Reload);
        _timer.Tick += (_, _) => RefreshPlan();   // 실행 중 표시를 주기적으로 갱신
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public void Reload()
    {
        var selectedId = _selected?.Id;
        var presets = _svc.Data.Presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        PresetList.ItemsSource = presets;
        ListEmpty.Visibility = presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PresetList.SelectedItem = presets.FirstOrDefault(p => p.Id == selectedId);
        _selected = PresetList.SelectedItem as Preset;
        RefreshPlan();
        UpdateUndo();
    }

    private void Preset_Selected(object sender, SelectionChangedEventArgs e)
    {
        _selected = PresetList.SelectedItem as Preset;
        ClearResults();
        RefreshPlan();
    }

    // ---- 적용 대상 확인 ----

    private void RefreshPlan()
    {
        if (_selected == null)
        {
            PlanList.ItemsSource = null;
            PlanEmpty.Visibility = Visibility.Visible;
            PlanSummary.Text = "";
            ApplyBtn.IsEnabled = false;
            return;
        }

        var folder = _svc.SettingsFolder;
        var rows = _selected.Entries.Select(e =>
        {
            var t = _svc.GetTemplate(e.TemplateId);
            var (status, ok) =
                folder == null ? ("EVE 설정 폴더를 찾을 수 없음", false)
                : t == null || !File.Exists(_svc.TemplatePath(t.Id)) ? ("설정파일 없음", false)
                : _svc.GetRunState(e.CharId) switch
                {
                    RunState.Running => ("실행 중 — 건너뜀", false),
                    RunState.Unknown => ("실행 여부 확인 불가 — 건너뜀", false),
                    _ => ("적용 가능", true),
                };
            return new PlanRow { CharId = e.CharId, Name = _svc.Names.Display(e.CharId), TemplateName = t?.Name ?? "(삭제됨)", Status = status, Ok = ok };
        }).ToList();

        // 2초마다 갱신되므로 내용이 같으면 다시 그리지 않는다.
        if (PlanList.ItemsSource is List<PlanRow> old && old.Count == rows.Count
            && old.Zip(rows).All(p => p.First.CharId == p.Second.CharId && p.First.Status == p.Second.Status && p.First.Name == p.Second.Name && p.First.TemplateName == p.Second.TemplateName))
            return;

        PlanList.ItemsSource = rows;
        PlanEmpty.Visibility = Visibility.Collapsed;
        var go = rows.Count(r => r.Ok);
        PlanSummary.Text = $"적용 가능 {go}명" + (rows.Count - go > 0 ? $"  ·  건너뜀 {rows.Count - go}명" : "");
        ApplyBtn.IsEnabled = go > 0;
    }

    // ---- 적용 ----

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var ok = PlanList.ItemsSource is List<PlanRow> rows ? rows.Count(r => r.Ok) : 0;
        var msg = $"'{_selected.Name}' 프리셋을 적용합니다.\n\n· 적용 가능 {ok}명의 캐릭터 설정파일을 덮어씁니다.\n· 덮어쓰는 원본은 자동으로 백업되며 '직전 적용 되돌리기'로 복구할 수 있습니다.\n· 실행 중인 캐릭터는 건너뜁니다.";
        if (MessageBox.Show(msg, "프리셋 적용", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        ShowResults("3. 적용 결과", _svc.Apply(_selected), canRetry: true);
        RefreshPlan();
        UpdateUndo();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _failed.Count == 0) return;
        ShowResults("3. 적용 결과 (재시도)", _svc.Apply(_selected, _failed), canRetry: true);
        RefreshPlan();
        UpdateUndo();
    }

    // ---- 되돌리기 ----

    private void UpdateUndo()
    {
        var b = _svc.LastBackup();
        UndoBtn.IsEnabled = b != null;
        UndoBtn.ToolTip = b == null ? "되돌릴 적용 기록이 없습니다" : $"{b.Time:MM-dd HH:mm} '{b.PresetName}' 적용 ({b.Items.Count}명)을 되돌립니다";
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var b = _svc.LastBackup();
        if (b == null) return;
        var msg = $"{b.Time:MM-dd HH:mm}에 적용한 '{b.PresetName}' ({b.Items.Count}명)을 되돌립니다.\n\n"
                + "덮어쓰기 전 원본으로 복원하며, 그 이후 EVE에서 바뀐 설정도 함께 되돌아갑니다.\n계속할까요?";
        if (MessageBox.Show(msg, "직전 적용 되돌리기", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        ShowResults("3. 되돌리기 결과", _svc.UndoLast(), canRetry: false);
        RefreshPlan();
        UpdateUndo();
    }

    // ---- 결과 ----

    private void ShowResults(string title, List<ApplyResult> results, bool canRetry)
    {
        ResultTitle.Text = title;
        var rows = results.Select(r => new ResultRow(r)).ToList();
        ResultList.ItemsSource = rows;
        ResultEmpty.Visibility = Visibility.Collapsed;

        var ok = rows.Count(r => r.Ok);
        var fail = rows.Count - ok;
        ResultSummary.Text = $"성공 {ok}  ·  실패 {fail}";
        _failed = [.. rows.Where(r => !r.Ok).Select(r => r.CharId)];
        RetryBtn.Visibility = canRetry && fail > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearResults()
    {
        ResultTitle.Text = "3. 결과";
        ResultList.ItemsSource = null;
        ResultEmpty.Visibility = Visibility.Visible;
        ResultSummary.Text = "";
        RetryBtn.Visibility = Visibility.Collapsed;
        _failed = [];
    }
}
