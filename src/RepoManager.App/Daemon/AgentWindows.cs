using RepoManager.Contracts;
using RepoManager.Core.Agents;

namespace RepoManager.App.Daemon;

/// <summary>Brings a Claude session's own window forward (the town's "Open window" and a notification click).</summary>
public static class AgentWindows
{
    public static ActionResultDto Open(DaemonHost d, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || d.Agents.Get(sessionId) is not { } session) return ActionResultDto.Fail("unknown session");
        if (session.Surface == "desktop" && WindowActivator.DesktopLink(session.HostSessionId) is { } link && WindowActivator.OpenLink(link))
            return ActionResultDto.Success("opened in Claude");
        if (d.Agents.ProcessFor(sessionId) is not { } pid)
            return ActionResultDto.Fail("RepoManager could not find this session's window");
        // A pid of a process that ended may already belong to another program.
        var started = ProcessTree.StartTime(pid);
        if (started is null || started != d.Agents.ProcessStarted(sessionId))
            return ActionResultDto.Fail("this session's Claude process is no longer running");
        var (ok, message) = WindowActivator.Activate(pid);
        return ok ? ActionResultDto.Success(message) : ActionResultDto.Fail(message);
    }
}
