using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Argus.Modules.Cctv;

/// <summary>로컬 비전 모델(Ollama) 설정. 모델은 읽을 이미지가 있을 때만 올리고 대기가 끝나면 내린다 — Argus 를 켠다고 저절로 올라가지 않는다.</summary>
public sealed class VisionSettings
{
    /// <summary>Ollama 서버 주소.</summary>
    public string Host { get; set; } = "http://127.0.0.1:11434";
    /// <summary>이미지를 읽을 수 있는(비전) 모델 이름.</summary>
    public string Model { get; set; } = "qwen2.5vl:7b";
    /// <summary>모델 호출 하나를 기다리는 최대 시간(초).</summary>
    public int TimeoutSeconds { get; set; } = 240;
    /// <summary>Ollama keep_alive: 요청 뒤 모델을 메모리(GPU)에 붙들어 두는 시간. Argus 는 대기가 끝나면 스스로 내리므로, 이 값은 Argus 가 갑자기 꺼졌을 때 모델이 오래 남지 않게 하는 안전장치다.</summary>
    public string KeepAlive { get; set; } = "5m";
}

/// <summary>
/// 로컬 비전 모델(Ollama)로 영역 이미지를 읽는다. 이 PC 의 Ollama 서버만 부르며 밖으로 나가는 요청은 없다.
/// 모델을 올리고 내리는 것(Load/Unload)과 이미지를 읽는 것(Recognize)을 나눠서, 사용자가 분석을 켠 때만 GPU 를 쓰게 한다.
/// </summary>
public sealed class VisionClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>마지막 호출이 실패한 이유 (성공하면 null). 설정·상태 화면에 보여 준다.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 마지막 실패가 '모델이 답은 했지만 읽을 수 없는 응답'(잘림, JSON 아님)인가. true 면 같은 이미지를 몇 번 다시 해 보고 안 되면 실패로 기록한다.
    /// false 면 서버에 연결하지 못했거나 시간 초과 같은 문제라 이미지는 그대로 두고 나중에 다시 시도한다.
    /// </summary>
    public bool LastFailureWasBadResponse { get; private set; }

    private static readonly Dictionary<RegionKind, string> Prompts = new()
    {
        [RegionKind.Probe] = """
            당신은 EVE Online 스크린샷을 분석하는 OCR 도우미입니다.
            이 이미지는 '탐사 스캐너(Probe Scanner)' 창의 결과 목록입니다. 컬럼은 순서대로 상태 아이콘, 거리, ID, 이름, 그룹, 신호(%)입니다.
            표 헤더(ID/이름 등)와 최소 한 개의 데이터 행이 보이면 visible을 true로, 표 자체가 안 보이거나 완전히 비어 있으면 false로 설정하세요.
            ID는 항상 "영문 대문자 3글자-영숫자 1~3글자" 형식입니다 (예: AHQ-5, DZC-4). 실제로 화면에 표시된 글자를 그대로 옮기고, 다른 글자로 추측해서 바꾸지 마세요.
            다음 JSON 형식으로만 응답하세요. 설명이나 다른 텍스트는 절대 포함하지 마세요:
            {"visible": boolean, "rows": [{"id": string, "distance": string, "name": string, "group": string}]}
            """,
        [RegionKind.Overview] = """
            당신은 EVE Online 스크린샷을 분석하는 OCR 도우미입니다.
            이 이미지는 '오버뷰(Overview)' 창의 목록입니다. 컬럼은 거리, 이름, 종류(함선), 코퍼레이션 티커, 속도(m/s) 등입니다.
            표 헤더와 최소 한 개의 데이터 행이 보이면 visible을 true로, 표 자체가 안 보이거나 완전히 비어 있으면 false로 설정하세요.
            실제로 화면에 표시된 글자를 그대로 옮기고, 다른 글자로 추측해서 바꾸지 마세요.
            속도는 숫자로 계산하지 말고 화면에 보이는 쉼표와 점을 포함한 문자열로 옮기세요. 쉼표는 천 단위 구분자입니다. 예: 1,775,962는 "1,775,962", 126.5는 "126.5". 읽을 수 없으면 빈 문자열을 쓰세요.
            다음 JSON 형식으로만 응답하세요. 설명이나 다른 텍스트는 절대 포함하지 마세요:
            {"visible": boolean, "rows": [{"name": string, "ship": string, "corporation": string, "distance": string, "speed": string}]}
            """,
        [RegionKind.Dock] = """
            당신은 EVE Online 스크린샷을 분석하는 OCR 도우미입니다.
            이 이미지는 스트럭쳐/스테이션의 도킹 인원 숫자가 표시된 작은 영역입니다. 숫자는 항상 대괄호 [ ] 안에 있습니다 (예: [2]).
            대괄호 안의 숫자만 읽으세요. 대괄호 위나 바깥에 보이는 다른 숫자(눈금 숫자 등)는 무시하세요. 대괄호 안이 0이면 0입니다.
            대괄호 안의 숫자가 보이지 않으면 null을 반환하세요.
            다음 JSON 형식으로만 응답하세요. 설명이나 다른 텍스트는 절대 포함하지 마세요:
            {"count": number}
            """,
    };

    private static string Base(string host) => host.Trim().TrimEnd('/');
    private static string Trim(string s) => s.Length > 160 ? s[..160] + "…" : s;

    private async Task<(JsonObject? Json, string? Error)> PostGenerateAsync(VisionSettings settings, JsonObject body, int timeoutSeconds, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 600)));
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{Base(settings.Host)}/api/generate", content, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                return (null, response.StatusCode == System.Net.HttpStatusCode.NotFound ? $"모델 '{settings.Model}' 이 설치되어 있지 않습니다 (ollama pull {settings.Model})" : $"Ollama 오류 {(int)response.StatusCode}: {Trim(detail)}");
            }
            return (JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)) as JsonObject, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, $"모델이 {timeoutSeconds}초 안에 응답하지 않았습니다."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, ex is HttpRequestException ? $"Ollama({settings.Host})에 연결할 수 없습니다. 실행 중인지 확인하세요." : ex.Message); }
    }

    /// <summary>
    /// 모델에 주는 문맥 길이(토큰). Ollama 기본값(수만 토큰)은 읽을 때 쓰이지 않는 메모리(KV 캐시)를 그래픽 메모리에 잡아 두므로,
    /// 프롬프트 + 이미지 + 답이 들어갈 만큼만 쓴다 (7B 모델에서 약 1.4GB 절약, EVE 클라이언트가 그래픽 메모리를 많이 쓸 때 중요하다).
    /// </summary>
    internal const int ContextTokens = 8192;

    /// <summary>답의 길이 상한. 모델이 같은 행을 끝없이 되풀이하는 일이 있어, 영역마다 정상 답이 닿을 만한 만큼만 허용해 헛도는 시간을 줄인다.</summary>
    private static int MaxTokens(RegionKind kind) => kind switch { RegionKind.Dock => 120, RegionKind.Probe => 2000, _ => 3500 };

    /// <summary>모델에게 영역 이미지(PNG)를 읽게 한다. 실패하면 null (이유는 <see cref="LastError"/>).</summary>
    public async Task<JsonObject?> RecognizeAsync(RegionKind kind, byte[] png, VisionSettings settings, CancellationToken ct = default)
    {
        if (!Prompts.TryGetValue(kind, out var prompt)) return null;
        var body = new JsonObject
        {
            ["model"] = settings.Model, ["prompt"] = prompt, ["images"] = new JsonArray(Convert.ToBase64String(png)),
            ["format"] = "json", ["stream"] = false, ["keep_alive"] = settings.KeepAlive,
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_predict"] = MaxTokens(kind), ["num_ctx"] = ContextTokens },
        };
        var (json, error) = await PostGenerateAsync(settings, body, settings.TimeoutSeconds, ct).ConfigureAwait(false);
        if (json == null) { LastError = error; LastFailureWasBadResponse = false; return null; }
        var text = json["response"]?.GetValue<string>();
        var parsed = ParseJsonObject(text);
        if (parsed == null)
        {
            var cut = json["done_reason"]?.GetValue<string>() == "length";
            LastError = cut ? "모델 응답이 중간에 잘렸습니다 (행이 너무 많음)." : $"모델 응답을 JSON 으로 읽지 못했습니다: {Trim(text ?? "")}";
            LastFailureWasBadResponse = true;
            return null;
        }
        LastError = null; LastFailureWasBadResponse = false;
        if (json["done_reason"] is JsonValue reason && reason.TryGetValue<string>(out var why) && why == "length") parsed["_truncated"] = true;
        return parsed;
    }

    /// <summary>
    /// 모델 응답을 JSON 개체로 읽는다. 앞뒤에 설명이 붙었으면 첫 { 부터 마지막 } 까지를 읽고,
    /// 행 목록 중간에서 잘렸으면(길이 제한) 마지막으로 완성된 행까지만 살려서 닫는다 (잘린 마지막 행은 버린다).
    /// </summary>
    internal static JsonObject? ParseJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; } catch { /* 아래에서 다시 시도 */ }
        var a = text.IndexOf('{');
        if (a < 0) return null;
        var b = text.LastIndexOf('}');
        if (b > a) { try { return JsonNode.Parse(text[a..(b + 1)]) as JsonObject; } catch { /* 잘린 응답일 수 있다 */ } }

        // 잘림 복구: 마지막 완성된 행('}' 로 끝나는 것)까지 자르고 배열과 개체를 닫는다.
        for (var end = text.LastIndexOf('}'); end > a; end = text.LastIndexOf('}', end - 1))
        {
            foreach (var closer in new[] { "]}", "}]}" })
            {
                try { if (JsonNode.Parse(text[a..(end + 1)] + closer) is JsonObject repaired) return repaired; } catch { /* 다음 후보 */ }
            }
            if (end == 0) break;
        }
        return null;
    }

    // ---------- 모델 올리기 · 내리기 ----------

    /// <summary>모델을 메모리(GPU)에 올린다 (질문 없이 올리기만 한다). 처음에는 몇 초에서 수십 초 걸릴 수 있다. 성공하면 null, 실패하면 이유.</summary>
    public async Task<string?> LoadModelAsync(VisionSettings settings, CancellationToken ct = default)
    {
        var body = new JsonObject { ["model"] = settings.Model, ["keep_alive"] = settings.KeepAlive, ["stream"] = false, ["options"] = new JsonObject { ["num_ctx"] = ContextTokens } };   // 읽을 때와 같은 num_ctx 여야 다시 올리지 않는다
        var (json, error) = await PostGenerateAsync(settings, body, 180, ct).ConfigureAwait(false);
        if (json == null) { LastError = error; return error; }
        LastError = null;
        return null;
    }

    /// <summary>모델을 메모리(GPU)에서 내린다 (keep_alive 0). 서버가 꺼져 있으면 이미 내려간 것이므로 조용히 넘어간다.</summary>
    public async Task UnloadModelAsync(VisionSettings settings)
    {
        try
        {
            var body = new JsonObject { ["model"] = settings.Model, ["keep_alive"] = 0, ["stream"] = false };
            await PostGenerateAsync(settings, body, 15, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* 내리지 못해도 Ollama 가 keep_alive 시간 뒤에 스스로 내린다 */ }
    }

    /// <summary>지금 메모리에 올라가 있는 모델 이름들 (GET /api/ps).</summary>
    public async Task<List<string>> LoadedModelsAsync(string host, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _http.GetAsync($"{Base(host)}/api/ps", timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)) as JsonObject;
        return [.. (json?["models"] as JsonArray ?? []).OfType<JsonObject>().Select(m => m["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0)];
    }

    /// <summary>Ollama 에 설치된 모델 이름들.</summary>
    public async Task<List<string>> ListModelsAsync(string host, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var response = await _http.GetAsync($"{Base(host)}/api/tags", timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)) as JsonObject;
        return [.. (json?["models"] as JsonArray ?? []).OfType<JsonObject>().Select(m => m["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>서버에 연결되고 선택한 모델이 설치돼 있는지 확인한다 (모델을 올리지는 않는다).</summary>
    public async Task<(bool Ok, string Message)> TestAsync(VisionSettings settings, CancellationToken ct = default)
    {
        try
        {
            var models = await ListModelsAsync(settings.Host, ct).ConfigureAwait(false);
            if (models.Count == 0) return (false, "Ollama 에 연결됐지만 설치된 모델이 없습니다.");
            var found = models.Any(m => string.Equals(m, settings.Model, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Split(':')[0], settings.Model, StringComparison.OrdinalIgnoreCase));
            return found ? (true, $"연결됨 · 모델 '{settings.Model}' 사용 가능 (설치된 모델 {models.Count}개)")
                         : (false, $"연결됐지만 모델 '{settings.Model}' 이 없습니다. 설치된 모델: {string.Join(", ", models.Take(6))}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, "Ollama 가 응답하지 않습니다."); }
        catch (Exception ex) { return (false, ex is HttpRequestException ? $"Ollama({settings.Host})에 연결할 수 없습니다. 실행 중인지 확인하세요." : ex.Message); }
    }

    public void Dispose() => _http.Dispose();
}
