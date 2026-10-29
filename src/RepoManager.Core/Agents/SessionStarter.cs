using System.Diagnostics;
using RepoManager.Contracts;

namespace RepoManager.Core.Agents;

/// <summary>
/// Starts Claude Code sessions for the town: a terminal session in Windows Terminal (else a cmd window), or a desktop
/// session through the desktop app's <c>new</c> link. A terminal session's first prompt is not put on the command line
/// (Windows Terminal splits on ';' and re-quotes); it is held and sent through the inbox of the first terminal
/// session that starts in that folder within <see cref="HoldFor"/>.
/// </summary>
public sealed class SessionStarter(
    Func<ProcessStartInfo, bool> start,
    Func<string, bool> openLink,
    Func<DateTimeOffset> clock,
    Func<string, string, string, Task<(bool Ok, string Message)>> send)
{
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan KeepStatus = TimeSpan.FromMinutes(10);

    private sealed record Held(string Id, string Folder, string Text, DateTimeOffset Until);

    private readonly Lock _lock = new();
    private readonly List<Held> _held = [];
    private readonly Dictionary<string, (StartStatusDto Status, DateTimeOffset At)> _status = [];

    /// <summary>The desktop app's link for a new session in a folder. Undocumented: found in the app's link handler.</summary>
    public static string DesktopNewLink(string folder) => "claude://code/new?folder=" + Uri.EscapeDataString(folder);

    public StartStatusDto StartTerminal(string folder, string? text)
    {
        var id = Guid.NewGuid().ToString("N");
        var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = false, ArgumentList = { "-d", folder, "claude" } };
        var cmd = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, WorkingDirectory = folder, ArgumentList = { "/k", "claude" } };
        if (!start(wt) && !start(cmd)) return Set(id, "failed", "could not open a terminal");
        if (string.IsNullOrWhiteSpace(text)) return Set(id, "none", "started");
        lock (_lock) _held.Add(new Held(id, Key(folder), text, clock() + HoldFor));
        return Set(id, "pending", "waiting for the session");
    }

    public ActionResultDto StartDesktop(string folder) =>
        openLink(DesktopNewLink(folder))
            ? ActionResultDto.Success("opened a new session in Claude")
            : ActionResultDto.Fail("could not open the Claude desktop app");

    /// <summary>Hands the oldest held prompt for this folder to a terminal session that just started.</summary>
    public async Task OnSessionStart(AgentSessionDto session, (string Socket, string Token)? inbox)
    {
        if (session.Surface != "terminal") return;
        Held? held;
        lock (_lock)
        {
            Prune();
            held = _held.FirstOrDefault(h => h.Folder == Key(session.Cwd));
            if (held != null) _held.Remove(held);
        }
        if (held == null) return;
        if (inbox is not { } ib)
        {
            Set(held.Id, "failed", "the new session has no inbox");
            return;
        }
        var (ok, message) = await send(ib.Socket, ib.Token, AgentInbox.FromTownUser(held.Text));
        Set(held.Id, ok ? "sent" : "failed", message);
    }

    public StartStatusDto Status(string id)
    {
        lock (_lock)
        {
            Prune();
            return _status.TryGetValue(id, out var s) ? s.Status : new StartStatusDto { Id = id, State = "expired", Message = "no session started in time" };
        }
    }

    private StartStatusDto Set(string id, string state, string? message)
    {
        var s = new StartStatusDto { Id = id, State = state, Message = message };
        lock (_lock) _status[id] = (s, clock());
        return s;
    }

    private void Prune()
    {
        var now = clock();
        foreach (var h in _held.Where(h => h.Until < now).ToList())
        {
            _held.Remove(h);
            _status[h.Id] = (new StartStatusDto { Id = h.Id, State = "expired", Message = "no session started in time" }, now);
        }
        foreach (var k in _status.Where(kv => now - kv.Value.At > KeepStatus).Select(kv => kv.Key).ToList()) _status.Remove(k);
    }

    private static string Key(string folder) => PathUtil.Normalize(folder).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
}
