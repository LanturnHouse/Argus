using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Argus.Core.Settings;

namespace Argus.Modules.Profiles;

/// <summary>
/// 캐릭터 ID → 이름. EVE ESI 공개 API(로그인 불필요)로 조회하고 한 번 받은 이름은 로컬에 저장한다.
/// 우선 POST /universe/names/ 로 한 번에 조회하고, 실패하면 GET /characters/{id}/ 로 하나씩 조회한다.
/// </summary>
public sealed class EsiNames
{
    private const string BaseUrl = "https://esi.evetech.net";
    private const string SettingsKey = "profiles.names";

    private static readonly HttpClient Http = CreateClient();

    private readonly ISettingsStore _store;
    private readonly NamesData _data;
    private readonly object _lock = new();

    public EsiNames(ISettingsStore store)
    {
        _store = store;
        _data = store.Load<NamesData>(SettingsKey);
    }

    /// <summary>마지막 조회 실패 사유 (성공하면 null).</summary>
    public string? LastError { get; private set; }

    public string? Get(long id)
    {
        lock (_lock) return _data.Names.TryGetValue(id, out var n) ? n : null;
    }

    public string Display(long id) => Get(id) ?? $"ID {id}";

    /// <summary>이름을 모르는 ID 만 조회한다. 새로 알게 된 이름 수를 반환한다.</summary>
    public async Task<int> ResolveAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        long[] missing;
        lock (_lock) missing = [.. ids.Distinct().Where(i => !_data.Names.ContainsKey(i))];
        if (missing.Length == 0) { LastError = null; return 0; }

        var found = 0;
        LastError = null;
        try
        {
            foreach (var chunk in missing.Chunk(500))
                found += await ResolveBulkAsync(chunk, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 일괄 조회가 실패하면(ID 하나가 잘못돼도 전체가 실패한다) 하나씩 조회한다.
            foreach (var id in missing.Where(i => Get(i) == null))
            {
                try { found += await ResolveOneAsync(id, ct); }
                catch (Exception inner) when (inner is not OperationCanceledException) { LastError = inner.Message; }
            }
        }
        if (found > 0) Save();
        return found;
    }

    private async Task<int> ResolveBulkAsync(long[] ids, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync($"{BaseUrl}/universe/names/", ids, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var count = 0;
        lock (_lock)
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.GetProperty("category").GetString() != "character") continue;
                _data.Names[e.GetProperty("id").GetInt64()] = e.GetProperty("name").GetString() ?? "";
                count++;
            }
        }
        return count;
    }

    private async Task<int> ResolveOneAsync(long id, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"{BaseUrl}/characters/{id}/", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var name = doc.RootElement.GetProperty("name").GetString();
        if (string.IsNullOrEmpty(name)) return 0;
        lock (_lock) _data.Names[id] = name;
        return 1;
    }

    private void Save()
    {
        lock (_lock) _store.Save(SettingsKey, _data);
    }

    /// <summary>테스트용: 네트워크 없이 이름을 넣는다.</summary>
    internal void Seed(long id, string name)
    {
        lock (_lock) _data.Names[id] = name;
        Save();
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // ESI 는 앱을 식별하는 User-Agent 를 요구한다. 개인 정보는 넣지 않는다.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Argus/0.1");
        return c;
    }
}
