using System.Diagnostics;

namespace RepoManager.App.Daemon;

/// <summary>Runs `devm claude-setup` to add or remove the Agent Town hooks after the setting changes.</summary>
public static class ClaudeSetup
{
    // Two quick toggles must not run two claude-setup processes writing settings.json at once.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>devm.exe next to RepoManager.exe (the install folder), or REPOMANAGER_DEVM.</summary>
    public static string? FindDevm()
    {
        var overridden = Environment.GetEnvironmentVariable("REPOMANAGER_DEVM");
        if (!string.IsNullOrWhiteSpace(overridden)) return File.Exists(overridden) ? overridden : null;
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        var path = dir == null ? null : Path.Combine(dir, "devm.exe");
        return path != null && File.Exists(path) ? path : null;
    }

    /// <summary>Never throws. claude-setup backs up Claude Code's settings.json before it changes it.</summary>
    public static async Task<(bool Ok, string Message)> RunAsync(bool town)
    {
        if (FindDevm() is not { } devm) return (false, "devm.exe was not found next to RepoManager.exe");
        await Gate.WaitAsync();
        try
        {
            var psi = new ProcessStartInfo(devm, town ? "claude-setup --no-mcp" : "claude-setup --no-mcp --no-town")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Kill it while `p` is still alive: one that hangs must not keep writing settings.json later.
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (false, "devm claude-setup did not finish within 30 s");
            }
            return p.ExitCode == 0 ? (true, (await output).Trim()) : (false, ((await error) + (await output)).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }
}
