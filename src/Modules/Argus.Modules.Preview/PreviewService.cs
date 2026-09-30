using System.Windows;
using System.Windows.Threading;
using Argus.Core.Clients;
using Argus.Core.Events;
using Argus.Core.Modules;
using Argus.Modules.Preview.Native;

namespace Argus.Modules.Preview;

/// <summary>클라이언트 프리뷰 창들과 레이아웃 프리셋을 관리한다.</summary>
public sealed partial class PreviewService : IDisposable
{
    private const string SettingsKey = "preview";

    private readonly IModuleContext _ctx;
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, PreviewTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IDisposable? _sub, _combatSub, _iconSub;

    // 전투 로그 모듈이 알려주는 캐릭터별 수치 (HUD 에 그린다)
    private readonly Dictionary<string, CombatSnapshot> _combat = new(StringComparer.OrdinalIgnoreCase);
    private long _combatAt;
    private const int CombatStaleMs = 3000;

    // 상태이상 인식 모듈이 화면의 아이콘에서 읽은 태클 상태 (태클의 유일한 출처). 읽기가 끊기면(최소화, 모듈 끔 등) 1.5초 뒤에 버린다.
    private readonly Dictionary<string, (TackleIconState State, long At)> _icons = new(StringComparer.OrdinalIgnoreCase);
    private const int IconStaleMs = 1500;

    // 'EVE 를 플레이 중일 때만 표시' 규칙용 상태
    private const int HideDelayMs = 200;
    private nint _lastFg;
    private bool _lastFgIsEve;
    private bool _eveFocused = true;
    private long _nonEveSince;

    public PreviewSettings Settings { get; }
    public bool EditMode { get; private set; }

    /// <summary>클라이언트 목록, 프리셋, 배치가 바뀌었을 때 (UI 갱신용).</summary>
    public event Action? Changed;

    public PreviewService(IModuleContext ctx)
    {
        _ctx = ctx;
        _ui = Application.Current.Dispatcher;
        Settings = ctx.Settings.Load<PreviewSettings>(SettingsKey);
        if (Settings.Presets.Count == 0) Settings.Presets.Add(new LayoutPreset { Name = "기본" });
        if (Settings.Presets.All(p => p.Id != Settings.ActivePresetId)) Settings.ActivePresetId = Settings.Presets[0].Id;
        Settings.Opacity = Math.Clamp(Settings.Opacity, 0.3, 1.0);
        Settings.DefaultWidth = Math.Clamp(Settings.DefaultWidth, TileGeometry.MinWidth, TileGeometry.MaxWidth);
        if (!Settings.HotkeysSeeded)
        {
            // 처음 한 번만: 마우스 옆 버튼 앞으로 = 다음, 뒤로 = 이전. 이후 프리셋마다 자유롭게 바꾼다.
            var first = ActivePreset;
            first.CycleNext ??= new HotkeyTrigger { Mouse = MouseButtonKind.X2 };
            first.CyclePrev ??= new HotkeyTrigger { Mouse = MouseButtonKind.X1 };
            Settings.HotkeysSeeded = true;
        }

        _tick.Tick += (_, _) => OnTick();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
    }

    public LayoutPreset ActivePreset => Settings.Presets.First(p => p.Id == Settings.ActivePresetId);

    public void Start()
    {
        EvaluateFocus(immediate: true);   // 시작 직후 깜빡이지 않도록 처음부터 현재 상태를 반영
        Sync(_ctx.Clients.Current);
        _sub = _ctx.Events.Subscribe<ClientsChanged>(e => _ui.BeginInvoke(() => Sync(e.Clients)));
        _combatSub = _ctx.Events.Subscribe<CombatStatsUpdated>(e => _ui.BeginInvoke(() => OnCombat(e)));
        _iconSub = _ctx.Events.Subscribe<TackleIconsUpdated>(e => _ui.BeginInvoke(() => OnIcons(e)));
        _tick.Start();
    }

