using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Argus.Modules.Profiles;

/// <summary>설정 프리셋 화면: 설정파일 / 프리셋 / 적용 세 탭.</summary>
public partial class ProfilesView : UserControl
{
    private readonly ProfilesService _svc;
    private readonly TemplatesTab _templates;
    private readonly PresetsTab _presets;
    private readonly ApplyTab _apply;

    public ProfilesView(ProfilesService svc)
    {
        _svc = svc;
        InitializeComponent();
        _templates = new TemplatesTab(svc);
        _presets = new PresetsTab(svc);
        _apply = new ApplyTab(svc);

        TabTemplates.IsChecked = true;
        Show(_templates, "EVE 설정 폴더의 캐릭터 설정을 Argus 안에 저장해 둡니다.");
        Loaded += async (_, _) =>
        {
            // 생성자에서 켠 탭 표시가 화면에 붙는 과정에서 풀리는 일이 있어, 화면이 뜬 뒤 아무 탭도 켜져 있지 않으면 다시 켠다.
            if (TabTemplates.IsChecked != true && TabPresets.IsChecked != true && TabApply.IsChecked != true) TabTemplates.IsChecked = true;
            await ResolveNamesAsync();
        };
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (TabTemplates.IsChecked == true) Show(_templates, "EVE 설정 폴더의 캐릭터 설정을 Argus 안에 저장해 둡니다. 저장한 뒤에는 EVE 폴더의 원본이 바뀌어도 영향이 없습니다.");
        else if (TabPresets.IsChecked == true) Show(_presets, "캐릭터별로 적용할 설정파일을 묶어 프리셋으로 저장합니다.");
        else Show(_apply, "프리셋을 적용하면 EVE 설정 폴더의 캐릭터 파일을 덮어씁니다. 실행 중인 캐릭터는 건너뜁니다.");
    }

    private void Show(UserControl tab, string subtitle)
    {
        Body.Content = tab;
        Subtitle.Text = subtitle;
        (tab as IReloadable)?.Reload();
    }

    /// <summary>폴더의 캐릭터 ID 들의 이름을 ESI 로 조회하고 화면을 갱신한다.</summary>
    private async Task ResolveNamesAsync()
    {
        try
        {
            var ids = _svc.ListCharFiles().Select(f => f.Id)
                .Concat(_svc.Data.Templates.Select(t => t.SourceCharId))
                .Concat(_svc.Data.Presets.SelectMany(p => p.Entries.Select(e => e.CharId)));
            if (await _svc.Names.ResolveAsync(ids) > 0) (Body.Content as IReloadable)?.Reload();
        }
        catch { /* 이름 조회 실패는 ID 표시로 대체된다 */ }
    }
}

public interface IReloadable
{
    void Reload();
}
