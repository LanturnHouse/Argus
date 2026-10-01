using System.IO;
using System.Media;
using Argus.Core.Events;
using Argus.Core.Modules;

namespace Argus.Modules.Alerts;

public sealed class AlertsSettings
{
    /// <summary>비어 있으면 시스템 경고음(BEEP), 아니면 해당 .wav 파일을 재생한다.</summary>
    public string SoundPath { get; set; } = "";
}

/// <summary>RegionChanged 이벤트를 구독해 알림음을 재생한다. 캡처 모듈과는 이벤트로만 연결된다.</summary>
public sealed class AlertsModule : IArgusModule
{
    private const string SettingsKey = "alerts";
    private IModuleContext? _ctx;
    private AlertsSettings _settings = new();
    private IDisposable? _sub;

    public string Id => "argus.alerts";
    public string Icon => "";
    public string DisplayName => "알림";

    // 알림음은 CCTV 알림에만 쓰이므로 설정 페이지에서 그 하위 항목으로 보여준다.
    public string? SettingsParentId => "argus.capture";

    public Task StartAsync(IModuleContext context, CancellationToken ct)
    {
        _ctx = context;
        _settings = context.Settings.Load<AlertsSettings>(SettingsKey);
        _sub = context.Events.Subscribe<RegionChanged>(e => { if (e.Beep) Play(); });
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _sub?.Dispose();
        return Task.CompletedTask;
    }

    // 별도 페이지는 없다. 알림음 설정은 '설정' 페이지의 섹션으로 제공한다.
    public object? CreateSettingsView() => new AlertsSettingsView(_settings.SoundPath, SetSound, Play);

    private void SetSound(string path)
    {
        _settings.SoundPath = path;
        _ctx?.Settings.Save(SettingsKey, _settings);
    }

    private void Play()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_settings.SoundPath) && File.Exists(_settings.SoundPath))
            {
                using var p = new SoundPlayer(_settings.SoundPath);
                p.Play(); // 비동기 재생
            }
            else SystemSounds.Exclamation.Play();
        }
        catch { /* 알림음 실패가 감시를 멈추면 안 된다 */ }
    }
}
