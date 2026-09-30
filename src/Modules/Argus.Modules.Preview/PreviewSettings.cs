namespace Argus.Modules.Preview;

/// <summary>프리셋 안에서 클라이언트 하나의 프리뷰 배치. 좌표는 가상 화면 물리 픽셀.</summary>
public sealed class ClientLayout
{
    public string Character { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 210;
    public int H { get; set; } = 118;
    public bool Visible { get; set; } = true;

    /// <summary>이 프리셋의 사이클(다음/이전 전환)에 포함할지. 클라이언트 목록의 순서가 사이클 순서다.</summary>
    public bool InCycle { get; set; } = true;

    /// <summary>이 클라이언트로 바로 이동하는 단축키.</summary>
    public HotkeyTrigger? Hotkey { get; set; }

    // 프리뷰 위에 그리는 전투 HUD 요소를 이 클라이언트에서 보일지 (프리셋마다 따로)
    public bool HudDpsIn { get; set; } = true;
    public bool HudLogi { get; set; } = true;
    public bool HudNeut { get; set; } = true;
    public bool HudTackle { get; set; } = true;
    /// <summary>레드박싱 경고(붉은 깜빡임, 전환 단축키 안내, 레드박싱 전환 대상 포함).</summary>
    public bool HudSurge { get; set; } = true;

    public HudFlags Hud => new(HudDpsIn, HudLogi, HudNeut, HudTackle, HudSurge);
}

/// <summary>클라이언트 하나에서 켜져 있는 HUD 요소들.</summary>
public readonly record struct HudFlags(bool DpsIn, bool Logi, bool Neut, bool Tackle, bool Surge = true);

public enum HudElement { DpsIn, Logi, Neut, Tackle, Surge }

/// <summary>프리뷰 배치 프리셋. 프리셋마다 클라이언트별 위치·크기·표시 여부를 따로 갖는다.</summary>
public sealed class LayoutPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>클라이언트 목록의 순서가 곧 사이클 순서다 (지금 실행 중이 아닌 캐릭터도 자리를 기억한다).</summary>
    public List<ClientLayout> Clients { get; set; } = [];

    /// <summary>사이클 다음/이전 클라이언트로 전환하는 단축키.</summary>
    public HotkeyTrigger? CycleNext { get; set; }
    public HotkeyTrigger? CyclePrev { get; set; }

    /// <summary>레드박싱이 난 클라이언트로 전환하는 단축키. 평소에는 꺼져 있고 레드박싱 후 일정 시간 동안만 동작한다.</summary>
    public HotkeyTrigger? SurgeHotkey { get; set; }

    public override string ToString() => Name;
}

public sealed class PreviewSettings
{
    /// <summary>프리뷰 전체 표시 여부.</summary>
    public bool Enabled { get; set; } = true;
    public string ActivePresetId { get; set; } = "";
    public List<LayoutPreset> Presets { get; set; } = [];

    /// <summary>새 클라이언트 프리뷰의 기본 가로 크기. 세로는 클라이언트 비율로 정해진다.</summary>
    public int DefaultWidth { get; set; } = 210;
    /// <summary>프리뷰 불투명도 (0.3 ~ 1.0).</summary>
    public double Opacity { get; set; } = 1.0;
    /// <summary>지금 활성인 클라이언트의 프리뷰를 숨긴다.</summary>
    public bool HideActive { get; set; } = false;
    /// <summary>EVE 클라이언트가 맨 앞일 때만 프리뷰를 보여준다. 다른 창(브라우저 등)이 위에 있으면 숨긴다.</summary>
    public bool OnlyWhenEveActive { get; set; } = true;

    // ---- HUD 투명도 (0.2 ~ 1.0) ----
    /// <summary>하단 수치 바의 투명도.</summary>
    public double HudBarOpacity { get; set; } = 1.0;
    /// <summary>태클 리본의 투명도.</summary>
    public double HudRibbonOpacity { get; set; } = 1.0;

    // ---- 레드박싱 경고 (판정 기준은 전투 로그 설정) ----
    /// <summary>레드박싱 이벤트가 유지되는 시간(초): 붉은 색조, 전환 키 안내, 레드박싱 전환 단축키가 모두 이 시간 동안만 살아 있다.</summary>
    public int SurgeSeconds { get; set; } = 10;
    /// <summary>예전 설정 파일(색조 시간과 단축키 시간이 따로 있던 때)의 색조 시간을 이어받는다. 저장은 하지 않는다.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("SurgeTintSeconds")]
    public int LegacySurgeTintSeconds { set => SurgeSeconds = value; }
    /// <summary>붉은 색조가 한 번 깜빡이는 주기(ms). 짧을수록 빠르다.</summary>
    public int SurgeFlashMs { get; set; } = 700;
    /// <summary>레드박싱이 난 클라이언트 프리뷰에 레드박싱 전환 단축키를 밝은 회색으로 표시한다.</summary>
    public bool ShowSurgeKeyHint { get; set; } = true;

    // ---- 단축키 (전역 옵션. 단축키 자체는 프리셋에 저장) ----
    public bool HotkeysEnabled { get; set; } = true;
    /// <summary>EVE 클라이언트나 Argus 가 맨 앞일 때만 단축키 동작 (다른 프로그램의 마우스 뒤로/앞으로 등을 가로채지 않기 위해).</summary>
    public bool HotkeysOnlyWhenEveActive { get; set; } = true;
    /// <summary>지정한 것보다 보조키(Ctrl·Shift·Alt)가 더 눌려 있어도 동작. Ctrl 을 누른 채 락온하면서 전환하기 위한 것.</summary>
    public bool HotkeysAllowExtraMods { get; set; } = true;
    /// <summary>기본 단축키(마우스 옆 버튼 = 다음/이전)를 처음 한 번 넣었는지.</summary>
    public bool HotkeysSeeded { get; set; }
}
