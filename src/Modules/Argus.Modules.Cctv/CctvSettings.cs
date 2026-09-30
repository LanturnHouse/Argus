using System.IO;

namespace Argus.Modules.Cctv;

/// <summary>EVE CCTV 웹앱 연동 설정 (설정 키 "cctv").</summary>
public sealed class CctvSettings
{
    /// <summary>웹앱 폴더(package.json 이 있는 곳). 비워 두면 알려진 위치에서 자동으로 찾는다.</summary>
    public string AppFolder { get; set; } = "";

    /// <summary>node.exe 경로. 비워 두면 Argus 옆 runtime\node.exe, 없으면 PATH 의 node.</summary>
    public string NodePath { get; set; } = "";

    /// <summary>Argus 를 켜면 CCTV 서비스도 같이 켠다.</summary>
    public bool AutoStart { get; set; }

    // 웹앱이 고정으로 쓰는 포트 (app/page.tsx 와 scripts/run-framework.mjs)
    public const int ServicePort = 8765;
    public const int UiPort = 5173;

    private static readonly string[] KnownAppFolders = [@"D:\eve cctv web v2"];

    /// <summary>실제로 쓸 웹앱 폴더. 못 찾으면 null.</summary>
    public string? ResolveAppFolder()
    {
        bool Valid(string p) => !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, "local-service", "server.mjs"));
        if (Valid(AppFolder)) return AppFolder;
        return KnownAppFolders.FirstOrDefault(Valid);
    }

    /// <summary>실제로 쓸 node.exe. 못 찾으면 null.</summary>
    public string? ResolveNode()
    {
        if (!string.IsNullOrWhiteSpace(NodePath) && File.Exists(NodePath)) return NodePath;

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
        {
            var bundled = Path.Combine(exeDir, "runtime", "node.exe");   // publish.bat 이 넣어 주는 동봉 Node
            if (File.Exists(bundled)) return bundled;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 잘못된 PATH 항목은 건너뜀 */ }
        }
        return null;
    }
}
