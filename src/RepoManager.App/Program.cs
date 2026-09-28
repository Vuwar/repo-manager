using System.Net.Http.Json;
using System.Text.Json;
using RepoManager.App.Daemon;
using RepoManager.App.Shell;
using RepoManager.Contracts;

namespace RepoManager.App;

internal static class Program
{
    /// <summary>
    /// RepoManager.exe            start (or activate) and show the window
    /// RepoManager.exe --background   start in the tray only (autostart, first devm command)
    /// RepoManager.exe --headless     no tray or window (tests, diagnostics); stops on Ctrl+C
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var headless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase);
        var dataDir = Protocol.DataDir();
        var port = int.TryParse(Environment.GetEnvironmentVariable("REPOMANAGER_PORT"), out var p) ? p : Protocol.DefaultPort;

        using var mutex = new Mutex(true, "Local\\RepoManager.Singleton." + StableHash(dataDir), out var first);
        if (!first)
        {
            if (!background) ActivateRunning(dataDir);
            return 0;
        }

        var host = new DaemonHost(dataDir, port);
        try
        {
            host.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var msg = $"RepoManager could not listen on 127.0.0.1:{port}.\n\n{ex.Message}\n\nSet REPOMANAGER_PORT to use another port.";
            if (headless) Console.Error.WriteLine(msg);
            else MessageBox.Show(msg, "RepoManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 1;
        }

        _ = Task.Run(host.Supervisor.RestoreAsync);

        if (headless)
        {
            Console.WriteLine($"RepoManager headless on http://127.0.0.1:{host.Port}");
            var done = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
            host.QuitRequested += done.Set;
            done.Wait();
        }
        else
        {
            ApplicationConfiguration.Initialize();
            using var ctx = new TrayApp(host, showWindow: !background);
            Application.Run(ctx);
        }

        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>Second launch: ask the running instance to show its window.</summary>
    private static void ActivateRunning(string dataDir)
    {
        try
        {
            var info = JsonSerializer.Deserialize<DaemonInfoDto>(File.ReadAllText(Protocol.DaemonInfoPath(dataDir)), Protocol.Json)!;
            var token = File.ReadAllText(Protocol.TokenPath(dataDir)).Trim();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Add(Protocol.TokenHeader, token);
            http.PostAsJsonAsync($"http://127.0.0.1:{info.Port}/api/app/activate", new { }).GetAwaiter().GetResult();
        }
        catch
        {
            // Starting up or shutting down; nothing useful to do.
        }
    }

    private static string StableHash(string s)
    {
        var h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 8);
    }
}
