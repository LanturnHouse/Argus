using System.Text.Json;
using System.Text.Json.Nodes;

namespace Argus.Modules.Cctv;

/// <summary>한 이미지를 처리할 때 읽어야 하는 영역 (감시 눈깔 정보 포함).</summary>
public sealed record WatcherRegion(string WatcherId, string WatcherLabel, WatchType WatchType, RegionKind Kind, double X, double Y, double W, double H, int SortOrder);

internal sealed record CharacterStat(string Name, int ImageCount, string LatestCaptureAt, long LatestImageId);

internal sealed record ImageDetail(ImageRow Image, string Status, List<(long Id, string WatcherId, RegionKind Kind, RegionPayload Payload, double? Confidence)> Observations);

/// <summary>
/// CCTV 분석 결과 저장소 (SQLite). 구조는 기존 EVE CCTV 웹앱과 같다: 설정 · 감시 눈깔 · 영역 · 이미지 · 관측 · 이벤트 · 현재 대상 · 현재 시그니처.
/// 웹앱과 달리 프로그램을 다시 켜도 결과를 지우지 않는다 (처리한 이미지는 다시 분석하지 않는다).
/// </summary>
public sealed class CctvStore : IDisposable
{
    internal Db Db { get; }

    public CctvStore(string databasePath)
    {
        Db = new Db(databasePath);
        Db.Exec("""
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE IF NOT EXISTS watchers (
              id TEXT PRIMARY KEY, label TEXT NOT NULL, character_name TEXT NOT NULL,
              watch_type TEXT NOT NULL CHECK (watch_type IN ('structure', 'gate')),
              enabled INTEGER NOT NULL DEFAULT 1, region_version INTEGER NOT NULL DEFAULT 1,
              created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE IF NOT EXISTS regions (
              id INTEGER PRIMARY KEY AUTOINCREMENT, watcher_id TEXT NOT NULL REFERENCES watchers(id) ON DELETE CASCADE,
              kind TEXT NOT NULL CHECK (kind IN ('overview', 'probe', 'dock')),
              x REAL NOT NULL, y REAL NOT NULL, width REAL NOT NULL, height REAL NOT NULL, sort_order INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS images (
              id INTEGER PRIMARY KEY AUTOINCREMENT, folder_path TEXT NOT NULL, file_path TEXT NOT NULL UNIQUE, filename TEXT NOT NULL,
              character_name TEXT NOT NULL, capture_key TEXT NOT NULL, captured_at TEXT NOT NULL, size_bytes INTEGER NOT NULL,
              modified_at INTEGER NOT NULL, processing_status TEXT NOT NULL DEFAULT 'pending', discovered_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE IF NOT EXISTS observations (
              id INTEGER PRIMARY KEY AUTOINCREMENT, image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
              watcher_id TEXT REFERENCES watchers(id) ON DELETE SET NULL, region_kind TEXT NOT NULL, payload_json TEXT NOT NULL,
              confidence REAL, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE IF NOT EXISTS events (
              id INTEGER PRIMARY KEY AUTOINCREMENT, event_time TEXT NOT NULL, event_type TEXT NOT NULL, character_name TEXT,
              corporation_ticker TEXT, ship_name TEXT, speed_mps REAL, watcher_id TEXT REFERENCES watchers(id) ON DELETE SET NULL,
              confidence REAL, image_id INTEGER REFERENCES images(id) ON DELETE CASCADE,
              previous_image_id INTEGER REFERENCES images(id) ON DELETE SET NULL, details_json TEXT,
              created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE IF NOT EXISTS current_objects (
              watcher_id TEXT NOT NULL REFERENCES watchers(id) ON DELETE CASCADE, identity_key TEXT NOT NULL, character_name TEXT NOT NULL,
              ship_name TEXT, corporation_ticker TEXT, distance_text TEXT, speed_mps REAL, confidence REAL,
              image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE, last_seen_at TEXT NOT NULL, entry_type TEXT, first_seen_at TEXT,
              missing_count INTEGER NOT NULL DEFAULT 0, missing_since_at TEXT, missing_image_id INTEGER, pending_exit_type TEXT,
              pending_exit_reason TEXT, last_brightness REAL, previous_speed_mps REAL, entry_confirmed INTEGER NOT NULL DEFAULT 0,
              entry_previous_image_id INTEGER, entry_event_id INTEGER REFERENCES events(id) ON DELETE SET NULL,
              PRIMARY KEY (watcher_id, identity_key));
            CREATE TABLE IF NOT EXISTS current_signatures (
              watcher_id TEXT NOT NULL REFERENCES watchers(id) ON DELETE CASCADE, signature_id TEXT NOT NULL, name TEXT, group_name TEXT,
              distance_text TEXT, confidence REAL, image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
              first_seen_at TEXT NOT NULL, last_seen_at TEXT NOT NULL, missing_count INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (watcher_id, signature_id));
            CREATE TABLE IF NOT EXISTS dock_credits (
              id INTEGER PRIMARY KEY AUTOINCREMENT, watcher_id TEXT NOT NULL REFERENCES watchers(id) ON DELETE CASCADE,
              kind TEXT NOT NULL, amount INTEGER NOT NULL, at TEXT NOT NULL, details_json TEXT);
            CREATE INDEX IF NOT EXISTS idx_images_folder_capture ON images(folder_path, capture_key DESC);
            CREATE INDEX IF NOT EXISTS idx_images_character_capture ON images(character_name, capture_key DESC);
            CREATE INDEX IF NOT EXISTS idx_images_status_capture ON images(processing_status, capture_key);
            CREATE INDEX IF NOT EXISTS idx_events_time ON events(event_time DESC);
            CREATE INDEX IF NOT EXISTS idx_events_watcher_time ON events(watcher_id, event_time DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_events_dedupe ON events(watcher_id, event_time, event_type, IFNULL(character_name, ''), IFNULL(details_json, ''));
            CREATE INDEX IF NOT EXISTS idx_regions_watcher ON regions(watcher_id, sort_order);
            CREATE INDEX IF NOT EXISTS idx_observations_dock_watcher ON observations(watcher_id, image_id) WHERE region_kind = 'dock';
            """);
        EnsureColumns();
    }

