using System.Diagnostics;
using System.Text.Json;

namespace Argus.Core.Settings;

public interface ISettingsStore
{
    T Load<T>(string key) where T : new();
    void Save<T>(string key, T value);
}

/// <summary>%APPDATA%\Argus\settings\{key}.json 에 모듈별로 분리 저장.</summary>
public sealed class JsonSettingsStore(string root) : ISettingsStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private string PathFor(string key) => Path.Combine(root, key + ".json");

    public T Load<T>(string key) where T : new()
    {
        var path = PathFor(key);
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T(); }
        catch (FileNotFoundException) { return new T(); }   // 첫 실행
        catch (DirectoryNotFoundException) { return new T(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // 손상된 파일은 기본값으로 덮어써 사라지기 전에 .bad 로 한 부를 남긴다.
            Trace.WriteLine($"[Settings] {key} 읽기 실패(손상): {ex.Message}");
            KeepBad(path);
            return new T();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Settings] {key} 읽기 실패: {ex.Message}");
            return new T();
        }
    }

    private static void KeepBad(string path)
    {
        try
        {
            var bad = path + ".bad";
            if (File.Exists(bad) && File.GetLastWriteTimeUtc(bad) == File.GetLastWriteTimeUtc(path)) return;
            File.Copy(path, bad, overwrite: true);
        }
        catch { /* 백업 실패는 무시 */ }
    }

    public void Save<T>(string key, T value)
    {
        Directory.CreateDirectory(root);
        var tmp = PathFor(key) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts));
        File.Move(tmp, PathFor(key), overwrite: true);
    }
}
