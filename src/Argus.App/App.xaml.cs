using System.Diagnostics;
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
    public ISettingsStore Settings { get; private set; } = null!;

    public App()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argus");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "argus.log");
            // 한 세대만 보관: 1MB 를 넘으면 직전 로그로 밀어낸다.
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".old", overwrite: true);
            Trace.Listeners.Add(new TextWriterTraceListener(path));
            Trace.AutoFlush = true;

            DispatcherUnhandledException += (_, e) => Log("UI", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Domain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
            TaskScheduler.UnobservedTaskException += (_, e) => Log("Task", e.Exception);
        }
        catch { /* 로그 실패가 앱 시작을 막지 않게 */ }
    }

    private static void Log(string kind, Exception ex) => Trace.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{kind}] {ex}");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argus");
        Bus = new EventBus();
        Registry = new ClientRegistry(Bus);
        Settings = new JsonSettingsStore(Path.Combine(root, "settings"));
        var ctx = new ModuleContext(Registry, Bus, Settings, Path.Combine(root, "data"));

        Host = new ModuleHost(ctx);
        // 모듈 추가/삭제는 이 목록만 고치면 된다.
        Host.Register(new Modules.Capture.CaptureModule());
        Host.Register(new Modules.Alerts.AlertsModule());
        Host.Register(new Modules.Profiles.ProfilesModule());
        Host.Register(new Modules.Preview.PreviewModule());
        Host.Register(new Modules.CombatLog.CombatLogModule());
        Host.Register(new Modules.Cctv.CctvModule());   // 스크린샷을 로컬 Ollama 비전 모델로 판독하는 분석 모듈 (사이드바 'CCTV' 아래 '분석')
        Host.Register(new Modules.Preview.PreviewFeatureTestModule());   // 설정 > 프리뷰 맨 아래 '기능 테스트'

        Registry.Start();
        await Host.StartAllAsync();

        // 메인 창을 닫으면 프리뷰 창이 남아 있어도 앱을 끝낸다. 모듈이 먼저 만든 창(프리뷰 호스트)이 자동으로 MainWindow 가 되지 않게
        // 메인 창을 직접 지정한 뒤에 종료 방식을 바꾼다.
        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await Host.StopAllAsync();
        Registry.Dispose();
        base.OnExit(e);
    }
}