    public void Dispose() => Db.Dispose();

    /// <summary>
    /// 영역 세트 · 일시중지 열을 붙인다 (기존 DB 는 그대로 열린다).
    /// regions.valid_from: 이 영역 세트가 적용되기 시작하는 촬영 키('' = 처음부터), regions.baseline: 이 세트의 첫 프레임을 '새 출발점'으로 본다(1).
    /// watchers.paused / paused_at: 캐릭터 단위 감시 일시중지.
    /// </summary>
    private void EnsureColumns()
    {
        void Ensure(string table, string column, string ddl)
        {
            if (Db.Query($"PRAGMA table_info({table})").Any(r => string.Equals(r.Str("name"), column, StringComparison.OrdinalIgnoreCase))) return;
            Db.Exec($"ALTER TABLE {table} ADD COLUMN {column} {ddl}");
        }
        Ensure("regions", "valid_from", "TEXT NOT NULL DEFAULT ''");
        Ensure("regions", "baseline", "INTEGER NOT NULL DEFAULT 0");
        Ensure("watchers", "paused", "INTEGER NOT NULL DEFAULT 0");
        Ensure("watchers", "paused_at", "TEXT");
    }

    // ---------- 감시 눈깔 ----------

    public List<Watcher> ListWatchers()
    {
        var watchers = Db.Query("SELECT id, label, character_name, watch_type, enabled, region_version, paused, paused_at FROM watchers ORDER BY created_at, rowid");
        return [.. watchers.Select(w =>
        {
            var regions = LatestRegions(w.Str("id")!);
            return new Watcher(w.Str("id")!, w.Str("label")!, w.Str("character_name")!, Names.ToWatchType(w.Str("watch_type")!), w.Bool("enabled"), (int)w.Long("region_version"), regions,
                w.Bool("paused"), w.Str("paused_at"));
        })];
    }

    /// <summary>눈깔의 가장 최근 영역 세트 (수정 창에 불러오는 것 · 지금 적용 중인 영역).</summary>
    public List<RegionDef> LatestRegions(string watcherId) =>
        [.. Db.Query("""
            SELECT kind, x, y, width, height FROM regions
            WHERE watcher_id = ? AND valid_from = (SELECT MAX(valid_from) FROM regions WHERE watcher_id = ?) ORDER BY sort_order
            """, watcherId, watcherId)
            .Select(r => new RegionDef(Names.ToRegionKind(r.Str("kind")!), r.Dbl("x") ?? 0, r.Dbl("y") ?? 0, r.Dbl("width") ?? 0, r.Dbl("height") ?? 0))];

