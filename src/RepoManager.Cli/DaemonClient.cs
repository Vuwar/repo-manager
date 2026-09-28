using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using RepoManager.Contracts;

namespace RepoManager.Cli;

public sealed class DaemonUnavailableException(string message) : Exception(message);

/// <summary>HTTP client for the RepoManager daemon. Finds it through daemon.json and starts it when asked to.</summary>
public sealed class DaemonClient : IDisposable
{
    private readonly string _dataDir;
    private HttpClient? _http;

    public DaemonClient(string? dataDir = null) => _dataDir = dataDir ?? Protocol.DataDir();

    /// <summary>Connects to a running daemon. With <paramref name="start"/>, launches RepoManager.exe --background first if needed.</summary>
    public async Task<bool> ConnectAsync(bool start, TimeSpan? timeout = null)
    {
        if (await TryConnectAsync(TimeSpan.FromSeconds(2))) return true;
        if (!start) return false;

        var exe = FindAppExe() ?? throw new DaemonUnavailableException(
            "RepoManager is not running and RepoManager.exe was not found next to devm.exe. Start RepoManager, or set REPOMANAGER_EXE.");
        Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });

        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(300);
            if (await TryConnectAsync(TimeSpan.FromSeconds(1))) return true;
        }
        throw new DaemonUnavailableException("started RepoManager but its API did not answer within 10s");
    }

    private async Task<bool> TryConnectAsync(TimeSpan timeout)
    {
        try
        {
            var infoPath = Protocol.DaemonInfoPath(_dataDir);
            var tokenPath = Protocol.TokenPath(_dataDir);
            if (!File.Exists(infoPath) || !File.Exists(tokenPath)) return false;
            var info = JsonSerializer.Deserialize<DaemonInfoDto>(await File.ReadAllTextAsync(infoPath), Protocol.Json);
            if (info == null) return false;
            var token = (await File.ReadAllTextAsync(tokenPath)).Trim();
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{info.Port}/"), Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Add(Protocol.TokenHeader, token);
            using var cts = new CancellationTokenSource(timeout);
            using var resp = await http.GetAsync("api/ping", cts.Token);
            if (!resp.IsSuccessStatusCode) { http.Dispose(); return false; }
            _http?.Dispose();
            _http = http;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindAppExe()
    {
        var env = Environment.GetEnvironmentVariable("REPOMANAGER_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var dir = AppContext.BaseDirectory;
        var sibling = Path.Combine(dir, "RepoManager.exe");
        if (File.Exists(sibling)) return sibling;
        // Dev layout: src/RepoManager.Cli/bin/<cfg>/net10.0 → src/RepoManager.App/bin/<cfg>/net10.0-windows
        var d = new DirectoryInfo(dir);
        var cfg = d.Parent?.Name;
        var src = d.Parent?.Parent?.Parent?.Parent;
        if (cfg != null && src != null)
        {
            var dev = Path.Combine(src.FullName, "RepoManager.App", "bin", cfg, "net10.0-windows", "RepoManager.exe");
            if (File.Exists(dev)) return dev;
        }
        return null;
    }

    private HttpClient Http => _http ?? throw new DaemonUnavailableException("not connected");

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(path, ct);
        return await ReadAsync<T>(resp, ct);
    }

    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var resp = await Http.PostAsJsonAsync(path, body, Protocol.Json, ct);
        return await ReadAsync<T>(resp, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string? err = null;
            try { err = JsonSerializer.Deserialize<ActionResultDto>(text, Protocol.Json)?.Error; } catch { }
            throw new InvalidOperationException(err ?? $"HTTP {(int)resp.StatusCode}: {text}");
        }
        return JsonSerializer.Deserialize<T>(text, Protocol.Json)!;
    }

    /// <summary>Streams SSE events until cancelled.</summary>
    public async IAsyncEnumerable<EventDto> EventsAsync(string logs, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/events?logs=" + Uri.EscapeDataString(logs));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) yield break;
            if (!line.StartsWith("data: ")) continue;
            EventDto? e = null;
            try { e = JsonSerializer.Deserialize<EventDto>(line[6..], Protocol.Json); } catch { }
            if (e != null) yield return e;
        }
    }

    public void Dispose() => _http?.Dispose();
}
