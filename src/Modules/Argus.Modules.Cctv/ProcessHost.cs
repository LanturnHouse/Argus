using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Argus.Modules.Cctv;

/// <summary>
/// 자식 프로세스들을 하나의 Windows 작업 개체(Job Object)에 묶는다. 작업 개체를 닫으면(Argus 가 종료되거나 비정상 종료해도)
/// 묶인 프로세스가 모두 같이 끝나서 Node 프로세스가 남아 포트를 붙잡는 일이 없다.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private nint _handle;

    public JobObject()
    {
        _handle = CreateJobObject(0, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Add(Process process)
    {
        if (_handle != 0) AssignProcessToJobObject(_handle, process.Handle);
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        CloseHandle(_handle);   // 묶인 프로세스가 모두 종료된다
        _handle = 0;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint attrs, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}

/// <summary>Node 자식 프로세스 하나: 창 없이 실행하고 출력 마지막 줄들을 기억한다.</summary>
internal sealed class NodeProcess : IDisposable
{
    private readonly Process _process;
    private readonly Queue<string> _lines = new();
    private readonly object _lock = new();

    public string Name { get; }
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }
    public int? ExitCode { get { try { return _process.HasExited ? _process.ExitCode : null; } catch { return null; } } }
    public event Action<NodeProcess>? Exited;

    public NodeProcess(string name, string node, string workingDirectory, IEnumerable<string> args, JobObject job)
    {
        Name = name;
        var psi = new ProcessStartInfo(node)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["FORCE_COLOR"] = "0";   // 로그에 색 제어 문자가 섞이지 않게

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Add(e.Data);
        _process.ErrorDataReceived += (_, e) => Add(e.Data);
        _process.Exited += (_, _) => Exited?.Invoke(this);
        _process.Start();
        job.Add(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_lock)
        {
            _lines.Enqueue($"[{Name}] {line}");
            while (_lines.Count > 300) _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> Lines() { lock (_lock) return [.. _lines]; }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* 이미 끝남 */ }
        _process.Dispose();
    }
}