    // ---------- 클라이언트 동기화 ----------

    private void Sync(IReadOnlyList<EveClient> all)
    {
        // 같은 캐릭터명의 창이 여러 개면 첫 번째만 쓴다.
        var clients = all.GroupBy(c => c.Character, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        foreach (var name in _tiles.Keys.Where(n => clients.All(c => !string.Equals(c.Character, n, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _tiles[name].Dispose();
            _tiles.Remove(name);
        }

        foreach (var c in clients)
        {
            if (_tiles.TryGetValue(c.Character, out var existing)) { existing.SetClient(c); continue; }
            var tile = new PreviewTile(this, c);
            tile.InitAspect();
            _tiles[c.Character] = tile;
            ApplyLayout(tile);
        }
        Changed?.Invoke();
    }

    /// <summary>활성 프리셋에서 이 타일의 배치를 찾아(없으면 만들어) 적용한다.</summary>
    private void ApplyLayout(PreviewTile tile)
    {
        var layout = GetOrCreateLayout(tile);
        var bounds = Rect32.FromSize(layout.X, layout.Y, layout.W, layout.H);
        var monitors = Win32.Monitors();
        if (monitors.Count > 0 && !TileGeometry.IsOnScreen(bounds, monitors)) // 모니터 구성이 바뀌어 화면 밖이면 빈 자리로 옮긴다
        {
            var (x, y) = TileGeometry.FindFreeSlot(monitors[0], layout.W, layout.H, OccupiedRects(layout.Character));
            layout.X = x; layout.Y = y;
            bounds = Rect32.FromSize(x, y, layout.W, layout.H);
            ScheduleSave();
        }
        tile.SetVisible(ShouldShow(tile, layout), bounds);
        tile.SetCombat(SnapshotFor(tile.Client.Character), TackleFor(tile.Client.Character), layout.Hud);
        tile.Hud.SetOpacities(Settings.HudBarOpacity, Settings.HudRibbonOpacity);
    }

    private bool ShouldShow(PreviewTile tile, ClientLayout layout) =>
        Settings.Enabled && layout.Visible
        && (EditMode || ((!Settings.OnlyWhenEveActive || _eveFocused) && !(Settings.HideActive && tile.IsActive)));

    private ClientLayout GetOrCreateLayout(PreviewTile tile)
    {
        var preset = ActivePreset;
        var layout = preset.Clients.FirstOrDefault(l => string.Equals(l.Character, tile.Client.Character, StringComparison.OrdinalIgnoreCase));
        if (layout != null) return layout;

        int w = Settings.DefaultWidth, h = (int)Math.Round(w / tile.Aspect);
        var monitors = Win32.Monitors();
        var (x, y) = monitors.Count > 0 ? TileGeometry.FindFreeSlot(monitors[0], w, h, OccupiedRects(tile.Client.Character)) : (20, 20);
        layout = new ClientLayout { Character = tile.Client.Character, X = x, Y = y, W = w, H = h };
        preset.Clients.Add(layout);
        ScheduleSave();
        return layout;
    }

    private List<Rect32> OccupiedRects(string exceptCharacter) =>
        [.. ActivePreset.Clients.Where(l => l.Visible && !string.Equals(l.Character, exceptCharacter, StringComparison.OrdinalIgnoreCase))
            .Select(l => Rect32.FromSize(l.X, l.Y, l.W, l.H))];

    /// <summary>드래그 중인 타일이 붙을 수 있는 다른 타일들의 영역.</summary>
    internal List<Rect32> OtherBounds(PreviewTile except) =>
        [.. _tiles.Values.Where(t => t != except && t.IsShown && t.Bounds.Width > 0).Select(t => t.Bounds)];

    /// <summary>Ctrl+크기 조절 때 함께 조절되는 타일들: 활성 프리셋에서 표시 중인 다른 프리뷰.</summary>
    internal List<PreviewTile> ScalableTiles(PreviewTile except) =>
        [.. _tiles.Values.Where(t => t != except && t.IsShown && ActivePreset.Clients.Any(l => l.Visible && string.Equals(l.Character, t.Client.Character, StringComparison.OrdinalIgnoreCase)))];

    // ---------- 주기 작업: 활성 클라이언트 표시, 원본 상태 ----------

    /// <summary>맨 앞 창이 EVE 클라이언트이거나 Argus 자신의 창(프리뷰 창 포함)인지 보고 표시 여부를 갱신한다.</summary>
    private void EvaluateFocus(bool immediate = false)
    {
        var fg = WindowFocus.Foreground;
        if (fg != 0 && fg != _lastFg)
        {
            _lastFg = fg;
            _lastFgIsEve = _tiles.Values.Any(t => t.HostHwnd == fg || t.Client.Hwnd == fg) || IsEveOrArgusWindow(fg);
        }

        // fg == 0 은 Alt+Tab 전환 같은 순간적인 상태라 직전 결과를 유지한다.
        var eve = fg == 0 ? _eveFocused : _lastFgIsEve;
        if (eve) { _nonEveSince = 0; SetEveFocused(true); return; }

        if (immediate) { SetEveFocused(false); return; }
        var now = Environment.TickCount64;
        if (_nonEveSince == 0) _nonEveSince = now;                 // 보이는 것은 즉시, 숨기는 것은 잠깐 지연
        else if (now - _nonEveSince >= HideDelayMs) SetEveFocused(false);
    }

    private void SetEveFocused(bool focused)
    {
        if (_eveFocused == focused) return;
        _eveFocused = focused;
        foreach (var t in _tiles.Values) ApplyLayout(t);
    }

    private static bool IsEveOrArgusWindow(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return true;   // Argus 자신은 예외: 설정을 만지는 동안에도 프리뷰가 보인다
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("exefile", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    private void OnTick()
    {
        EvaluateFocus();
        if (_combat.Count > 0 && Environment.TickCount64 - _combatAt > CombatStaleMs)   // 전투 로그 쪽이 멈추면 낡은 수치를 지운다
        {
            _combat.Clear();
            foreach (var t in _tiles.Values) ApplyCombat(t);
        }
        ExpireIcons();
        TickFeatureTests();
        RefreshSurge();   // 색조·단축키 유지 시간이 지났는지 확인
        var fg = WindowFocus.Foreground;
        var fgIsClient = _tiles.Values.Any(t => t.Client.Hwnd == fg);
        foreach (var tile in _tiles.Values)
        {
            // 포커스가 프리뷰 창 같은 다른 곳에 있을 때는 활성 표시를 그대로 둔다. 클라이언트로 옮겨갔을 때만 갱신.
            if (fgIsClient)
            {
                var active = tile.Client.Hwnd == fg;
                if (active != tile.IsActive)
                {
                    tile.SetActive(active);
                    ApplyLayout(tile);   // '활성 프리뷰 숨기기' 반영
                }
            }
            if (tile.RefreshSource()) SaveBounds(tile);
        }
    }

    // ---------- 전투 HUD ----------

    private void OnCombat(CombatStatsUpdated e)
    {
        _combat.Clear();
        foreach (var s in e.Snapshots) _combat[s.Character] = s;
        _combatAt = Environment.TickCount64;
        foreach (var t in _tiles.Values) ApplyCombat(t);
        RefreshSurge();
    }

    private void OnIcons(TackleIconsUpdated e)
    {
        var now = Environment.TickCount64;
        foreach (var s in e.States)
        {
            var before = _icons.TryGetValue(s.Character, out var prev) && now - prev.At <= IconStaleMs ? prev.State : null;
            _icons[s.Character] = (s, now);
            if (before != null && before.Disrupt == s.Disrupt && before.Scram == s.Scram && before.Hic == s.Hic) continue;   // 바뀐 것만 다시 그린다
            if (_tiles.TryGetValue(s.Character, out var tile)) ApplyCombat(tile);
        }
        RefreshSurge();
    }

    /// <summary>읽기가 끊긴(최소화, 모듈 중지 등) 클라이언트의 낡은 아이콘 정보를 버린다 (리본이 꺼진다).</summary>
    private void ExpireIcons()
    {
        if (_icons.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var name in _icons.Where(kv => now - kv.Value.At > IconStaleMs).Select(kv => kv.Key).ToList())
        {
            _icons.Remove(name);
            if (_tiles.TryGetValue(name, out var tile)) ApplyCombat(tile);
        }
    }

    private void ApplyCombat(PreviewTile tile)
    {
        var layout = ActivePreset.Clients.FirstOrDefault(l => string.Equals(l.Character, tile.Client.Character, StringComparison.OrdinalIgnoreCase));
        if (layout == null) return;
        var snap = SnapshotFor(tile.Client.Character);
        tile.SetCombat(snap, TackleFor(tile.Client.Character), layout.Hud);

        // 레드박싱: 색조, 전환 키 안내, 레드박싱 전환 단축키 모두 레드박싱 후 SurgeSeconds 동안만 살아 있다.
        var hint = Settings.ShowSurgeKeyHint && SurgeActive(snap, layout) && ActivePreset.SurgeHotkey is { IsEmpty: false } key ? key.ToString() : null;
        tile.SetSurge(SurgeActive(snap, layout), Settings.SurgeFlashMs, hint);
    }

    // ---------- 레드박싱 ----------

    private string _surgeSig = "";

    // 레드박싱 전환 단축키로 이미 그 클라이언트로 옮겨 간 레드박싱(감지 시각을 기억): 시간이 남아 있어도 끝난 것으로 본다. 그 뒤에 새로 감지된 레드박싱은 다시 살아난다.
    private readonly Dictionary<string, long> _surgeDismissed = new(StringComparer.OrdinalIgnoreCase);

    private bool SurgeWithin(CombatSnapshot? s, ClientLayout l, int seconds) =>
        s != null && l.HudSurge && s.SurgeAt != long.MinValue && Environment.TickCount64 - s.SurgeAt <= seconds * 1000L
        && (!_surgeDismissed.TryGetValue(l.Character, out var dismissed) || s.SurgeAt > dismissed);

    /// <summary>이 클라이언트의 지금 레드박싱 이벤트를 끝낸다 (붉은 색조, 전환 키 안내, 레드박싱 전환 대상에서 빠진다).</summary>
    public void DismissSurge(string character)
    {
        var s = SnapshotFor(character);
        if (s == null || s.SurgeAt == long.MinValue) return;
        _surgeDismissed[character] = s.SurgeAt;
        foreach (var t in _tiles.Values) ApplyCombat(t);
        RefreshSurge(force: true);
    }

    /// <summary>다음/이전 사이클에 포함된 클라이언트 중 몇 번째인지 (사이클 밖이면 null).</summary>
    public int? CycleNumber(string character)
    {
        var order = Clients().Where(c => c.Layout.InCycle).Select(c => c.Character).ToList();
        var i = order.FindIndex(n => string.Equals(n, character, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : i + 1;
    }

    private bool SurgeActive(CombatSnapshot? s, ClientLayout l) => SurgeWithin(s, l, Settings.SurgeSeconds);

    /// <summary>지금 레드박싱 전환 단축키가 동작하는 클라이언트들(레드박싱 경고를 켠 것만), 먼저 감지된 순서.</summary>
    internal List<(string Character, long At)> SurgeOrder() =>
        [.. ActivePreset.Clients
            .Select(l => (Layout: l, Snap: SnapshotFor(l.Character)))
            .Where(x => _tiles.ContainsKey(x.Layout.Character) && SurgeActive(x.Snap, x.Layout))
            .Select(x => (x.Layout.Character, x.Snap!.SurgeAt))
            .OrderBy(x => x.SurgeAt)];

    /// <summary>레드박싱 색조·단축키 동작 상태가 바뀌었으면(시작 또는 시간 만료) 화면과 단축키에 알린다.</summary>
    private void RefreshSurge(bool force = false)
    {
        var sig = string.Join("|", ActivePreset.Clients.Where(l => _tiles.ContainsKey(l.Character)).Select(l =>
        {
            var s = SnapshotFor(l.Character);
            return $"{l.Character}:{(SurgeActive(s, l) ? 1 : 0)}{(SurgeActive(s, l) ? 1 : 0)}:{s?.SurgeAt}";
        }));
        if (sig == _surgeSig && !force) return;
        _surgeSig = sig;
        foreach (var t in _tiles.Values) ApplyCombat(t);
        Changed?.Invoke();   // 레드박싱 전환 단축키를 켜거나 끄도록 단축키 쪽도 갱신
    }

    /// <summary>프리뷰 하나를 기본 크기(설정의 기본 프리뷰 크기, 클라이언트 화면 비율)로 되돌린다. 왼쪽 위 위치는 그대로 둔다.</summary>
    internal void ResetSize(PreviewTile tile)
    {
        var layout = GetOrCreateLayout(tile);
        layout.W = Settings.DefaultWidth;
        layout.H = (int)Math.Round(layout.W / tile.Aspect);
        ApplyLayout(tile);
        Save(); Changed?.Invoke();
    }

    public void SetSurgeHotkey(HotkeyTrigger? trigger)
    {
        ActivePreset.SurgeHotkey = trigger is { IsEmpty: false } ? trigger : null;
        foreach (var t in _tiles.Values) ApplyCombat(t);
        Save(); Changed?.Invoke();
    }

    /// <summary>이 클라이언트에서 HUD 요소 하나를 켜거나 끈다 (활성 프리셋).</summary>
    public void SetHud(string character, HudElement element, bool on)
    {
        if (!_tiles.TryGetValue(character, out var t)) return;
        var l = GetOrCreateLayout(t);
        switch (element)
        {
            case HudElement.DpsIn: l.HudDpsIn = on; break;
            case HudElement.Logi: l.HudLogi = on; break;
            case HudElement.Neut: l.HudNeut = on; break;
            case HudElement.Tackle: l.HudTackle = on; break;
            case HudElement.Surge: l.HudSurge = on; break;
        }
        ApplyCombat(t);
        RefreshSurge();
        Save(); Changed?.Invoke();
    }

    internal PreviewTile? TileFor(string character) => _tiles.TryGetValue(character, out var t) ? t : null;

    internal void ActivateClient(EveClient client) => _ctx.Clients.Activate(client);

    // ---------- 이동/크기 저장 ----------

    internal void SaveBounds(PreviewTile tile)
    {
        var layout = ActivePreset.Clients.FirstOrDefault(l => string.Equals(l.Character, tile.Client.Character, StringComparison.OrdinalIgnoreCase));
        if (layout == null) return;
        var b = tile.Bounds;
        layout.X = b.Left; layout.Y = b.Top; layout.W = b.Width; layout.H = b.Height;
        ScheduleSave();
        Changed?.Invoke();
    }

    private void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }   // 드래그 중 잦은 저장을 모은다

    public void Save()
    {
        _saveTimer.Stop();
        _ctx.Settings.Save(SettingsKey, Settings);
    }

    // ---------- UI 가 호출하는 동작 ----------

    public sealed record ClientInfo(string Character, ClientLayout Layout);

    /// <summary>실행 중인 클라이언트와 활성 프리셋의 배치.</summary>
    public List<ClientInfo> Clients()
    {
        foreach (var t in _tiles.Values) GetOrCreateLayout(t);   // 새 클라이언트는 목록 끝에 붙는다
        return [.. ActivePreset.Clients
            .Where(l => _tiles.ContainsKey(l.Character))
            .Select(l => new ClientInfo(l.Character, l))];
    }

    public void SetEnabled(bool enabled)
    {
        Settings.Enabled = enabled;
        foreach (var t in _tiles.Values) ApplyLayout(t);
        Save(); Changed?.Invoke();
    }

    public void SetEditMode(bool edit)
    {
        EditMode = edit;
        foreach (var t in _tiles.Values) { t.SetEditMode(edit); ApplyLayout(t); }
        Changed?.Invoke();
    }

    public void SetVisible(string character, bool visible)
    {
        if (!_tiles.TryGetValue(character, out var t)) return;
        GetOrCreateLayout(t).Visible = visible;
        ApplyLayout(t);
        Save(); Changed?.Invoke();
    }

    // ---------- 사이클·단축키 (활성 프리셋 기준) ----------

    public void SetInCycle(string character, bool included)
    {
        if (!_tiles.TryGetValue(character, out var t)) return;
        GetOrCreateLayout(t).InCycle = included;
        Save(); Changed?.Invoke();
    }

    public void SetClientHotkey(string character, HotkeyTrigger? trigger)
    {
        if (!_tiles.TryGetValue(character, out var t)) return;
        GetOrCreateLayout(t).Hotkey = trigger is { IsEmpty: false } ? trigger : null;
        Save(); Changed?.Invoke();
    }

    public void SetCycleHotkey(bool next, HotkeyTrigger? trigger)
    {
        var t = trigger is { IsEmpty: false } ? trigger : null;
        if (next) ActivePreset.CycleNext = t; else ActivePreset.CyclePrev = t;
        Save(); Changed?.Invoke();
    }

    /// <summary>클라이언트 목록에서 <paramref name="character"/> 를 화면에 보이는 목록 기준 <paramref name="toIndex"/> 번째 자리로 옮긴다. 이 순서가 사이클 순서다.</summary>
    public void MoveClient(string character, int toIndex)
    {
        var shown = Clients();
        var from = shown.FindIndex(c => string.Equals(c.Character, character, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return;
        toIndex = Math.Clamp(toIndex, 0, shown.Count - 1);
        if (toIndex == from) return;

        var list = ActivePreset.Clients;
        var moving = shown[from].Layout;
        list.Remove(moving);
        // 옮길 자리에 있던 클라이언트(들)의 앞 또는 뒤에 끼운다 (실행 중이 아닌 캐릭터의 자리는 그대로 둔다).
        var anchor = shown[toIndex].Layout;
        var at = list.IndexOf(anchor);
        list.Insert(toIndex > from ? at + 1 : at, moving);
        Save(); Changed?.Invoke();
    }

    public void UpdateGlobalSettings(int? defaultWidth = null, double? opacity = null, bool? hideActive = null, bool? onlyWhenEveActive = null,
        bool? hotkeysEnabled = null, bool? hotkeysOnlyWhenEveActive = null, bool? hotkeysAllowExtraMods = null,
        int? surgeSeconds = null, int? surgeFlashMs = null, bool? showSurgeKeyHint = null,
        double? hudBarOpacity = null, double? hudRibbonOpacity = null)
    {
        if (defaultWidth is { } w) Settings.DefaultWidth = Math.Clamp(w, TileGeometry.MinWidth, TileGeometry.MaxWidth);
        if (opacity is { } o) { Settings.Opacity = Math.Clamp(o, 0.3, 1.0); foreach (var t in _tiles.Values) t.ApplyOpacity(); }
        if (hideActive is { } h) { Settings.HideActive = h; foreach (var t in _tiles.Values) ApplyLayout(t); }
        if (hudBarOpacity is { } bo) Settings.HudBarOpacity = Math.Clamp(bo, 0.2, 1.0);
        if (hudRibbonOpacity is { } ro) Settings.HudRibbonOpacity = Math.Clamp(ro, 0.2, 1.0);
        if (hudBarOpacity != null || hudRibbonOpacity != null) foreach (var t in _tiles.Values) t.Hud.SetOpacities(Settings.HudBarOpacity, Settings.HudRibbonOpacity);
        var surgeChanged = surgeSeconds != null || surgeFlashMs != null || showSurgeKeyHint != null;
        if (surgeSeconds is { } ss) Settings.SurgeSeconds = Math.Clamp(ss, 2, 60);
        if (surgeFlashMs is { } sf) Settings.SurgeFlashMs = Math.Clamp(sf, 200, 2000);
        if (showSurgeKeyHint is { } sk) Settings.ShowSurgeKeyHint = sk;
        if (surgeChanged) { foreach (var t in _tiles.Values) ApplyCombat(t); RefreshSurge(force: true); }
        if (hotkeysEnabled is { } he) Settings.HotkeysEnabled = he;
        if (hotkeysOnlyWhenEveActive is { } ho) Settings.HotkeysOnlyWhenEveActive = ho;
        if (hotkeysAllowExtraMods is { } hm) Settings.HotkeysAllowExtraMods = hm;
        if (onlyWhenEveActive is { } oe) { Settings.OnlyWhenEveActive = oe; EvaluateFocus(immediate: true); foreach (var t in _tiles.Values) ApplyLayout(t); }
        Save(); Changed?.Invoke();
    }

    // ---------- 레이아웃 프리셋 ----------

    public void SwitchPreset(string id)
    {
        if (Settings.Presets.All(p => p.Id != id) || id == Settings.ActivePresetId) return;
        Settings.ActivePresetId = id;
        foreach (var t in _tiles.Values) ApplyLayout(t);
        Save(); Changed?.Invoke();
    }

    /// <summary>새 프리셋을 만든다. 현재 배치를 복사해서 시작하고 그 프리셋으로 전환한다.</summary>
    public LayoutPreset CreatePreset(string name)
    {
        var preset = new LayoutPreset
        {
            Name = name.Trim(),
            Clients = [.. ActivePreset.Clients.Select(l => new ClientLayout { Character = l.Character, X = l.X, Y = l.Y, W = l.W, H = l.H, Visible = l.Visible, InCycle = l.InCycle, Hotkey = l.Hotkey?.Clone(), HudDpsIn = l.HudDpsIn, HudLogi = l.HudLogi, HudNeut = l.HudNeut, HudTackle = l.HudTackle, HudSurge = l.HudSurge })],
            CycleNext = ActivePreset.CycleNext?.Clone(),
            CyclePrev = ActivePreset.CyclePrev?.Clone(),
            SurgeHotkey = ActivePreset.SurgeHotkey?.Clone(),
        };
        Settings.Presets.Add(preset);
        SwitchPreset(preset.Id);
        return preset;
    }

    public void RenamePreset(string id, string name)
    {
        var p = Settings.Presets.FirstOrDefault(x => x.Id == id);
        if (p == null || string.IsNullOrWhiteSpace(name)) return;
        p.Name = name.Trim();
        Save(); Changed?.Invoke();
    }

    /// <summary>프리셋을 삭제한다. 마지막 하나는 지울 수 없다.</summary>
    public bool DeletePreset(string id)
    {
        if (Settings.Presets.Count <= 1) return false;
        var p = Settings.Presets.FirstOrDefault(x => x.Id == id);
        if (p == null) return false;
        Settings.Presets.Remove(p);
        if (Settings.ActivePresetId == id)
        {
            Settings.ActivePresetId = Settings.Presets[0].Id;
            foreach (var t in _tiles.Values) ApplyLayout(t);
        }
        Save(); Changed?.Invoke();
        return true;
    }

    public bool PresetNameTaken(string name, string? exceptId = null) =>
        Settings.Presets.Any(p => p.Id != exceptId && string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _tick.Stop();
        _sub?.Dispose();
        _combatSub?.Dispose();
        _iconSub?.Dispose();
        Save();
        foreach (var t in _tiles.Values) t.Dispose();
        _tiles.Clear();
    }
}
