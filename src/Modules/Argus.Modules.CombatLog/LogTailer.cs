using System.IO;
using System.Text;

namespace Argus.Modules.CombatLog;

/// <summary>로그 파일 하나를 이어서 읽는다 (tail). EVE 가 쓰는 중인 파일을 방해하지 않도록 읽기 전용·공유로 연다.</summary>
internal sealed class LogTailer : IDisposable
{
    private const int MaxChunk = 4 * 1024 * 1024;

    private readonly FileStream _fs;
    private long _pos;
    private byte[] _partial = [];   // 줄바꿈이 오기 전까지의 미완성 줄 (멀티바이트 문자가 잘리지 않게 바이트로 보관)

    public string Path { get; }
    public LogLanguage Language { get; }

    /// <param name="fromEnd">true 면 지금까지의 내용은 건너뛰고 새로 쓰이는 줄부터 읽는다.</param>
    public LogTailer(string path, LogLanguage language, bool fromEnd)
    {
        Path = path;
        Language = language;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _pos = fromEnd ? _fs.Length : 0;
    }

    /// <summary>마지막으로 읽은 뒤 새로 완성된 줄들.</summary>
    public List<string> ReadNewLines()
    {
        var lines = new List<string>();
        var length = _fs.Length;
        if (length < _pos) { _pos = 0; _partial = []; }   // 파일이 다시 만들어졌다
        if (length == _pos) return lines;

        var count = (int)Math.Min(length - _pos, MaxChunk);
        var buf = new byte[_partial.Length + count];
        Array.Copy(_partial, buf, _partial.Length);
        _fs.Position = _pos;
        var read = _fs.Read(buf, _partial.Length, count);
        _pos += read;
        var total = _partial.Length + read;

        var last = Array.LastIndexOf(buf, (byte)'\n', total - 1);
        if (last < 0) { _partial = buf[..total]; return lines; }
        _partial = buf[(last + 1)..total];

        var text = Encoding.UTF8.GetString(buf, 0, last + 1);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) lines.Add(line.TrimEnd('\r'));
        return lines;
    }

    public void Dispose() => _fs.Dispose();
}
