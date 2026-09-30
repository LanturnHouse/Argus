using System.IO;
using System.Text.RegularExpressions;

namespace Argus.Modules.Profiles;

/// <summary>EVE 설정 폴더 탐지와 core_char_{ID}.dat 목록.</summary>
public static partial class EveSettingsLocator
{
    // core_char_숫자.dat 만 캐릭터 파일로 본다 (core_char__.dat 등 찌꺼기 파일은 제외).
    [GeneratedRegex(@"^core_char_(\d+)\.dat$", RegexOptions.IgnoreCase)]
    private static partial Regex CharFileName();

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCP", "EVE");

    /// <summary>%LOCALAPPDATA%\CCP\EVE\c_ccp_eve_*\settings_* 폴더를 모두 찾는다.</summary>
    public static List<EveSettingsFolder> Discover(string? root = null)
    {
        root ??= DefaultRoot;
        var result = new List<EveSettingsFolder>();
        if (!Directory.Exists(root)) return result;

        foreach (var server in Directory.GetDirectories(root, "c_ccp_eve_*"))
        {
            var serverName = Path.GetFileName(server);
            var label = serverName.EndsWith("tranquility", StringComparison.OrdinalIgnoreCase) ? "Tranquility"
                      : serverName.EndsWith("singularity", StringComparison.OrdinalIgnoreCase) ? "Singularity"
                      : serverName;
            foreach (var settings in Directory.GetDirectories(server, "settings_*"))
                result.Add(new EveSettingsFolder(settings, $"{label} / {Path.GetFileName(settings)}"));
        }
        return result;
    }

    /// <summary>Tranquility 의 settings_Default 를 우선, 없으면 첫 폴더.</summary>
    public static EveSettingsFolder? PickDefault(IReadOnlyList<EveSettingsFolder> folders) =>
        folders.FirstOrDefault(f => f.Label.StartsWith("Tranquility", StringComparison.Ordinal)
                                    && f.Label.EndsWith("settings_Default", StringComparison.OrdinalIgnoreCase))
        ?? folders.FirstOrDefault(f => f.Label.StartsWith("Tranquility", StringComparison.Ordinal))
        ?? folders.FirstOrDefault();

    public static List<CharFile> ListCharFiles(string folder)
    {
        var list = new List<CharFile>();
        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.EnumerateFiles(folder, "core_char_*.dat"))
        {
            var m = CharFileName().Match(Path.GetFileName(file));
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out var id)) continue;
            var info = new FileInfo(file);
            list.Add(new CharFile(id, file, info.Length, info.LastWriteTime));
        }
        return [.. list.OrderByDescending(f => f.Modified)];
    }

    public static string CharFilePath(string folder, long charId) => Path.Combine(folder, $"core_char_{charId}.dat");
}
