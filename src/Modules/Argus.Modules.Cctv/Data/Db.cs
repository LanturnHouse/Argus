using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Argus.Modules.Cctv;

/// <summary>SQLite 결과 한 행. 열 이름으로 읽는다 (없거나 NULL 이면 null).</summary>
internal sealed class Row(Dictionary<string, object?> values)
{
    public object? Raw(string name) => values.TryGetValue(name, out var v) ? v : null;
    public string? Str(string name) => Raw(name) switch { null => null, string s => s, var o => Convert.ToString(o, CultureInfo.InvariantCulture) };
    public long Long(string name) => Raw(name) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
    public long? LongN(string name) => Raw(name) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
    public double? Dbl(string name) => Raw(name) is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : null;
    public bool Bool(string name) => Raw(name) is { } v && Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0;
}

/// <summary>
/// SQLite 연결 하나를 잠금으로 감싼 얇은 도우미. SQL 의 '?' 는 순서대로 매개변수가 된다.
/// 한 번에 한 스레드만 쓰고(처리 스레드와 화면 읽기가 번갈아), 쓰기는 BeginTransaction 으로 묶는다.
/// </summary>
internal sealed class Db : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    private long _writes;

    /// <summary>지금까지 실행한 쓰기(Exec)의 횟수. 화면이 "바뀐 게 없으면 다시 읽지 않기"를 판단하는 데 쓴다.</summary>
    public long WriteCount => Interlocked.Read(ref _writes);

    public Db(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Private }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode = WAL");
        Exec("PRAGMA foreign_keys = ON");
        Exec("PRAGMA busy_timeout = 5000");
    }

    /// <summary>여러 호출을 한 번의 잠금 안에서 실행한다 (처리 스레드가 읽기-쓰기 사이에 끼어들림을 막는다).</summary>
    public T Locked<T>(Func<T> action) { lock (_lock) return action(); }
    public void Locked(Action action) { lock (_lock) action(); }

    private SqliteCommand Command(string sql, object?[] args)
    {
        var cmd = _conn.CreateCommand();
        var sb = new StringBuilder(sql.Length + 8);
        int n = 0;
        foreach (var ch in sql)
        {
            if (ch == '?') { var name = "@p" + n; sb.Append(name); cmd.Parameters.AddWithValue(name, Normalize(n < args.Length ? args[n] : null)); n++; }
            else sb.Append(ch);
        }
        cmd.CommandText = sb.ToString();
        return cmd;
    }

    private static object Normalize(object? v) => v switch
    {
        null => DBNull.Value,
        bool b => b ? 1L : 0L,
        double d when double.IsNaN(d) || double.IsInfinity(d) => DBNull.Value,
        _ => v,
    };

    public int Exec(string sql, params object?[] args)
    {
        lock (_lock) { using var cmd = Command(sql, args); var changes = cmd.ExecuteNonQuery(); Interlocked.Increment(ref _writes); return changes; }
    }

    public long LastInsertId() { lock (_lock) { using var cmd = _conn.CreateCommand(); cmd.CommandText = "SELECT last_insert_rowid()"; return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture); } }

    public List<Row> Query(string sql, params object?[] args)
    {
        lock (_lock)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<Row>();
            while (r.Read())
            {
                var d = new Dictionary<string, object?>(r.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                list.Add(new Row(d));
            }
            return list;
        }
    }

    public Row? One(string sql, params object?[] args) => Query(sql, args).FirstOrDefault();

    public long Count(string sql, params object?[] args) => One(sql, args)?.Long("count") ?? 0;

    /// <summary>트랜잭션 안에서 실행한다. 예외가 나면 되돌린다.</summary>
    public void Transaction(Action action)
    {
        lock (_lock)
        {
            Exec("BEGIN IMMEDIATE");
            try { action(); Exec("COMMIT"); }
            catch { try { Exec("ROLLBACK"); } catch { /* 이미 끝남 */ } throw; }
        }
    }

    public void Dispose() { lock (_lock) _conn.Dispose(); }
}
