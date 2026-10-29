using RepoManager.Contracts;
using RepoManager.Core.Config;

namespace RepoManager.Core.Agents;

public sealed record AttentionNotice(string SessionId, string Title, string Body);

public enum AttentionSound { NeedsYou, Done }

/// <summary>What the shell should do now: show a notice, play a sound, blink the tray, show the done badge.</summary>
public sealed record AttentionUpdate(AttentionNotice? Notice, AttentionSound? Sound, bool Blink, bool DoneBadge);

/// <summary>
/// Decides when RepoManager gets the user's attention for Claude sessions. A session entering "waiting" gets one
/// notification and the needs-you sound; entering "done" gets the softer sound and the tray's done badge. While the
/// town is focused there are no notifications and no blinking. Sounds are limited to one per <see cref="SoundGap"/>.
/// No I/O: the shell carries the decisions out.
/// </summary>
public sealed class AttentionTracker(Func<DateTimeOffset> clock)
{
    public static readonly TimeSpan SoundGap = TimeSpan.FromSeconds(2);
    private const int BodyMax = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _waiting = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSound = DateTimeOffset.MinValue;
    private bool _doneBadge;

    public AttentionUpdate OnSession(AgentSessionDto s, bool townFocused, TownSettings settings)
    {
        lock (_gate)
        {
            var known = _states.TryGetValue(s.Id, out var before);
            _states[s.Id] = s.State;
            AttentionNotice? notice = null;
            AttentionSound? sound = null;
            if (s.State == "waiting")
            {
                if (_waiting.Add(s.Id))
                {
                    if (settings.Notifications && !townFocused) notice = NoticeFor(s);
                    if (settings.Sound) sound = Sound(AttentionSound.NeedsYou);
                }
            }
            else
            {
                _waiting.Remove(s.Id);
                // A session first seen already done (daemon restarted) did not just finish.
                if (s.State == "done" && known && before != "done")
                {
                    if (settings.Sound) sound = Sound(AttentionSound.Done);
                    if (!townFocused) _doneBadge = true;
                }
            }
            return Update(notice, sound, townFocused, settings);
        }
    }

    public AttentionUpdate OnEnded(string sessionId, bool townFocused, TownSettings settings)
    {
        lock (_gate)
        {
            _states.Remove(sessionId);
            _waiting.Remove(sessionId);
            return Update(null, null, townFocused, settings);
        }
    }

    /// <summary>The town gained or lost focus. Gaining it means the user has seen what finished.</summary>
    public AttentionUpdate OnFocus(bool townFocused, TownSettings settings)
    {
        lock (_gate)
        {
            if (townFocused) _doneBadge = false;
            return Update(null, null, townFocused, settings);
        }
    }

    public void Seen()
    {
        lock (_gate) _doneBadge = false;
    }

    private AttentionUpdate Update(AttentionNotice? notice, AttentionSound? sound, bool focused, TownSettings settings) =>
        new(notice, sound, settings.Tray && !focused && _waiting.Count > 0, _doneBadge && !focused);

    /// <summary>The sound, unless one played within <see cref="SoundGap"/>.</summary>
    private AttentionSound? Sound(AttentionSound s)
    {
        var now = clock();
        if (now - _lastSound < SoundGap) return null;
        _lastSound = now;
        return s;
    }

    private static AttentionNotice NoticeFor(AgentSessionDto s)
    {
        var lot = s.Project ?? s.Folder;
        var prompt = s.Prompts.FirstOrDefault();
        var title = prompt == null ? $"{lot}: has a question"
            : prompt.ToolName is "Bash" or "PowerShell" ? $"{lot}: wants to run {prompt.Summary}"
            : $"{lot}: wants to use {prompt.ToolName}";
        var body = s.Message.Length <= BodyMax ? s.Message : s.Message[..BodyMax];
        return new AttentionNotice(s.Id, title, body);
    }
}
