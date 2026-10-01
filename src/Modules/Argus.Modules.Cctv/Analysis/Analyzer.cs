using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Argus.Modules.Cctv;

/// <summary>
/// 인식 결과(오버뷰 행, 프로빙 시그니처, 도킹 숫자)를 이전 프레임과 비교해 사건(워프인·아웃, 점프, 도킹·언독, 코옵, 시그니처 생성·소멸)을 판정하는 상태 기계.
/// 기존 EVE CCTV 웹앱(local-service/analysis.mjs)의 규칙을 그대로 옮겼다. 각 캐릭터의 이미지를 촬영 순서대로 하나씩 넣어야 한다.
/// </summary>
public sealed class Analyzer(CctvStore store)
{
    /// <summary>이 속도(m/s) 이상이면 워프 중으로 본다. 애프터버너를 켠 저속 함선은 수천 m/s 를 넘기 어렵다.</summary>
    public const double WarpSpeedThresholdMps = 10_000;
    /// <summary>
    /// 도킹 숫자는 오버뷰보다 늦게 바뀐다(몇 초). 오버뷰에서 행이 생기거나 사라진 뒤 이 시간 안에 숫자가 따라 바뀌면 언독 · 도킹으로 확정한다.
    /// 숫자가 오버뷰보다 먼저 바뀌는 경우도 같은 시간 안이면 짝지어 준다.
    /// </summary>
    private const int DockConfirmWindowMs = 6_000;

    private Db Db => store.Db;

