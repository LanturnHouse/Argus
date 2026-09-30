using System.IO;
using System.Windows;
using Argus.Core.Clients;
using Argus.Core.Events;
using Argus.Core.Modules;
using Argus.Core.Settings;

namespace Argus.App;

public partial class App : Application
{
    public ModuleHost Host { get; private set; } = null!;
    public ClientRegistry Registry { get; private set; } = null!;
    public IEventBus Bus { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argus");
        Bus = new EventBus();
        Registry = new ClientRegistry(Bus);
        var ctx = new ModuleContext(Registry, Bus, new JsonSettingsStore(Path.Combine(root, "settings")), Path.Combine(root, "data"));

        Host = new ModuleHost(ctx);
        // 모듈 추가/삭제는 이 목록만 고치면 된다.
        Host.Register(new Modules.Capture.CaptureModule());
        Host.Register(new Modules.Alerts.AlertsModule());
        Host.Register(new Modules.Profiles.ProfilesModule());
        Host.Register(new Modules.Preview.PreviewModule());
        Host.Register(new Modules.CombatLog.CombatLogModule());
        Host.Register(new Modules.Capture.StatusIcons.StatusIconModule());   // 설정 > 프리뷰 > '상태이상 인식'
        Host.Register(new Modules.Preview.PreviewFeatureTestModule());   // 설정 > 프리뷰 맨 아래 '기능 테스트'

        Registry.Start();
        await Host.StartAllAsync();
        new MainWindow().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await Host.StopAllAsync();
        Registry.Dispose();
        base.OnExit(e);
    }
}
