# Argus

EVE Online 멀티 클라이언트 관제 툴. 기능은 모듈 단위로 추가/삭제한다.

## 구조

```
src/
  Argus.Core/                 모듈 인터페이스, EventBus, ClientRegistry, 설정 저장소 (WPF 비의존)
  Argus.App/                  WPF 셸. 모듈 등록(App.xaml.cs)과 탭 호스팅만 담당
  Modules/
    Argus.Modules.Capture/    화면 변화 감지 + ROI 스크린샷 (CCTV 파일명으로 저장) + 상태이상 아이콘 인식
    Argus.Modules.Alerts/     감지 알림
    Argus.Modules.Profiles/   캐릭터 설정 파일 프리셋 적용
    Argus.Modules.Preview/    eve-o-preview 대체: 썸네일, 레이아웃 프리셋, 단축키, 전투 HUD, 레드박싱
    Argus.Modules.CombatLog/  전투 로그에서 받는 DPS · LOGI · 뉴트와 레드박싱 계산
    Argus.Modules.Cctv/       CCTV 분석: 스크린샷의 오버뷰·프로빙·도킹 숫자를 비전 모델(Ollama)로 읽어 함선 출입·도킹·시그니처 변화 판정
  Argus.Ui/                   공용 WPF 부품 (NumberBox, 대시보드)
```

## 규칙

- 모듈끼리 직접 참조하지 않는다. `IModuleContext`(Clients / Events / Settings)만 통해 통신한다.
- 모듈 추가: 프로젝트 생성 + `App.xaml.cs`에 `Host.Register(...)` 한 줄. 삭제: 그 반대.
- 한 모듈의 시작 실패는 `ModuleFailed` 이벤트로 알리고 다른 모듈은 계속 동작한다.
- 설정은 `%APPDATA%\Argus\settings\{key}.json`, 모듈 데이터는 `%APPDATA%\Argus\data\{moduleId}`.

## CCTV 모듈 (비전 모델)

- 인식은 로컬 **Ollama 비전 모델**(기본 `qwen2.5vl:7b`)만 쓴다. Ollama 가 설치·실행 중이어야 한다.
- 모델은 Argus 를 켠다고, 분석을 켠다고 올라가지 않는다. CCTV 탭에서 분석을 켜 두면 **읽을 스크린샷이 있을 때만** 올리고, 모두 읽으면(유예 뒤) 내린다.
- 스크린샷은 CCTV(화면 감시)가 저장하는 `CCTV{날짜시각}_{캐릭터}.png` 폴더에서 읽는다.
- 자세한 설계와 결정은 `docs/SPEC-preview.md` 의 CCTV 절.

## 빌드

```powershell
dotnet build
dotnet run --project src/Argus.App
```

.NET 10 SDK, Windows 필요.