    // ---------- 코버트 옵스 ----------
    // 코버트 클로킹 장치를 달 수 있는 선체: 클로킹한 채 워프할 수 있어서 '워프로 들어왔나, 언독했나'를 화면만으로 구분할 수 없다.
    // 그래서 이 선체의 등장·이탈은 모두 코옵인/코옵아웃으로 기록한다. T3 크루저(Tengu/Legion/Proteus/Loki)와 심우주 수송선은
    // 장착한 서브시스템에 따라 달라 오버뷰로 알 수 없으므로 일부러 뺐다.
    private static readonly HashSet<string> CovertOpsShips = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANATHEMA", "BUZZARD", "HELIOS", "CHEETAH",                 // 코버트 옵스 프리깃
        "PURIFIER", "MANTICORE", "NEMESIS", "HOUND",                // 스텔스 폭격기
        "PILGRIM", "FALCON", "ARAZU", "RAPIER",                     // 포스 리콘
        "PROWLER", "CRANE", "VIATOR", "PRORATOR",                   // 봉쇄 돌파선
        "REDEEMER", "WIDOW", "SIN", "PANTHER",                      // 블랙 옵스
        "ASTERO", "PROSPECT", "PACIFIER", "METAMORPHOSIS",
    };

    internal static bool IsCovertOpsShip(string? ship) => CovertOpsShips.Contains(Regex.Replace((ship ?? "").Trim(), @"\*+$", "").ToUpperInvariant());

    // 오버뷰에는 천체와 고정 구조물도 나온다. 이름 열은 파일럿 이름이 아니므로 상태 기계에 넣지 않는다.
    // '이름'이 아니라 '종류' 열로 판단한다 (플레이어가 구조물 이름으로 캐릭터를 지을 수 있고, Sunesis 는 실제 함선이다).
    private static readonly Regex NonPilotTypes = new(@"^(?:WORMHOLE|STARGATE|ASTRAHUS|FORTIZAR|KEEPSTAR|RAITARU|AZBEL|SOTIYO|ATHANOR|TATARA|PHAROLYNX|TENEBREX|ANSIBLEX|METENOX MOON DRILL|ORBITAL SKYHOOK|CONTROL TOWER|CUSTOMS OFFICE|MOBILE DEPOT|MOBILE TRACTOR UNIT)(?:\b|\*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // OCR 이 "Sun K5" 를 "SunK5"나 "SunKS5" 로 읽기도 한다. 분광형 접두어는 알아보되 Sunesis 함선은 삼키지 않는다.
    private static readonly Regex SunType = new(@"^SUN\s*[OBAFGKM][0-9S]{1,2}(?:\b|\*)|^SUN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool Finite(double? v) => v is { } d && !double.IsNaN(d) && !double.IsInfinity(d);

    internal static string NormalizeIdentity(string? value) => Regex.Replace((value ?? "").Trim(), @"\s+", " ").ToUpperInvariant();

    /// <summary>함선 이름 열의 OCR 찌꺼기를 자른다: 기호가 처음 나오는 곳부터 버린다 ("Zealot=*5" → "Zealot", "Zealot5" 가 되지 않게).</summary>
    internal static string CleanShipName(string? value)
    {
        var raw = (value ?? "").Trim();
        var m = Regex.Match(raw, @"[^\p{L}\p{N}\s]");
        return Regex.Replace(m.Success ? raw[..m.Index] : raw, @"\s+", " ").Trim();
    }

    private static bool IsPilotOverviewRow(OverviewRow row)
    {
        var type = (row.Ship ?? "").Trim();
        return !string.IsNullOrEmpty(row.Name) && !SunType.IsMatch(type) && !NonPilotTypes.IsMatch(type);
    }

    private static List<OverviewRow> TrackableOverviewRows(IEnumerable<Observation> observations) =>
        [.. observations.SelectMany(o => o.Payload.Fields.OverviewRows ?? [])
            // 속도를 못 읽었다고 그 캐릭터가 오버뷰에서 사라진 것은 아니다.
            .Select(r => new OverviewRow { Distance = r.Distance, Name = r.Name, Ship = CleanShipName(r.Ship), Corporation = r.Corporation, Speed = Finite(r.Speed) ? r.Speed : null, Raw = r.Raw, Confidence = r.Confidence, Brightness = r.Brightness })
            .Where(IsPilotOverviewRow)];

    private static List<SignatureRow> ProbeRows(IEnumerable<Observation> observations) => [.. observations.SelectMany(o => o.Payload.Fields.Signatures ?? [])];

    // ---------- 도킹 숫자 ----------

    private static Dictionary<int, int>? DockReadings(List<Observation> observations)
    {
        var dock = observations.Where(o => o.Kind == RegionKind.Dock).ToList();
        var readings = new Dictionary<int, int>();
        for (int index = 0; index < dock.Count; index++)
        {
            var count = dock[index].Payload.Fields.DockCount;
            if (count is null or < 0) return null;
            readings[dock[index].Payload.RegionIndex ?? index] = count.Value;
        }
        return readings.Count == dock.Count && readings.Count > 0 ? readings : null;
    }

    private sealed record DockChange(int RegionIndex, int Before, int After, int Delta);

    /// <summary>
    /// 이번 프레임의 도킹 숫자가 '마지막으로 제대로 읽은 값'에서 바뀌었으면 그 변화를 돌려준다. 직전 프레임에서 숫자를 못 읽었어도(흐림, 오독) 그 앞의 값과 비교한다.
    /// 숫자가 둘 이상의 도킹 영역에서 동시에 바뀌면 어느 오버뷰 행과 이어지는지 알 수 없으므로 null.
    /// </summary>
    private DockChange? DockCounterChange(string watcherId, string captureKey, List<Observation> current)
    {
        var cur = DockReadings(current);
        if (cur == null) return null;
        var changes = new List<DockChange>();
        foreach (var (regionIndex, count) in cur)
        {
            var prev = Db.One("""
                SELECT json_extract(o.payload_json, '$.fields.dockCount') AS cnt
                FROM observations o JOIN images i ON i.id = o.image_id
                WHERE o.watcher_id = ? AND o.region_kind = 'dock' AND i.capture_key < ?
                  AND json_extract(o.payload_json, '$.regionIndex') = ?
                  AND json_type(o.payload_json, '$.fields.dockCount') = 'integer' AND json_extract(o.payload_json, '$.fields.dockCount') >= 0
                ORDER BY i.capture_key DESC LIMIT 1
                """, watcherId, captureKey, regionIndex);
            if (prev == null) continue;   // 이 영역은 처음 읽었다: 비교할 값이 없다
            var before = (int)prev.Long("cnt");
            if (count != before) changes.Add(new DockChange(regionIndex, before, count, count - before));
        }
        return changes.Count == 1 ? changes[0] : null;
    }

    // ---------- 이전 프레임 ----------

    private sealed record Previous(long? ImageId, List<Observation> Observations);

    private Previous PreviousObservations(string watcherId, string captureKey)
    {
        var prevImage = Db.One("""
            SELECT i.id FROM images i JOIN observations o ON o.image_id = i.id
            WHERE o.watcher_id = ? AND i.capture_key < ? AND i.processing_status = 'processed'
            GROUP BY i.id, i.capture_key ORDER BY i.capture_key DESC LIMIT 1
            """, watcherId, captureKey);
        if (prevImage == null) return new Previous(null, []);
        var observations = Db.Query("SELECT region_kind, payload_json, confidence FROM observations WHERE image_id = ? AND watcher_id = ? ORDER BY id", prevImage.Long("id"), watcherId)
            .Select(r => new Observation { WatcherId = watcherId, Kind = Names.ToRegionKind(r.Str("region_kind")!), Payload = CctvJson.Deserialize<RegionPayload>(r.Str("payload_json")) ?? new RegionPayload(), Confidence = r.Dbl("confidence") }).ToList();
        return new Previous(prevImage.Long("id"), observations);
    }

    // ---------- 이벤트 기록 ----------

    private static JsonObject Obj(params (string Key, object? Value)[] items)
    {
        var o = new JsonObject();
        foreach (var (k, v) in items) o[k] = ToNode(v);
        return o;
    }

    private static JsonNode? ToNode(object? v) => v switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => double.IsNaN(d) || double.IsInfinity(d) ? null : JsonValue.Create(d),
        _ => JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture)),
    };

    private static void Merge(JsonObject target, params (string Key, object? Value)[] items) { foreach (var (k, v) in items) target[k] = ToNode(v); }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private long? InsertEvent(EventDraft e)
    {
        var changes = Db.Exec("""
            INSERT OR IGNORE INTO events (event_time, event_type, character_name, corporation_ticker, ship_name, speed_mps, watcher_id, confidence, image_id, previous_image_id, details_json)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, e.Time, e.Type, NullIfEmpty(e.Character), NullIfEmpty(e.Corporation), NullIfEmpty(e.Ship), Finite(e.Speed) ? e.Speed : null, e.WatcherId, e.Confidence, e.ImageId, e.PreviousImageId, CctvJson.Compact(e.Details));
        return changes > 0 ? Db.LastInsertId() : null;
    }

    private bool ReviseEntryEvent(long? eventId, EventDraft e)
    {
        if (eventId == null) return false;
        return Db.Exec("UPDATE events SET event_type = ?, ship_name = ?, speed_mps = ?, image_id = ?, previous_image_id = ?, details_json = ? WHERE id = ?",
            e.Type, NullIfEmpty(e.Ship), Finite(e.Speed) ? e.Speed : null, e.ImageId, e.PreviousImageId, CctvJson.Compact(e.Details), eventId) > 0;
    }

    private static DateTime ParseTime(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None);

    // ---------- 도킹 · 언독 확정 ----------

    private sealed class PendingEvent
    {
        public long Id; public string Time = ""; public string Type = ""; public long? ImageId; public long? PreviousImageId; public JsonObject Details = new();
        public string? DockPending => Details["dockPending"]?.GetValue<string>();
    }

    /// <summary>도킹 숫자가 바뀌었지만 아직 오버뷰 행과 짝지어지지 않은 몫. 숫자가 오버뷰보다 늦거나 빨라도 시간 안이면 짝지을 수 있게 남겨 둔다.</summary>
    private sealed class DockCredit
    {
        public long Id; public string Kind = ""; public int Amount; public string At = ""; public JsonObject Details = new();
    }

    private void ReconcileDockTransitions(string watcherId, ImageRow image, List<Observation> current)
    {
        var capturedAt = ParseTime(image.CapturedAt);

        // 1) 이번 프레임에서 도킹 숫자가 바뀌었으면 몫으로 적어 둔다 (줄었으면 언독 몫, 늘었으면 도킹 몫).
        if (DockCounterChange(watcherId, image.CaptureKey, current) is { } change)
        {
            var details = Obj(("dockCountBefore", change.Before), ("dockCountAfter", change.After), ("dockRegionIndex", change.RegionIndex), ("imageId", image.Id));
            Db.Exec("INSERT INTO dock_credits (watcher_id, kind, amount, at, details_json) VALUES (?, ?, ?, ?, ?)",
                watcherId, change.Delta < 0 ? "entry" : "exit", Math.Abs(change.Delta), image.CapturedAt, CctvJson.Compact(details));
        }

        // 2) 시간이 지난 몫은 버린다 (오버뷰에 나타나지 않은 은닉 함선 등이 만든 변화).
        var credits = Db.Query("SELECT id, kind, amount, at, details_json FROM dock_credits WHERE watcher_id = ? ORDER BY at, id", watcherId)
            .Select(r => new DockCredit { Id = r.Long("id"), Kind = r.Str("kind")!, Amount = (int)r.Long("amount"), At = r.Str("at")!, Details = CctvJson.ParseObject(r.Str("details_json")) })
            .Where(c =>
            {
                if ((capturedAt - ParseTime(c.At)).TotalMilliseconds <= DockConfirmWindowMs) return true;
                Db.Exec("DELETE FROM dock_credits WHERE id = ?", c.Id);
                return false;
            }).ToList();

        var pending = Db.Query("""
            SELECT id, event_time, event_type, image_id, previous_image_id, details_json FROM events
            WHERE watcher_id = ? AND json_extract(details_json, '$.dockPending') IS NOT NULL ORDER BY event_time, id
            """, watcherId).Select(r => new PendingEvent { Id = r.Long("id"), Time = r.Str("event_time")!, Type = r.Str("event_type")!, ImageId = r.LongN("image_id"), PreviousImageId = r.LongN("previous_image_id"), Details = CctvJson.ParseObject(r.Str("details_json")) }).ToList();
        if (pending.Count == 0) return;

        void Save(PendingEvent e, string type, JsonObject details, long? imageId = null, long? previousImageId = null, bool explicitImage = false)
        {
            Db.Exec("UPDATE events SET event_type = ?, image_id = ?, previous_image_id = ?, details_json = ? WHERE id = ?",
                type, explicitImage ? imageId : e.ImageId, explicitImage ? previousImageId : e.PreviousImageId, CctvJson.Compact(details), e.Id);
        }

        bool Expired(PendingEvent e) => (capturedAt - ParseTime(e.Time)).TotalMilliseconds > DockConfirmWindowMs;

        // 3) 시간 안의 대기 중인 이벤트와 몫을 짝짓는다. 대기 중인 수만큼 몫이 모였을 때만 확정한다 (누가 도킹했는지 임의로 고르지 않는다).
        //    숫자가 오버뷰보다 늦게 바뀌어 몫이 하나씩 따로 들어와도 합쳐서 짝지으므로, 여러 대가 연달아 언독 · 도킹해도 모두 확정된다.
        var live = pending.Where(e => !Expired(e)).ToList();
        var blockers = live.Where(e => e.DockPending is "entry_blocker" or "exit_blocker").ToList();   // 코버트 선체: 숫자에 들어가는지 알 수 없다
        var confirmed = new HashSet<long>();
        if (blockers.Count == 0)
        {
            foreach (var (kind, dockPending, newType, reason) in new[] { ("entry", "entry", "undocked", "overview_added_with_dock_decrease"), ("exit", "exit", "docked", "overview_removed_with_dock_increase") })
            {
                var group = live.Where(e => e.DockPending == dockPending).ToList();
                var mine = credits.Where(c => c.Kind == kind).ToList();
                if (group.Count == 0 || mine.Sum(c => c.Amount) < group.Count) continue;

                // 몫을 오래된 것부터 하나씩 쓴다.
                var queue = new Queue<DockCredit>(mine);
                foreach (var e in group)
                {
                    var credit = queue.Peek();
                    var d = (JsonObject)e.Details.DeepClone();
                    Merge(d, ("verification", "confirmed"), ("reason", reason),
                        ("dockCountBefore", credit.Details["dockCountBefore"]), ("dockCountAfter", credit.Details["dockCountAfter"]), ("dockRegionIndex", credit.Details["dockRegionIndex"]),
                        ("transitionImageId", e.ImageId), ("confirmedImageId", image.Id));
                    d.Remove("dockPending");
                    Save(e, newType, d, image.Id, e.ImageId == image.Id ? e.PreviousImageId : e.ImageId, explicitImage: true);
                    confirmed.Add(e.Id);
                    if (--credit.Amount <= 0) { Db.Exec("DELETE FROM dock_credits WHERE id = ?", credit.Id); queue.Dequeue(); }
                    else Db.Exec("UPDATE dock_credits SET amount = ? WHERE id = ?", credit.Amount, credit.Id);
                }
            }
        }

        // 4) 시간 안에 숫자가 따라 바뀌지 않은 이벤트는 확정하지 못한 채로 마무리한다 (언독 → 오버뷰 인, 도킹 후보는 그대로 오버뷰 이탈).
        foreach (var e in pending)
        {
            if (confirmed.Contains(e.Id) || !Expired(e)) continue;
            var details = (JsonObject)e.Details.DeepClone();
            details.Remove("dockPending"); details.Remove("dockFrames");
            if (e.DockPending == "entry")
            {
                details["reason"] = "dock_count_decrease_not_confirmed";
                details.Remove("verification");
                Save(e, "appeared", details);
                Db.Exec("UPDATE current_objects SET entry_type = 'appeared' WHERE watcher_id = ? AND entry_event_id = ?", watcherId, e.Id);
            }
            else
            {
                if (e.DockPending == "exit" && e.Type == "disappeared")
                {
                    details["reason"] = "dock_count_increase_not_confirmed";
                    details.Remove("verification");
                }
                Save(e, e.Type, details);
            }
        }
    }

    private void ClosePendingDockEntry(long? entryEventId)
    {
        if (entryEventId == null) return;
        var row = Db.One("SELECT event_type, details_json FROM events WHERE id = ?", entryEventId);
        if (row == null) return;
        var details = CctvJson.ParseObject(row.Str("details_json"));
        var pending = details["dockPending"]?.GetValue<string>();
        if (pending != "entry" && pending != "entry_blocker") return;
        var wasUndockEstimate = pending == "entry";
        details.Remove("dockPending"); details.Remove("dockFrames");
        if (wasUndockEstimate)
        {
            details.Remove("verification");
            details["reason"] = "dock_count_decrease_not_confirmed_before_exit";
        }
        Db.Exec("UPDATE events SET event_type = ?, details_json = ? WHERE id = ?", wasUndockEstimate ? "appeared" : row.Str("event_type"), CctvJson.Compact(details), entryEventId);
    }

    // ---------- 이름 매칭 ----------

    internal static int EditDistance(string? left, string? right)
    {
        static string Strip(string? s) => Regex.Replace(NormalizeIdentity(s), "[^A-Z0-9가-힣]", "");
        var a = Strip(left); var b = Strip(right);
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int prev = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = prev;
            }
        }
        return row[b.Length];
    }

    private static bool OverviewIsReliable(List<Observation> observations)
    {
        var overview = observations.Where(o => o.Kind == RegionKind.Overview).ToList();
        return overview.Count > 0 && overview.All(o => o.Payload.Fields.OverviewDetected == true);
    }

    // ---------- 현재 대상 (오버뷰) ----------

    private sealed class ExistingObject
    {
        public string Identity = "", Name = ""; public string? Ship, Corporation, Distance, EntryType, FirstSeenAt;
        public double? Speed, PreviousSpeed, Confidence, LastBrightness; public long LastImageId; public bool EntryConfirmed; public long? EntryPreviousImageId, EntryEventId;
    }

    private void ReconcileCurrentObjects(string watcherId, WatchType watchType, ImageRow image, List<Observation> currentObservations, Previous previous, List<OverviewRow> rows)
    {
        var existingRows = Db.Query("""
            SELECT identity_key, character_name, ship_name, corporation_ticker, distance_text, speed_mps, previous_speed_mps, confidence, entry_type, first_seen_at,
                   last_brightness, image_id, entry_confirmed, entry_previous_image_id, entry_event_id
            FROM current_objects WHERE watcher_id = ?
            """, watcherId).Select(r => new ExistingObject
        {
            Identity = r.Str("identity_key")!, Name = r.Str("character_name")!, Ship = r.Str("ship_name"), Corporation = r.Str("corporation_ticker"), Distance = r.Str("distance_text"),
            Speed = r.Dbl("speed_mps"), PreviousSpeed = r.Dbl("previous_speed_mps"), Confidence = r.Dbl("confidence"), EntryType = r.Str("entry_type"), FirstSeenAt = r.Str("first_seen_at"),
            LastBrightness = r.Dbl("last_brightness"), LastImageId = r.Long("image_id"), EntryConfirmed = r.Bool("entry_confirmed"), EntryPreviousImageId = r.LongN("entry_previous_image_id"), EntryEventId = r.LongN("entry_event_id"),
        }).ToList();
        var existing = existingRows.ToDictionary(r => r.Identity);
        var matchedExisting = new HashSet<string>();
        var resolved = new List<(string Identity, OverviewRow Row)>();   // 삽입 순서를 유지 (같은 identity 는 나중 행이 덮어쓴다)

        foreach (var row in rows)
        {
            var exact = NormalizeIdentity(row.Name);
            string? identity = existing.ContainsKey(exact) && !matchedExisting.Contains(exact) ? exact : null;
            if (identity == null)
            {
                // 이름이 조금 다르게 읽혔을 때: 함선이 같고 이름 길이·편집 거리 차이가 2 이하인 후보가 정확히 하나일 때만 같은 대상으로 본다.
                var rowShip = NormalizeIdentity(row.Ship);
                var candidates = existingRows.Where(c => !matchedExisting.Contains(c.Identity) && NormalizeIdentity(c.Ship) == rowShip
                    && Math.Abs(NormalizeIdentity(c.Name).Length - NormalizeIdentity(row.Name).Length) <= 2 && EditDistance(c.Name, row.Name) <= 2).ToList();
                if (candidates.Count == 1) identity = candidates[0].Identity;
            }
            identity ??= exact;
            matchedExisting.Add(identity);
            var at = resolved.FindIndex(x => x.Identity == identity);
            if (at >= 0) resolved[at] = (identity, row); else resolved.Add((identity, row));
        }

        bool gate = watchType == WatchType.Gate;

        foreach (var (identity, row) in resolved)
        {
            var brightness = Finite(row.Brightness) ? row.Brightness : null;
            if (existing.TryGetValue(identity, out var prevRow))
            {
                // 함선이 워프를 빠져나가거나 범위를 벗어나기 직전 몇 프레임은 행 전체가 어두워지고 속도가 0 처럼 읽힌다.
                // 그 행의 밝기가 마지막으로 제대로 읽었을 때의 절반 아래로 떨어지면 전환 중인 흐린 프레임으로 보고 갱신하지 않는다
                // (이 대상은 여전히 있는 것으로 치고, 마지막 실제 속도·밝기를 덮어쓰지 않는다).
                var isFaded = Finite(prevRow.LastBrightness) && Finite(row.Brightness) && row.Brightness < prevRow.LastBrightness * 0.5;
                if (isFaded) continue;

                if (!prevRow.EntryConfirmed)
                {
                    var lastSpeed = prevRow.Speed;
                    double? recordedSpeed = prevRow.EntryEventId != null ? Db.One("SELECT speed_mps FROM events WHERE id = ?", prevRow.EntryEventId)?.Dbl("speed_mps") : null;
                    var firstSpeed = Finite(recordedSpeed) ? recordedSpeed : lastSpeed;
                    var knownShip = NullIfEmpty(row.Ship) ?? prevRow.Ship;
                    var isCovert = IsCovertOpsShip(knownShip);
                    var isWarpIn = !isCovert && Finite(lastSpeed) && lastSpeed >= WarpSpeedThresholdMps && Finite(row.Speed) && row.Speed < lastSpeed;
                    var hasShip = !string.IsNullOrEmpty(knownShip);
                    // 고속 값이 여러 프레임 이어질 수 있다: 더 낮은 유효 속도가 확인되어 감속 추세가 생길 때까지 추정으로 둔다.
                    var canDecide = isCovert || (hasShip && Finite(firstSpeed) && firstSpeed < WarpSpeedThresholdMps)
                        || (hasShip && !Finite(firstSpeed) && Finite(row.Speed)) || (hasShip && isWarpIn);
                    if (canDecide)
                    {
                        var entryType = isCovert ? "covop_in" : isWarpIn ? "warp_in" : !Finite(firstSpeed) ? "appeared" : gate ? "jump_in" : "undocked";
                        Db.Exec("UPDATE current_objects SET entry_type = ?, entry_confirmed = 1 WHERE watcher_id = ? AND identity_key = ?", entryType, watcherId, identity);
                        var details = Obj(("distance", prevRow.Distance),
                            ("reason", isCovert ? "covert_ops_hull" : isWarpIn ? "decelerated_from_warp_speed" : entryType == "appeared" ? "first_speed_unreadable" : gate ? "first_seen_at_gate" : "first_seen_at_structure"));
                        if (isWarpIn) Merge(details, ("verification", "confirmed"), ("firstSpeed", firstSpeed), ("previousSpeed", lastSpeed), ("nextSpeed", row.Speed));
                        if (entryType == "undocked") Merge(details, ("verification", "estimated"), ("dockPending", "entry"), ("dockFrames", 0));
                        if (isCovert && watchType == WatchType.Structure) Merge(details, ("dockPending", "entry_blocker"), ("dockFrames", 0));
                        var decided = new EventDraft
                        {
                            Time = prevRow.FirstSeenAt ?? image.CapturedAt, Type = entryType, Character = prevRow.Name, Corporation = prevRow.Corporation, Ship = knownShip, Speed = firstSpeed,
                            WatcherId = watcherId, Confidence = prevRow.Confidence, ImageId = image.Id, PreviousImageId = prevRow.LastImageId, Details = details,
                        };
                        if (!ReviseEntryEvent(prevRow.EntryEventId, decided)) InsertEvent(decided);
                    }
                }

                var b = brightness ?? prevRow.LastBrightness;
                Db.Exec("""
                    UPDATE current_objects SET character_name = ?, ship_name = COALESCE(?, ship_name), corporation_ticker = COALESCE(?, corporation_ticker), distance_text = COALESCE(?, distance_text),
                      previous_speed_mps = CASE WHEN ? IS NOT NULL THEN speed_mps ELSE previous_speed_mps END, speed_mps = COALESCE(?, speed_mps), confidence = ?, image_id = ?,
                      last_seen_at = ?, last_brightness = ?, missing_count = 0, missing_since_at = NULL, missing_image_id = NULL, pending_exit_type = NULL, pending_exit_reason = NULL
                    WHERE watcher_id = ? AND identity_key = ?
                    """, row.Name, NullIfEmpty(row.Ship), NullIfEmpty(row.Corporation), NullIfEmpty(row.Distance), row.Speed, row.Speed, row.Confidence, image.Id, image.CapturedAt, b, watcherId, identity);
                continue;
            }

            const string InsertSql = """
                INSERT INTO current_objects (watcher_id, identity_key, character_name, ship_name, corporation_ticker, distance_text, speed_mps, confidence, image_id, last_seen_at, entry_type,
                  first_seen_at, missing_count, last_brightness, entry_confirmed, entry_previous_image_id, entry_event_id)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?, ?)
                """;

            if (IsCovertOpsShip(row.Ship))
            {
                // 코버트 선체는 클로킹한 채 워프할 수 있어 워프/점프 구분과 속도 추세가 의미 없다: 다음 프레임을 기다리지 않고 바로 기록한다.
                var details = Obj(("distance", row.Distance), ("reason", "covert_ops_hull"));
                if (watchType == WatchType.Structure) Merge(details, ("dockPending", "entry_blocker"), ("dockFrames", 0));
                var entryEventId = InsertEvent(new EventDraft { Time = image.CapturedAt, Type = "covop_in", Character = row.Name, Corporation = row.Corporation, Ship = row.Ship, Speed = row.Speed,
                    WatcherId = watcherId, Confidence = row.Confidence, ImageId = image.Id, PreviousImageId = previous.ImageId, Details = details });
                Db.Exec(InsertSql, watcherId, identity, row.Name, NullIfEmpty(row.Ship), NullIfEmpty(row.Corporation), NullIfEmpty(row.Distance), row.Speed, row.Confidence, image.Id, image.CapturedAt, "covop_in", image.CapturedAt, brightness, 1, previous.ImageId, entryEventId);
                continue;
            }

            // 저속으로 처음 등장하면 워프인 조건을 채울 수 없으므로 게이트는 점프인, 스트럭쳐는 언독을 첫 프레임에 바로 기록한다.
            var immediateType = !string.IsNullOrEmpty(row.Ship) && Finite(row.Speed) && row.Speed < WarpSpeedThresholdMps ? (gate ? "jump_in" : "undocked") : null;
            var estimatedWarp = Finite(row.Speed) && row.Speed >= WarpSpeedThresholdMps;
            var newEntryType = immediateType ?? (estimatedWarp ? "warp_in" : null);
            long? newEventId = null;
            if (newEntryType != null)
            {
                var details = Obj(("distance", row.Distance), ("reason", estimatedWarp ? "high_first_speed" : gate ? "first_seen_at_gate" : "first_seen_at_structure"));
                if (estimatedWarp) Merge(details, ("verification", "estimated"), ("firstSpeed", row.Speed));
                if (immediateType == "undocked") Merge(details, ("verification", "estimated"), ("dockPending", "entry"), ("dockFrames", 0));
                newEventId = InsertEvent(new EventDraft { Time = image.CapturedAt, Type = newEntryType, Character = row.Name, Corporation = row.Corporation, Ship = row.Ship, Speed = row.Speed,
                    WatcherId = watcherId, Confidence = row.Confidence, ImageId = image.Id, PreviousImageId = previous.ImageId, Details = details });
            }
            Db.Exec(InsertSql, watcherId, identity, row.Name, NullIfEmpty(row.Ship), NullIfEmpty(row.Corporation), NullIfEmpty(row.Distance), row.Speed, row.Confidence, image.Id, image.CapturedAt, newEntryType, image.CapturedAt, brightness, immediateType != null ? 1 : 0, previous.ImageId, newEventId);
        }

        // 믿을 수 있는 현재 오버뷰(모든 오버뷰 영역에서 헤더를 읽음)에서만 사라진 대상을 이탈로 처리한다.
        if (!OverviewIsReliable(currentObservations)) goto Docking;
        foreach (var (identity, row) in existing)
        {
            if (resolved.Any(x => x.Identity == identity)) continue;
            var isCovert = IsCovertOpsShip(row.Ship);
            if (watchType == WatchType.Structure) ClosePendingDockEntry(row.EntryEventId);

            if (!row.EntryConfirmed && row.EntryEventId == null)
            {
                // 쓸 만한 첫 속도도, 알려진 선체도 없이 사라졌다: 나간 뒤에 워프를 지어내지 않고 '나타났음'만 기록한다.
                InsertEvent(new EventDraft { Time = row.FirstSeenAt ?? image.CapturedAt, Type = isCovert ? "covop_in" : "appeared", Character = row.Name, Corporation = row.Corporation, Ship = row.Ship, Speed = row.Speed,
                    WatcherId = watcherId, Confidence = row.Confidence, ImageId = row.LastImageId, PreviousImageId = row.EntryPreviousImageId,
                    Details = Obj(("distance", row.Distance), ("reason", "entry_unconfirmed_before_exit")) });
            }

            // 마지막 속도가 높으면 워프아웃 추정, 직전 유효 속도보다 올랐으면 확정. 도킹 카운터 변화는 이탈 후보가 모호하지 않을 때만 도킹을 확정한다.
            var isAccelerating = Finite(row.PreviousSpeed) && row.Speed > row.PreviousSpeed;
            var isWarpSpeed = !isCovert && Finite(row.Speed) && row.Speed >= WarpSpeedThresholdMps;
            var warpConfirmed = isWarpSpeed && isAccelerating;
            var type = "disappeared"; var reason = "overview_row_removed";
            if (isCovert) { type = "covop_out"; reason = "covert_ops_hull"; }
            else if (warpConfirmed) { type = "warp_out"; reason = "accelerated_to_warp_speed"; }
            else if (isWarpSpeed) { type = "warp_out"; reason = "high_last_speed"; }
            else if (gate) { type = "jump_out"; reason = "overview_removed_at_gate"; }
            var dockPending = watchType == WatchType.Structure ? (isCovert ? "exit_blocker" : warpConfirmed ? null : "exit") : null;
            var details = Obj(("distance", row.Distance), ("reason", reason));
            if (type == "warp_out") Merge(details, ("verification", warpConfirmed ? "confirmed" : "estimated"), ("previousSpeed", row.PreviousSpeed), ("lastSpeed", row.Speed));
            if (dockPending != null) Merge(details, ("dockPending", dockPending), ("dockFrames", 0));
            InsertEvent(new EventDraft
            {
                // 현재 프레임이 '사라짐'을 확인한 시점이다. 비교할 이전 그림은 그 대상이 마지막으로 보인 프레임(흐린 프레임을 건너뛰었다면 몇 프레임 전일 수 있다).
                Time = image.CapturedAt, Type = type, Character = row.Name, Corporation = row.Corporation, Ship = row.Ship, Speed = row.Speed,
                WatcherId = watcherId, Confidence = row.Confidence, ImageId = image.Id, PreviousImageId = row.LastImageId, Details = details,
            });
            Db.Exec("DELETE FROM current_objects WHERE watcher_id = ? AND identity_key = ?", watcherId, identity);
        }

    Docking:
        if (watchType == WatchType.Structure) ReconcileDockTransitions(watcherId, image, currentObservations);
    }

    // ---------- 현재 시그니처 (프로빙) ----------

    private void ReconcileCurrentSignatures(string watcherId, ImageRow image, List<SignatureRow> signatures, long? previousImageId)
    {
        var existing = Db.Query("SELECT signature_id, name, group_name, distance_text, confidence, first_seen_at, missing_count, image_id FROM current_signatures WHERE watcher_id = ?", watcherId)
            .ToDictionary(r => r.Str("signature_id")!, r => r);
        var current = new Dictionary<string, SignatureRow>();
        foreach (var s in signatures) current[s.Id] = s;

        foreach (var (id, sig) in current)
        {
            if (!existing.ContainsKey(id))
            {
                Db.Exec("INSERT INTO current_signatures (watcher_id, signature_id, name, group_name, distance_text, confidence, image_id, first_seen_at, last_seen_at, missing_count) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 0)",
                    watcherId, id, NullIfEmpty(sig.Name), NullIfEmpty(sig.Group), NullIfEmpty(sig.Distance), sig.Confidence, image.Id, image.CapturedAt, image.CapturedAt);
                InsertEvent(new EventDraft { Time = image.CapturedAt, Type = "signature_created", Character = id, WatcherId = watcherId, Confidence = sig.Confidence, ImageId = image.Id, PreviousImageId = previousImageId,
                    Details = Obj(("name", sig.Name), ("group", sig.Group), ("distance", sig.Distance)) });
            }
            else
            {
                Db.Exec("UPDATE current_signatures SET name = ?, group_name = ?, distance_text = ?, confidence = ?, image_id = ?, last_seen_at = ?, missing_count = 0 WHERE watcher_id = ? AND signature_id = ?",
                    NullIfEmpty(sig.Name), NullIfEmpty(sig.Group), NullIfEmpty(sig.Distance), sig.Confidence, image.Id, image.CapturedAt, watcherId, id);
            }
        }

        foreach (var (id, row) in existing)
        {
            if (current.ContainsKey(id)) continue;
            // 한 번 누락된 것은 OCR 누락일 수 있어 보류하고, 성공한 스캔에서 두 번 연속 누락되면 소멸로 기록한다.
            if (row.Long("missing_count") < 1)
            {
                Db.Exec("UPDATE current_signatures SET missing_count = missing_count + 1 WHERE watcher_id = ? AND signature_id = ?", watcherId, id);
                continue;
            }
            Db.Exec("DELETE FROM current_signatures WHERE watcher_id = ? AND signature_id = ?", watcherId, id);
            InsertEvent(new EventDraft { Time = image.CapturedAt, Type = "signature_destroyed", Character = id, WatcherId = watcherId, Confidence = row.Dbl("confidence"), ImageId = image.Id,
                PreviousImageId = row.LongN("image_id") ?? previousImageId, Details = Obj(("name", row.Str("name")), ("group", row.Str("group_name")), ("distance", row.Str("distance_text"))) });
        }
    }

    // ---------- 진입점 ----------

    /// <summary>한 이미지의 관측을 이전 프레임과 비교해 이벤트와 현재 상태를 갱신한다. 이미지는 촬영 순서대로 넣는다.</summary>
    public void AnalyzeImage(ImageRow image, IReadOnlyList<Observation> observations)
    {
        Db.Locked(() =>
        {
            foreach (var group in observations.GroupBy(o => o.WatcherId))
            {
                var watcherRow = Db.One("SELECT watch_type FROM watchers WHERE id = ?", group.Key);
                if (watcherRow == null) continue;
                var watchType = Names.ToWatchType(watcherRow.Str("watch_type")!);
                var current = group.ToList();
                var previous = PreviousObservations(group.Key, image.CaptureKey);
                var overview = TrackableOverviewRows(current.Where(o => o.Kind == RegionKind.Overview));
                ReconcileCurrentObjects(group.Key, watchType, image, current, previous, overview);

                var probe = ProbeRows(current.Where(o => o.Kind == RegionKind.Probe));
                // 프로빙 창이 숨겨졌거나 비어 읽혔다고 모든 시그니처가 사라진 것은 아니다: 유효한 행이 하나라도 읽힌 성공 스캔에서만 비교한다.
                if (probe.Count > 0) ReconcileCurrentSignatures(group.Key, image, probe, previous.ImageId);
            }
        });
    }
}
