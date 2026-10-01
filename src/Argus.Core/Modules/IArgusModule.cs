namespace Argus.Core.Modules;

/// <summary>기능 단위 모듈. 추가/삭제는 이 인터페이스 구현체를 등록/제거하는 것으로 끝난다.</summary>
public interface IArgusModule
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>사이드바 아이콘 (Segoe Fluent Icons 글리프).</summary>
    string Icon => "";

    Task StartAsync(IModuleContext context, CancellationToken ct);
    Task StopAsync();

    /// <summary>모듈이 UI 탭을 제공하면 반환한다 (WPF에 의존하지 않도록 object).</summary>
    object? CreateView() => null;

    /// <summary>모듈의 전역 설정 UI. 있으면 사이드바 '설정' 페이지에 이 모듈의 섹션으로 모인다.</summary>
    object? CreateSettingsView() => null;

    /// <summary>사이드바에서 이 모듈의 탭을 다른 모듈 탭의 하위 항목으로 보여주려면 그 모듈의 Id. 그 모듈의 탭이 없으면 일반 항목으로 나온다.</summary>
    string? NavParentId => null;

    /// <summary>설정 페이지에서 이 모듈의 설정을 다른 모듈의 그룹 아래 하위 항목으로 보여주려면 그 모듈의 Id. 그 모듈이 없으면 자기 그룹으로 나온다.</summary>
    string? SettingsParentId => null;
}

/// <summary>코어가 모듈에 제공하는 공용 서비스. 모듈끼리는 직접 참조하지 않고 여기와 EventBus만 통한다.</summary>
public interface IModuleContext
{
    Clients.IClientRegistry Clients { get; }
    Events.IEventBus Events { get; }
    Settings.ISettingsStore Settings { get; }
    string DataDirectory(string moduleId);
}