    /// <summary>
    /// 감시 눈깔을 저장한다. 이미 있으면 갱신. 캐릭터 · 감시 타입 · 인식 영역이 바뀌었으면 같은 캐릭터의 분석 결과를 모두 지우고 그 캐릭터의 이미지를 처음부터 다시 분석하게 한다
    /// (영역이 바뀌면 이전 판정이 맞지 않으므로). 이름만 바뀐 경우에는 분석 결과를 그대로 둔다.
    /// </summary>
    public void SaveWatcher(Watcher watcher)
    {
        // 이름 등 인식 결과에 영향이 없는 값만 바뀌었으면 분석 결과를 건드리지 않는다 (캐릭터 · 감시 타입 · 인식 영역이 그대로).
        var existing = ListWatchers().FirstOrDefault(w => w.Id == watcher.Id);
        if (existing != null && existing.RegionVersion >= 2 && existing.Character == watcher.Character && existing.WatchType == watcher.WatchType && SameRegions(existing.Regions, watcher.Regions))
        {
            Db.Exec("UPDATE watchers SET label = ?, enabled = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?", watcher.Label, watcher.Enabled ? 1 : 0, watcher.Id);
            return;
        }

        Db.Transaction(() =>
        {
            var previous = Db.One("SELECT character_name FROM watchers WHERE id = ?", watcher.Id);
            if (previous != null && previous.Str("character_name") != watcher.Character)
                ClearWatcherData(watcher.Id);

            Db.Exec("""
                INSERT INTO watchers (id, label, character_name, watch_type, enabled, region_version, updated_at)
                VALUES (?, ?, ?, ?, ?, 2, CURRENT_TIMESTAMP)
                ON CONFLICT(id) DO UPDATE SET label = excluded.label, character_name = excluded.character_name, watch_type = excluded.watch_type,
                  enabled = excluded.enabled, region_version = 2, paused = 0, paused_at = NULL, updated_at = CURRENT_TIMESTAMP
                """, watcher.Id, watcher.Label, watcher.Character, watcher.WatchType.Db(), watcher.Enabled ? 1 : 0);
            Db.Exec("DELETE FROM regions WHERE watcher_id = ?", watcher.Id);
            for (int i = 0; i < watcher.Regions.Count; i++)
            {
                var r = watcher.Regions[i];
                Db.Exec("INSERT INTO regions (watcher_id, kind, x, y, width, height, sort_order) VALUES (?, ?, ?, ?, ?, ?, ?)", watcher.Id, r.Kind.Db(), r.X, r.Y, r.W, r.H, i);
            }
            foreach (var table in new[] { "events", "observations", "current_objects", "current_signatures", "dock_credits" })
                Db.Exec($"DELETE FROM {table} WHERE watcher_id IN (SELECT id FROM watchers WHERE character_name = ?)", watcher.Character);
            Db.Exec("UPDATE images SET processing_status = 'pending' WHERE character_name = ?", watcher.Character);
        });
    }

    private void ClearWatcherData(string watcherId)
    {
        foreach (var table in new[] { "events", "observations", "current_objects", "current_signatures", "dock_credits" })
            Db.Exec($"DELETE FROM {table} WHERE watcher_id = ?", watcherId);
    }

    /// <summary>감시 눈깔과 그 분석 기록을 지운다 (원본 이미지는 그대로). 지운 눈깔을 돌려준다.</summary>
    public Watcher? DeleteWatcher(string watcherId)
    {
        Watcher? removed = null;
        Db.Transaction(() =>
        {
            removed = ListWatchers().FirstOrDefault(w => w.Id == watcherId);
            if (removed == null) return;
            ClearWatcherData(watcherId);
            Db.Exec("DELETE FROM watchers WHERE id = ?", watcherId);
        });
        return removed;
    }

    // ---------- 이미지 ----------

