using System.Collections.Concurrent;
using System.Text;
using RepoManager.Contracts;

namespace RepoManager.Core.Logs;

/// <summary>Ring buffer of recent lines for one service or task, mirrored to a rolling file.</summary>
public sealed class LogBuffer : IDisposable
{
    public const int Capacity = 5000;
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int FileCount = 3;

    private readonly LogLineDto[] _ring = new LogLineDto[Capacity];
    private readonly object _lock = new();
    private readonly string? _filePath;
    private StreamWriter? _writer;
    private long _fileBytes;
    private int _start, _count;

    public string Source { get; }
    public event Action<LogLineDto>? LineAdded;

    public LogBuffer(string source, string? filePath)
    {
        Source = source;
        _filePath = filePath;
        if (filePath != null) LoadTail(filePath);
    }

    public void Append(LogStream stream, string text)
    {
        var line = new LogLineDto(DateTimeOffset.Now, stream, text, Source);
        lock (_lock)
        {
            _ring[(_start + _count) % Capacity] = line;
            if (_count < Capacity) _count++;
            else _start = (_start + 1) % Capacity;
            WriteToFile(line);
        }
        LineAdded?.Invoke(line);
    }

    public IReadOnlyList<LogLineDto> Query(int? tail = null, DateTimeOffset? since = null, string? grep = null)
    {
        List<LogLineDto> all;
        lock (_lock)
        {
            all = new List<LogLineDto>(_count);
            for (var i = 0; i < _count; i++) all.Add(_ring[(_start + i) % Capacity]);
        }
        IEnumerable<LogLineDto> q = all;
        if (since != null) q = q.Where(l => l.Timestamp >= since);
        if (!string.IsNullOrEmpty(grep)) q = q.Where(l => l.Text.Contains(grep, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        if (tail != null && list.Count > tail) list = list.GetRange(list.Count - tail.Value, tail.Value);
        return list;
    }

    public IReadOnlyList<string> TailText(int n) => Query(n).Select(l => l.Text).ToList();

    public void Clear()
    {
        lock (_lock) { _start = 0; _count = 0; }
    }

    private void WriteToFile(LogLineDto line)
    {
        if (_filePath == null) return;
        try
        {
            if (_writer == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _fileBytes = fs.Length;
                _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            }
            var text = $"{line.Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} {StreamCode(line.Stream)} {line.Text}";
            _writer.WriteLine(text);
            _fileBytes += Encoding.UTF8.GetByteCount(text) + 2;
            if (_fileBytes >= MaxFileBytes) Roll();
        }
        catch (IOException)
        {
            // Disk full or file locked: keep the in-memory buffer working.
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Roll()
    {
        _writer?.Dispose();
        _writer = null;
        for (var i = FileCount - 1; i >= 1; i--)
        {
            var src = i == 1 ? _filePath! : $"{_filePath}.{i - 1}";
            var dst = $"{_filePath}.{i}";
            if (File.Exists(src)) File.Move(src, dst, true);
        }
        _fileBytes = 0;
    }

    private static string StreamCode(LogStream s) => s switch { LogStream.Err => "E", LogStream.Sys => "S", _ => "O" };

    /// <summary>Restores the last lines from disk so logs survive a daemon restart.</summary>
    private void LoadTail(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const long maxRead = 2 * 1024 * 1024;
            if (fs.Length > maxRead) fs.Seek(-maxRead, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            if (fs.Position > 0) reader.ReadLine(); // partial line
            var lines = new List<LogLineDto>();
            string? l;
            while ((l = reader.ReadLine()) != null)
            {
                var parsed = ParseFileLine(l);
                if (parsed != null) lines.Add(parsed);
            }
            foreach (var line in lines.Skip(Math.Max(0, lines.Count - Capacity)))
            {
                _ring[(_start + _count) % Capacity] = line;
                if (_count < Capacity) _count++; else _start = (_start + 1) % Capacity;
            }
        }
        catch (IOException) { }
    }

    private LogLineDto? ParseFileLine(string l)
    {
        var sp1 = l.IndexOf(' ');
        if (sp1 < 0 || l.Length < sp1 + 2) return null;
        if (!DateTimeOffset.TryParse(l[..sp1], out var ts)) return null;
        var stream = l[sp1 + 1] switch { 'E' => LogStream.Err, 'S' => LogStream.Sys, _ => LogStream.Out };
        var text = l.Length > sp1 + 3 ? l[(sp1 + 3)..] : "";
        return new LogLineDto(ts, stream, text, Source);
    }

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }
}

public sealed class LogStore : IDisposable
{
    private readonly string? _dir;
    private readonly ConcurrentDictionary<string, LogBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised for every line of every buffer (live log streaming).</summary>
    public event Action<LogLineDto>? LineAdded;

    public LogStore(string? dir) => _dir = dir;

    public LogBuffer Get(string source) => _buffers.GetOrAdd(source, s =>
    {
        var b = new LogBuffer(s, _dir == null ? null : Path.Combine(_dir, SafeFileName(s) + ".log"));
        b.LineAdded += l => LineAdded?.Invoke(l);
        return b;
    });

    public bool TryGet(string source, out LogBuffer? buffer)
    {
        if (_buffers.TryGetValue(source, out var b)) { buffer = b; return true; }
        if (_dir != null && File.Exists(Path.Combine(_dir, SafeFileName(source) + ".log"))) { buffer = Get(source); return true; }
        buffer = null;
        return false;
    }

    /// <summary>"panel-pro@feat/api" → "panel-pro@feat__api".</summary>
    public static string SafeFileName(string source)
    {
        var sb = new StringBuilder();
        foreach (var c in source)
            sb.Append(c == '/' ? "__" : Path.GetInvalidFileNameChars().Contains(c) ? "_" : c.ToString());
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var b in _buffers.Values) b.Dispose();
    }
}
