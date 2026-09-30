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
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(PathFor(key))) ?? new T(); }
        catch { return new T(); }
    }

    public void Save<T>(string key, T value)
    {
        Directory.CreateDirectory(root);
        var tmp = PathFor(key) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts));
        File.Move(tmp, PathFor(key), overwrite: true);
    }
}