    /// <summary>이미지를 등록한다. 이미 있으면 false.</summary>
    public bool AddImage(string folder, string filePath, string filename, string character, string captureKey, string capturedAt, long size, long modifiedMs) =>
        Db.Exec("INSERT OR IGNORE INTO images (folder_path, file_path, filename, character_name, capture_key, captured_at, size_bytes, modified_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            folder, filePath, filename, character, captureKey, capturedAt, size, modifiedMs) > 0;

    public void ResetDerivedData()
    {
        Db.Transaction(() =>
        {
            foreach (var table in new[] { "events", "observations", "current_objects", "current_signatures", "dock_credits", "images" }) Db.Exec($"DELETE FROM {table}");
        });
    }

    /// <summary>폴더에서 사라진 이미지를 기록에서 지운다 (그 이미지에서 나온 이벤트도 함께). 지운 이미지 id 들을 돌려준다.</summary>
    public List<long> RemoveMissingImages(string folder, ISet<string> existingPaths)
    {
        var removed = new List<long>();
        foreach (var row in Db.Query("SELECT id, file_path FROM images WHERE folder_path = ?", folder))
        {
            if (existingPaths.Contains(row.Str("file_path")!)) continue;
            Db.Exec("DELETE FROM events WHERE image_id = ?", row.Long("id"));
            Db.Exec("DELETE FROM images WHERE id = ?", row.Long("id"));
            removed.Add(row.Long("id"));
        }
        return removed;
    }

    /// <summary>이미 등록된 이미지 경로들 (폴더 안).</summary>
    public HashSet<string> KnownImagePaths(string folder) => [.. Db.Query("SELECT file_path FROM images WHERE folder_path = ?", folder).Select(r => r.Str("file_path")!)];

    /// <summary>처리 중이던 이미지를 다시 대기 상태로 돌린다 (비전 모델에 연결하지 못했을 때).</summary>
    public void MarkPending(long imageId) => Db.Exec("UPDATE images SET processing_status = 'pending' WHERE id = ?", imageId);

    /// <summary>프로그램이 처리 도중 꺼졌다면 '처리 중'으로 남은 이미지를 대기로 되돌린다.</summary>
    public void ResetStuckProcessing() => Db.Exec("UPDATE images SET processing_status = 'pending' WHERE processing_status = 'processing'");

    /// <summary>이 id 보다 큰 이벤트들 (새로 판정된 것).</summary>
    public List<EventRow> EventsAfter(long lastId) =>
        [.. Db.Query("""
            SELECT e.id, e.event_time, e.event_type, e.character_name, e.corporation_ticker, e.ship_name, e.speed_mps, e.confidence, e.image_id, e.previous_image_id,
                   e.details_json, w.id AS watcher_id, w.label AS watcher_label, i.filename, pi.filename AS previous_filename
            FROM events e LEFT JOIN watchers w ON w.id = e.watcher_id LEFT JOIN images i ON i.id = e.image_id LEFT JOIN images pi ON pi.id = e.previous_image_id
            WHERE e.id > ? ORDER BY e.id
            """, lastId).Select(r => new EventRow(r.Long("id"), r.Str("event_time")!, r.Str("event_type")!, r.Str("character_name"), r.Str("corporation_ticker"), r.Str("ship_name"),
                r.Dbl("speed_mps"), r.Dbl("confidence"), r.LongN("image_id"), r.LongN("previous_image_id"), CctvJson.ParseObject(r.Str("details_json")), r.Str("watcher_id"), r.Str("watcher_label"),
                r.Str("filename"), r.Str("previous_filename")))];

    public long MaxEventId() => Db.One("SELECT COALESCE(MAX(id), 0) AS m FROM events")?.Long("m") ?? 0;

    /// <summary>
    /// 처리할 다음 이미지: 활성 감시 눈깔이 있는 캐릭터의 대기 중 이미지 중 가장 오래된 것.
    /// 이 검색은 '대기 중'이라는 이미지 상태만 본다 — 처리 중 상태는 이 호출이 바꾸지 않는다.
    /// </summary>
    internal ImageRow? NextPendingImage()
    {
        var r = Db.One("""
            SELECT i.id, i.file_path, i.filename, i.character_name, i.capture_key, i.captured_at FROM images i
            WHERE i.processing_status = 'pending'
              AND EXISTS (SELECT 1 FROM watchers w WHERE w.character_name = i.character_name AND w.enabled = 1 AND w.paused = 0 AND w.region_version >= 2)
            ORDER BY i.capture_key LIMIT 1
            """);
        return r == null ? null : new ImageRow(r.Long("id"), r.Str("file_path")!, r.Str("filename")!, r.Str("character_name")!, r.Str("capture_key")!, r.Str("captured_at")!);
    }

    public void MarkProcessing(long imageId) => Db.Exec("UPDATE images SET processing_status = 'processing' WHERE id = ?", imageId);
    public string? ImageStatus(long imageId) => Db.One("SELECT processing_status FROM images WHERE id = ?", imageId)?.Str("processing_status");

    /// <summary>
    /// 이 캐릭터의 활성(일시중지 아님) 눈깔들의 영역. captureKey 를 주면 그 촬영 시각에 유효했던 영역 세트를 쓴다(영역을 중간에 바꿔도 예전 이미지는 옛 영역으로 읽는다).
    /// </summary>
    internal List<WatcherRegion> WatcherRegions(string character, string? captureKey = null) =>
        [.. Db.Query("""
            SELECT w.id, w.label, w.watch_type, r.kind, r.x, r.y, r.width, r.height, r.sort_order FROM watchers w
            JOIN regions r ON r.watcher_id = w.id
            WHERE w.character_name = ? AND w.enabled = 1 AND w.paused = 0 AND w.region_version >= 2
              AND r.valid_from = (SELECT MAX(r2.valid_from) FROM regions r2 WHERE r2.watcher_id = w.id AND r2.valid_from <= ?)
            ORDER BY w.created_at, w.rowid, r.sort_order
            """, character, captureKey ?? "\uffff").Select(r => new WatcherRegion(r.Str("id")!, r.Str("label")!, Names.ToWatchType(r.Str("watch_type")!), Names.ToRegionKind(r.Str("kind")!),
                r.Dbl("x") ?? 0, r.Dbl("y") ?? 0, r.Dbl("width") ?? 0, r.Dbl("height") ?? 0, (int)r.Long("sort_order")))];

    /// <summary>이 눈깔의 그 촬영 시각에 유효한 영역 세트가 언제부터인지(valid_from)와 그 첫 프레임을 새 출발점으로 볼지(baseline).</summary>
    internal (string ValidFrom, bool Baseline) RegionEpoch(string watcherId, string captureKey)
    {
        var r = Db.One("SELECT valid_from, baseline FROM regions WHERE watcher_id = ? AND valid_from <= ? ORDER BY valid_from DESC LIMIT 1", watcherId, captureKey);
        return r == null ? ("", false) : (r.Str("valid_from") ?? "", r.Bool("baseline"));
    }

    public bool WatcherEnabled(string watcherId) => Db.One("SELECT 1 AS ok FROM watchers WHERE id = ? AND enabled = 1 AND paused = 0", watcherId) != null;

    /// <summary>한 이미지의 관측을 저장하고 '처리 완료'로 표시한다 (이전 관측은 지운다).</summary>
    internal void CompleteImage(long imageId, IEnumerable<Observation> observations)
    {
        Db.Transaction(() =>
        {
            Db.Exec("DELETE FROM observations WHERE image_id = ?", imageId);
            foreach (var o in observations)
                Db.Exec("INSERT INTO observations (image_id, watcher_id, region_kind, payload_json, confidence) VALUES (?, ?, ?, ?, ?)",
                    imageId, o.WatcherId, o.Kind.Db(), CctvJson.Serialize(o.Payload), o.Confidence);
            Db.Exec("UPDATE images SET processing_status = 'processed' WHERE id = ?", imageId);
        });
    }

    public void FailImage(long imageId) => Db.Exec("UPDATE images SET processing_status = 'failed' WHERE id = ?", imageId);

    // ---------- 감시 일시중지 · 재시작 ----------

    /// <summary>촬영 시각 문자열(ISO)을 이미지의 촬영 키(숫자만)로: "2026-10-02T00:12:21.550" → "20261002001221550".</summary>
    internal static string KeyOf(string iso) => new([.. iso.Where(char.IsDigit)]);

    /// <summary>시각 표식 이벤트(일시중지 · 재시작 · 영역 변경)를 타임라인에 남긴다. 분석으로 만든 이벤트가 아니므로 되감기에서도 지우지 않는다.</summary>
    internal void InsertMarker(string watcherId, string time, string type, JsonObject? details = null) =>
        Db.Exec("INSERT OR IGNORE INTO events (event_time, event_type, watcher_id, details_json) VALUES (?, ?, ?, ?)", time, type, watcherId, CctvJson.Compact(details ?? new JsonObject()));

    internal static bool IsMarker(string type) => type is "watch_paused" or "watch_resumed" or "region_changed";

    /// <summary>이 캐릭터의 감시를 일시중지한다 (그 캐릭터의 모든 눈깔). 이미 중지 중이면 아무것도 하지 않는다. 시각은 지금.</summary>
    public bool PauseCharacter(string character, DateTime now)
    {
        var at = now.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        var changed = false;
        Db.Transaction(() =>
        {
            // 타임라인 표식의 시각은 시계가 아니라 CCTV 이미지 기준: 마지막으로 인식(분석)한 이미지의 촬영 시각. 실제로 누른 시각은 details 에 따로 남긴다.
            var markerTime = Db.One("SELECT captured_at FROM images WHERE character_name = ? AND processing_status = 'processed' ORDER BY capture_key DESC LIMIT 1", character)?.Str("captured_at")
                ?? Db.One("SELECT captured_at FROM images WHERE character_name = ? ORDER BY capture_key DESC LIMIT 1", character)?.Str("captured_at") ?? at;
            foreach (var w in Db.Query("SELECT id FROM watchers WHERE character_name = ? AND paused = 0", character))
            {
                Db.Exec("UPDATE watchers SET paused = 1, paused_at = ? WHERE id = ?", at, w.Str("id"));
                InsertMarker(w.Str("id")!, markerTime, "watch_paused", new JsonObject { ["pausedAt"] = at });
                changed = true;
            }
        });
        return changed;
    }

    /// <summary>이 캐릭터가 마지막으로 분석(인식)한 이미지. 없으면 null.</summary>
    internal ImageRow? LastProcessedImage(string character)
    {
        var r = Db.One("SELECT id, file_path, filename, character_name, capture_key, captured_at FROM images WHERE character_name = ? AND processing_status = 'processed' ORDER BY capture_key DESC LIMIT 1", character);
        return r == null ? null : new ImageRow(r.Long("id"), r.Str("file_path")!, r.Str("filename")!, r.Str("character_name")!, r.Str("capture_key")!, r.Str("captured_at")!);
    }

    public sealed record RestartImage(ImageRow Image, string Status);

    /// <summary>
    /// 재시작 지점을 고르는 목록: 기준 키(마지막으로 분석한 이미지) 이전 <paramref name="before"/> 장과 그 이후의 이미지(오래된 것부터 최대 <paramref name="afterLimit"/> 장), 오래된 것부터.
    /// </summary>
    internal (List<RestartImage> Before, List<RestartImage> After) ImagesAround(string character, string pausedKey, int before = 25, int afterLimit = 1000)
    {
        RestartImage Map(Row r) => new(new ImageRow(r.Long("id"), r.Str("file_path")!, r.Str("filename")!, r.Str("character_name")!, r.Str("capture_key")!, r.Str("captured_at")!), r.Str("processing_status") ?? "");
        var b = Db.Query("SELECT id, file_path, filename, character_name, capture_key, captured_at, processing_status FROM images WHERE character_name = ? AND capture_key <= ? ORDER BY capture_key DESC LIMIT ?", character, pausedKey, before).Select(Map).Reverse().ToList();
        var a = Db.Query("SELECT id, file_path, filename, character_name, capture_key, captured_at, processing_status FROM images WHERE character_name = ? AND capture_key > ? ORDER BY capture_key LIMIT ?", character, pausedKey, afterLimit).Select(Map).ToList();
        return (b, a);
    }

    public sealed record RestartPlan(string Character, string StartKey, List<string> WatcherIds, bool Rollback);

    /// <summary>
    /// 감시를 startKey 부터 다시 시작할 준비를 한다. 일시중지는 호출한 쪽이 상태 복원을 마친 뒤 <see cref="ResumeCharacter"/> 로 푼다. (그 캐릭터의 모든 눈깔).
    /// - 새 영역 세트(valid_from = startKey, 첫 프레임은 새 출발점)를 만든다: newRegions 에 있는 눈깔은 새 영역, 나머지는 지금 영역 그대로.
    /// - startKey 이전의 대기 이미지는 '건너뜀', startKey 이후 이미지는 다시 대기(이미 분석했다면 그 관측을 지우고 다시 읽는다).
    /// - startKey 이후까지 분석한 결과가 있었으면 되감기가 필요하다: 그 눈깔들의 분석 이벤트(표식 제외)와 현재 상태를 지운다. 돌려준 계획으로 호출한 쪽이 startKey 이전 이미지를 다시 분석기에 넣어 상태를 복원한다.
    /// - '재시작' 표식(과 영역이 바뀐 눈깔에는 '영역 변경' 표식)을 남긴다.
    /// </summary>
    internal RestartPlan ApplyRestartPoint(string character, string startKey, string startTime, IReadOnlyDictionary<string, IReadOnlyList<RegionDef>> newRegions, string? startImageName, string? resumedAt = null)
    {
        RestartPlan? plan = null;
        Db.Transaction(() =>
        {
            var watcherIds = Db.Query("SELECT id FROM watchers WHERE character_name = ? ORDER BY created_at, rowid", character).Select(r => r.Str("id")!).ToList();
            var rollback = Db.One("SELECT 1 AS ok FROM images WHERE character_name = ? AND capture_key >= ? AND processing_status = 'processed'", character, startKey) != null;

            foreach (var id in watcherIds)
            {
                var changed = newRegions.TryGetValue(id, out var fresh) && !SameRegions(fresh, LatestRegions(id));
                var set = newRegions.TryGetValue(id, out var given) ? new List<RegionDef>(given) : LatestRegions(id);
                // 이 시점 이후의 영역 세트는 되감기와 함께 사라진다. 같은 시점의 세트는 새로 덮는다.
                Db.Exec("DELETE FROM regions WHERE watcher_id = ? AND valid_from >= ?", id, startKey);
                for (int i = 0; i < set.Count; i++)
                    Db.Exec("INSERT INTO regions (watcher_id, kind, x, y, width, height, sort_order, valid_from, baseline) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1)",
                        id, set[i].Kind.Db(), set[i].X, set[i].Y, set[i].W, set[i].H, i, startKey);
                if (rollback)
                    foreach (var table in new[] { "current_objects", "current_signatures", "dock_credits" }) Db.Exec($"DELETE FROM {table} WHERE watcher_id = ?", id);

                // 되감기: 분석이 만든 이벤트는 모두 지운다 (호출한 쪽이 startKey 이전 이미지를 다시 분석해 그 앞의 이벤트를 똑같이 다시 만든다).
                // 표식은 startKey 이전 것만 남긴다. 되감기가 아니면 이미 지나간 이 시점 이후의 재시작·영역 변경 표식만 정리한다.
                if (rollback) Db.Exec("DELETE FROM events WHERE watcher_id = ? AND (event_time >= ? OR event_type NOT IN ('watch_paused', 'watch_resumed', 'region_changed'))", id, startTime);
                else Db.Exec("DELETE FROM events WHERE watcher_id = ? AND event_type IN ('watch_resumed', 'region_changed') AND event_time >= ?", id, startTime);

                InsertMarker(id, startTime, "watch_resumed", new JsonObject { ["startKey"] = startKey, ["startImage"] = startImageName, ["rollback"] = rollback, ["regionsChanged"] = changed, ["resumedAt"] = resumedAt });
                if (changed) InsertMarker(id, startTime, "region_changed", new JsonObject { ["startKey"] = startKey, ["regions"] = set.Count });
            }

            // 일시중지 중에 쌓인 이미지: 시작 이전은 건너뛰고, 시작 이후는 다시 읽게 한다.
            Db.Exec("UPDATE images SET processing_status = 'skipped' WHERE character_name = ? AND capture_key < ? AND processing_status IN ('pending', 'processing')", character, startKey);
            Db.Exec("DELETE FROM observations WHERE watcher_id IN (SELECT id FROM watchers WHERE character_name = ?) AND image_id IN (SELECT id FROM images WHERE character_name = ? AND capture_key >= ?)", character, character, startKey);
            Db.Exec("UPDATE images SET processing_status = 'pending' WHERE character_name = ? AND capture_key >= ?", character, startKey);
            plan = new RestartPlan(character, startKey, watcherIds, rollback);
        });
        return plan!;
    }

    /// <summary>이름과 감시 타입만 바꾼다 (재시작 창). 캐릭터와 영역은 건드리지 않는다.</summary>
    internal void UpdateWatcherMeta(string watcherId, string label, WatchType type) =>
        Db.Exec("UPDATE watchers SET label = ?, watch_type = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?", label, type.Db(), watcherId);

    /// <summary>이 촬영 키 이후에 이미 분석한 이미지가 있는가 (그 지점부터 시작하면 되감기가 필요하다).</summary>
    internal bool HasProcessedFrom(string character, string key) =>
        Db.One("SELECT 1 AS ok FROM images WHERE character_name = ? AND capture_key >= ? AND processing_status = 'processed'", character, key) != null;

    /// <summary>일시중지를 푼다 (되감기 복원이 끝난 뒤 호출 — 그 전에는 분석이 이 캐릭터의 이미지를 집어 가지 않는다).</summary>
    internal void ResumeCharacter(string character) => Db.Exec("UPDATE watchers SET paused = 0, paused_at = NULL WHERE character_name = ?", character);

    internal bool IsPaused(string character) => Db.One("SELECT 1 AS ok FROM watchers WHERE character_name = ? AND paused = 1", character) != null;

    internal static bool SameRegions(IReadOnlyList<RegionDef> a, IReadOnlyList<RegionDef> b)
    {
        if (a.Count != b.Count) return false;
        static bool Near(double x, double y) => Math.Abs(x - y) < 0.05;
        return a.Zip(b).All(p => p.First.Kind == p.Second.Kind && Near(p.First.X, p.Second.X) && Near(p.First.Y, p.Second.Y) && Near(p.First.W, p.Second.W) && Near(p.First.H, p.Second.H));
    }

    // ---------- 화면용 조회 ----------

    public ProcessingCounts Counts()
    {
        var rows = Db.Query("""
            SELECT processing_status AS status, COUNT(*) AS count FROM images i
            WHERE EXISTS (SELECT 1 FROM watchers w WHERE w.character_name = i.character_name AND w.enabled = 1 AND w.region_version >= 2)
              AND (processing_status != 'pending' OR EXISTS (SELECT 1 FROM watchers w WHERE w.character_name = i.character_name AND w.enabled = 1 AND w.paused = 0 AND w.region_version >= 2))
            GROUP BY processing_status
            """).ToDictionary(r => r.Str("status")!, r => (int)r.Long("count"));
        return new ProcessingCounts(rows.GetValueOrDefault("pending"), rows.GetValueOrDefault("processing"), rows.GetValueOrDefault("processed"), rows.GetValueOrDefault("failed"));
    }

    public int ImageCount(string folder) => (int)Db.Count("""
        SELECT COUNT(*) AS count FROM images i WHERE i.folder_path = ?
          AND EXISTS (SELECT 1 FROM watchers w WHERE w.character_name = i.character_name AND w.enabled = 1 AND w.region_version >= 2)
        """, folder);

    internal List<CharacterStat> CharacterStats(string folder) =>
        [.. Db.Query("""
            SELECT i.character_name AS name, COUNT(*) AS image_count, MAX(i.captured_at) AS latest_capture_at,
              (SELECT i2.id FROM images i2 WHERE i2.folder_path = i.folder_path AND i2.character_name = i.character_name ORDER BY i2.capture_key DESC LIMIT 1) AS latest_image_id
            FROM images i WHERE i.folder_path = ? GROUP BY i.character_name ORDER BY i.character_name
            """, folder).Select(r => new CharacterStat(r.Str("name")!, (int)r.Long("image_count"), r.Str("latest_capture_at") ?? "", r.Long("latest_image_id")))];

    public string? LatestCaptureAt(string folder) => Db.One("SELECT captured_at FROM images WHERE folder_path = ? ORDER BY capture_key DESC LIMIT 1", folder)?.Str("captured_at");

    public List<EventRow> Events(int limit = 100) =>
        [.. Db.Query("""
            SELECT e.id, e.event_time, e.event_type, e.character_name, e.corporation_ticker, e.ship_name, e.speed_mps, e.confidence, e.image_id, e.previous_image_id,
                   e.details_json, w.id AS watcher_id, w.label AS watcher_label, i.filename, pi.filename AS previous_filename
            FROM events e LEFT JOIN watchers w ON w.id = e.watcher_id LEFT JOIN images i ON i.id = e.image_id LEFT JOIN images pi ON pi.id = e.previous_image_id
            ORDER BY e.event_time DESC, e.id DESC LIMIT ?
            """, Math.Clamp(limit, 1, 2000)).Select(r => new EventRow(r.Long("id"), r.Str("event_time")!, r.Str("event_type")!, r.Str("character_name"), r.Str("corporation_ticker"), r.Str("ship_name"),
                r.Dbl("speed_mps"), r.Dbl("confidence"), r.LongN("image_id"), r.LongN("previous_image_id"), CctvJson.ParseObject(r.Str("details_json")), r.Str("watcher_id"), r.Str("watcher_label"),
                r.Str("filename"), r.Str("previous_filename")))];

    public List<CurrentObject> CurrentObjects() =>
        [.. Db.Query("""
            SELECT o.watcher_id, w.label AS watcher_label, o.character_name, o.ship_name, o.corporation_ticker, o.distance_text, o.speed_mps, o.confidence, o.last_seen_at, o.image_id, o.entry_type, o.first_seen_at
            FROM current_objects o JOIN watchers w ON w.id = o.watcher_id ORDER BY w.label, o.character_name
            """).Select(r => new CurrentObject(r.Str("watcher_id")!, r.Str("watcher_label")!, r.Str("character_name")!, r.Str("ship_name"), r.Str("corporation_ticker"), r.Str("distance_text"),
                r.Dbl("speed_mps"), r.Dbl("confidence"), r.Str("last_seen_at")!, r.Long("image_id"), r.Str("entry_type"), r.Str("first_seen_at")))];

    public List<CurrentSignature> CurrentSignatures() =>
        [.. Db.Query("""
            SELECT s.watcher_id, w.label AS watcher_label, s.signature_id, s.name, s.group_name, s.distance_text, s.confidence, s.first_seen_at, s.last_seen_at, s.image_id
            FROM current_signatures s JOIN watchers w ON w.id = s.watcher_id ORDER BY w.label, s.signature_id
            """).Select(r => new CurrentSignature(r.Str("watcher_id")!, r.Str("watcher_label")!, r.Str("signature_id")!, r.Str("name"), r.Str("group_name"), r.Str("distance_text"),
                r.Dbl("confidence"), r.Str("first_seen_at")!, r.Str("last_seen_at")!, r.Long("image_id")))];

    public List<DockPeak> DockPeaks() =>
        [.. Db.Query("""
            SELECT watcherId, watcherLabel, peakCount, capturedAt, imageId, observationId, filename FROM (
              SELECT w.id AS watcherId, w.label AS watcherLabel, json_extract(o.payload_json, '$.fields.dockCount') AS peakCount,
                     i.captured_at AS capturedAt, i.id AS imageId, o.id AS observationId, i.filename,
                     ROW_NUMBER() OVER (PARTITION BY w.id ORDER BY json_extract(o.payload_json, '$.fields.dockCount') DESC, i.capture_key DESC, o.id DESC) AS peakRank
              FROM observations o JOIN watchers w ON w.id = o.watcher_id JOIN images i ON i.id = o.image_id
              WHERE w.watch_type = 'structure' AND w.enabled = 1 AND o.region_kind = 'dock'
                AND json_type(o.payload_json, '$.fields.dockCount') = 'integer' AND json_extract(o.payload_json, '$.fields.dockCount') >= 0
            ) WHERE peakRank = 1 ORDER BY peakCount DESC, capturedAt DESC, watcherLabel
            """).Select(r => new DockPeak(r.Str("watcherId")!, r.Str("watcherLabel")!, (int)r.Long("peakCount"), r.Str("capturedAt")!, r.Long("imageId"), r.Long("observationId"), r.Str("filename")!))];

    /// <summary>눈깔·영역 종류마다 가장 최근 관측이 실패(헤더/숫자를 못 읽음)한 것들.</summary>
    public List<RegionWarning> RegionWarnings()
    {
        var latest = Db.Query("""
            SELECT o.watcher_id, w.label AS watcher_label, o.region_kind, o.payload_json, i.filename FROM observations o
            JOIN watchers w ON w.id = o.watcher_id JOIN images i ON i.id = o.image_id
            WHERE o.id = (SELECT o2.id FROM observations o2 JOIN images i2 ON i2.id = o2.image_id
                          WHERE o2.watcher_id = o.watcher_id AND o2.region_kind = o.region_kind ORDER BY i2.capture_key DESC, o2.id DESC LIMIT 1)
            """);
        var list = new List<RegionWarning>();
        foreach (var row in latest)
        {
            var fields = CctvJson.Deserialize<RegionPayload>(row.Str("payload_json"))?.Fields ?? new RegionFields();
            var kind = Names.ToRegionKind(row.Str("region_kind")!);
            string? message = kind switch
            {
                RegionKind.Overview when fields.OverviewDetected == false => "오버뷰 헤더를 찾지 못했습니다.",
                RegionKind.Probe when fields.ProbeDetected == false => "프로빙 창의 ID·이름 헤더를 찾지 못했습니다.",
                RegionKind.Dock when fields.DockCount == null => "도킹 숫자를 읽지 못했습니다.",
                _ => null,
            };
            if (message != null) list.Add(new RegionWarning(row.Str("watcher_id")!, row.Str("watcher_label")!, kind, message, row.Str("filename")!));
        }
        return list;
    }

    internal ImageDetail? Image(long imageId)
    {
        var r = Db.One("SELECT id, file_path, filename, character_name, capture_key, captured_at, processing_status FROM images WHERE id = ?", imageId);
        if (r == null) return null;
        var image = new ImageRow(r.Long("id"), r.Str("file_path")!, r.Str("filename")!, r.Str("character_name")!, r.Str("capture_key")!, r.Str("captured_at")!);
        var obs = Db.Query("SELECT id, watcher_id, region_kind, payload_json, confidence FROM observations WHERE image_id = ? ORDER BY id", imageId)
            .Select(o => (o.Long("id"), o.Str("watcher_id") ?? "", Names.ToRegionKind(o.Str("region_kind")!), CctvJson.Deserialize<RegionPayload>(o.Str("payload_json")) ?? new RegionPayload(), o.Dbl("confidence"))).ToList();
        return new ImageDetail(image, r.Str("processing_status")!, obs);
    }

    public string? ImagePath(long imageId) => Db.One("SELECT file_path FROM images WHERE id = ?", imageId)?.Str("file_path");
}
