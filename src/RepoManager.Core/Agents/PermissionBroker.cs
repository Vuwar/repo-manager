using RepoManager.Contracts;

namespace RepoManager.Core.Agents;

/// <summary>
/// Holds PermissionRequest hook calls for a town answer, only while the town is focused. A prompt that arrives
/// unfocused gets no decision at once and is listed as not answerable until the session's next event. Losing
/// focus, the timeout or the hook going away release a wait with no decision, so the session shows its dialog.
/// </summary>
public sealed class PermissionBroker
{
    private static readonly PermissionDecisionDto NoDecision = new(null, false);

    private sealed record Entry(string SessionId, PermissionPromptDto Prompt, TaskCompletionSource<PermissionDecisionDto>? Waiter);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private bool _focused;

    /// <summary>Raised with the session id whenever that session's prompts change.</summary>
    public event Action<string>? PromptsChanged;

    public bool Focused
    {
        get { lock (_gate) return _focused; }
    }

    public void SetFocused(bool focused)
    {
        List<Entry> released = [];
        lock (_gate)
        {
            _focused = focused;
            if (!focused)
            {
                released = _entries.Where(e => e.Waiter != null).ToList();
                _entries.RemoveAll(e => e.Waiter != null);
            }
        }
        foreach (var e in released) e.Waiter!.TrySetResult(NoDecision);
        foreach (var session in released.Select(e => e.SessionId).Distinct()) PromptsChanged?.Invoke(session);
    }

    public async Task<PermissionDecisionDto> WaitAsync(string sessionId, PermissionPromptDto prompt, TimeSpan max, CancellationToken ct)
    {
        TaskCompletionSource<PermissionDecisionDto>? waiter = null;
        lock (_gate)
        {
            if (_focused) waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _entries.Add(new Entry(sessionId, prompt with { Answerable = waiter != null }, waiter));
        }
        PromptsChanged?.Invoke(sessionId);
        if (waiter == null) return NoDecision;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(max);
        await using var reg = timeout.Token.Register(() => Release(prompt.RequestId, NoDecision));
        return await waiter.Task;
    }

    /// <summary>False when no wait with this id is pending (already answered, released or unknown).</summary>
    public bool Answer(string requestId, PermissionDecisionDto decision) => Release(requestId, decision);

    public IReadOnlyList<PermissionPromptDto> PromptsFor(string sessionId)
    {
        lock (_gate) return _entries.Where(e => e.SessionId == sessionId).Select(e => e.Prompt).ToList();
    }

    /// <summary>Removes the session's not-answerable prompts (it moved on, so they were answered in its window).</summary>
    public void ClearSession(string sessionId)
    {
        int removed;
        lock (_gate) removed = _entries.RemoveAll(e => e.SessionId == sessionId && e.Waiter == null);
        if (removed > 0) PromptsChanged?.Invoke(sessionId);
    }

    private bool Release(string requestId, PermissionDecisionDto decision)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = _entries.FirstOrDefault(e => e.Prompt.RequestId == requestId && e.Waiter != null);
            if (entry != null) _entries.Remove(entry);
        }
        if (entry == null) return false;
        entry.Waiter!.TrySetResult(decision);
        PromptsChanged?.Invoke(entry.SessionId);
        return true;
    }
}
