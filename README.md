# Argus

EVE Online 멀티 클라이언트 관제 툴. 기능은 모듈 단위로 추가/삭제한다.

## 구조

```
src/
  Argus.Core/                 모듈 인터페이스, EventBus, ClientRegistry, 설정 저장소 (WPF 비의존)
  Argus.App/                  WPF 셸. 모듈 등록(App.xaml.cs)과 탭 호스팅만 담당
  Modules/
    Argus.Modules.Capture/    화면 변화 감지 + ROI 스크린샷 (CCTV 웹앱 호환 파일명으로 출력)
    Argus.Modules.Profiles/   캐릭터 설정 파일 프리셋 적용
    Argus.Modules.Preview/    eve-o-preview 대체 (요구사항 확정 후)
```

## 규칙

- 모듈끼리 직접 참조하지 않는다. `IModuleContext`(Clients / Events / Settings)만 통해 통신한다.
- 모듈 추가: 프로젝트 생성 + `App.xaml.cs`에 `Host.Register(...)` 한 줄. 삭제: 그 반대.
- 한 모듈의 시작 실패는 `ModuleFailed` 이벤트로 알리고 다른 모듈은 계속 동작한다.
- 설정은 `%APPDATA%\Argus\settings\{key}.json`, 모듈 데이터는 `%APPDATA%\Argus\data\{moduleId}`.

## 빌드

```powershell
dotnet build
dotnet run --project src/Argus.App
```

.NET 10 SDK, Windows 필요.
