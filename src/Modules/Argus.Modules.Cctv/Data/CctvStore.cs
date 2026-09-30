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
            CREATE INDEX IF NOT EXISTS idx_images_folder_capture ON images(folder_path, capture_key DESC);
            CREATE INDEX IF NOT EXISTS idx_images_character_capture ON images(character_name, capture_key DESC);
            CREATE INDEX IF NOT EXISTS idx_images_status_capture ON images(processing_status, capture_key);
            CREATE INDEX IF NOT EXISTS idx_events_time ON events(event_time DESC);
            CREATE INDEX IF NOT EXISTS idx_events_watcher_time ON events(watcher_id, event_time DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_events_dedupe ON events(watcher_id, event_time, event_type, IFNULL(character_name, ''), IFNULL(details_json, ''));
            CREATE INDEX IF NOT EXISTS idx_regions_watcher ON regions(watcher_id, sort_order);
            CREATE INDEX IF NOT EXISTS idx_observations_dock_watcher ON observations(watcher_id, image_id) WHERE region_kind = 'dock';
            """);
    }

    public void Dispose() => Db.Dispose();

    // ---------- 설정 (키-값, JSON) ----------

    public T? GetSetting<T>(string key)
    {
        var row = Db.One("SELECT value FROM settings WHERE key = ?", key);
        return row == null ? default : CctvJson.Deserialize<T>(row.Str("value"));
    }

    public void SetSetting<T>(string key, T value) =>
        Db.Exec("INSERT INTO settings (key, value, updated_at) VALUES (?, ?, CURRENT_TIMESTAMP) ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = CURRENT_TIMESTAMP", key, CctvJson.Serialize(value));

    // ---------- 감시 눈깔 ----------

    public List<Watcher> ListWatchers()
    {
        var watchers = Db.Query("SELECT id, label, character_name, watch_type, enabled, region_version FROM watchers ORDER BY created_at, rowid");
        return [.. watchers.Select(w =>
        {
            var regions = Db.Query("SELECT kind, x, y, width, height FROM regions WHERE watcher_id = ? ORDER BY sort_order", w.Str("id"))
                .Select(r => new RegionDef(Names.ToRegionKind(r.Str("kind")!), r.Dbl("x") ?? 0, r.Dbl("y") ?? 0, r.Dbl("width") ?? 0, r.Dbl("height") ?? 0)).ToList();
            return new Watcher(w.Str("id")!, w.Str("label")!, w.Str("character_name")!, Names.ToWatchType(w.Str("watch_type")!), w.Bool("enabled"), (int)w.Long("region_version"), regions);
        })];
    }

    /// <summary>
    /// 감시 눈깔을 저장한다. 이미 있으면 갱신. 같은 캐릭터의 분석 결과는 모두 지우고 그 캐릭터의 이미지를 처음부터 다시 분석하게 한다
    /// (영역이 바뀌면 이전 판정이 맞지 않으므로).
    /// </summary>
    public void SaveWatcher(Watcher watcher)
    {
        Db.Transaction(() =>
        {
            var previous = Db.One("SELECT character_name FROM watchers WHERE id = ?", watcher.Id);
            if (previous != null && previous.Str("character_name") != watcher.Character)
                ClearWatcherData(watcher.Id);

            Db.Exec("""
                INSERT INTO watchers (id, label, character_name, watch_type, enabled, region_version, updated_at)
                VALUES (?, ?, ?, ?, ?, 2, CURRENT_TIMESTAMP)
                ON CONFLICT(id) DO UPDATE SET label = excluded.label, character_name = excluded.character_name, watch_type = excluded.watch_type,
                  enabled = excluded.enabled, region_version = 2, updated_at = CURRENT_TIMESTAMP
                """, watcher.Id, watcher.Label, watcher.Character, watcher.WatchType.Db(), watcher.Enabled ? 1 : 0);
            Db.Exec("DELETE FROM regions WHERE watcher_id = ?", watcher.Id);
            for (int i = 0; i < watcher.Regions.Count; i++)
            {
                var r = watcher.Regions[i];
                Db.Exec("INSERT INTO regions (watcher_id, kind, x, y, width, height, sort_order) VALUES (?, ?, ?, ?, ?, ?, ?)", watcher.Id, r.Kind.Db(), r.X, r.Y, r.W, r.H, i);
            }
            foreach (var table in new[] { "events", "observations", "current_objects", "current_signatures" })
                Db.Exec($"DELETE FROM {table} WHERE watcher_id IN (SELECT id FROM watchers WHERE character_name = ?)", watcher.Character);
            Db.Exec("UPDATE images SET processing_status = 'pending' WHERE character_name = ?", watcher.Character);
        });
    }

    private void ClearWatcherData(string watcherId)
    {
        foreach (var table in new[] { "events", "observations", "current_objects", "current_signatures" })
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
            foreach (var table in new[] { "events", "observations", "current_objects", "current_signatures", "images" }) Db.Exec($"DELETE FROM {table}");
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
              AND EXISTS (SELECT 1 FROM watchers w WHERE w.character_name = i.character_name AND w.enabled = 1 AND w.region_version >= 2)
            ORDER BY i.capture_key LIMIT 1
            """);
        return r == null ? null : new ImageRow(r.Long("id"), r.Str("file_path")!, r.Str("filename")!, r.Str("character_name")!, r.Str("capture_key")!, r.Str("captured_at")!);
    }

    public void MarkProcessing(long imageId) => Db.Exec("UPDATE images SET processing_status = 'processing' WHERE id = ?", imageId);
    public string? ImageStatus(long imageId) => Db.One("SELECT processing_status FROM images WHERE id = ?", imageId)?.Str("processing_status");

    internal List<WatcherRegion> WatcherRegions(string character) =>
        [.. Db.Query("""
            SELECT w.id, w.label, w.watch_type, r.kind, r.x, r.y, r.width, r.height, r.sort_order FROM watchers w
            JOIN regions r ON r.watcher_id = w.id
            WHERE w.character_name = ? AND w.enabled = 1 AND w.region_version >= 2
            ORDER BY w.created_at, w.rowid, r.sort_order
            """, character).Select(r => new WatcherRegion(r.Str("id")!, r.Str("label")!, Names.ToWatchType(r.Str("watch_type")!), Names.ToRegionKind(r.Str("kind")!),
                r.Dbl("x") ?? 0, r.Dbl("y") ?? 0, r.Dbl("width") ?? 0, r.Dbl("height") ?? 0, (int)r.Long("sort_order")))];

    public bool WatcherEnabled(string watcherId) => Db.One("SELECT 1 AS ok FROM watchers WHERE id = ? AND enabled = 1", watcherId) != null;

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

    public void FailImage(long imageId, string message)
    {
        Db.Exec("UPDATE images SET processing_status = 'failed' WHERE id = ?", imageId);
        SetSetting($"imageError:{imageId}", message);
    }

    // ---------- 화면용 조회 ----------

    public ProcessingCounts Counts()
    {
        var rows = Db.Query("""
            SELECT processing_status AS status, COUNT(*) AS count FROM images i
            WHERE EXISTS (SELECT 1 FROM watchers w WHERE w.character_name = i.character_name AND w.enabled = 1 AND w.region_version >= 2)
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

    public string? ImageError(long imageId) => GetSetting<string>($"imageError:{imageId}");
}
