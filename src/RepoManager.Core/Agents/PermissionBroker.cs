using RepoManager.Contracts;

namespace RepoManager.Core.Agents;

/// <summary>
/// Holds PermissionRequest hook calls for a town answer, only while the town is focused. Focus is a lease: the town
/// renews it by reporting focus again before it runs out, so a town that dies or misses its "unfocused" message can
/// never keep sessions waiting. A prompt that arrives unfocused gets no decision at once. Losing focus or the timeout
/// release a wait with no decision, so the session shows its own dialog; the prompt stays listed as not answerable
/// ("Answer in the window") until the session's next event clears it. An answer or the hook going away removes it.
/// </summary>
public sealed class PermissionBroker : IDisposable
{
    public static readonly TimeSpan DefaultFocusLease = TimeSpan.FromSeconds(15);
    private static readonly PermissionDecisionDto NoDecision = new(null, false);

    private sealed record Entry(string SessionId, PermissionPromptDto Prompt, TaskCompletionSource<PermissionDecisionDto>? Waiter);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly TimeSpan _focusLease;
    private readonly Timer _leaseTimer;
    private bool _focused;
    private long _leaseEnds;

    public PermissionBroker(TimeSpan? focusLease = null)
    {
        _focusLease = focusLease ?? DefaultFocusLease;
        _leaseTimer = new Timer(_ => OnLeaseTimer());
    }

    /// <summary>Raised with the session id whenever that session's prompts change.</summary>
    public event Action<string>? PromptsChanged;

    public bool Focused
    {
        get { lock (_gate) return _focused; }
    }

    /// <summary>True starts or renews the focus lease; false (or the lease running out) releases every wait.</summary>
    public void SetFocused(bool focused)
    {
        List<Entry> released = [];
        lock (_gate)
        {
            _focused = focused;
            if (focused)
            {
                _leaseEnds = Environment.TickCount64 + (long)_focusLease.TotalMilliseconds;
                _leaseTimer.Change(_focusLease, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _leaseTimer.Change(Timeout.Infinite, Timeout.Infinite);
                released = _entries.Where(e => e.Waiter != null).ToList();
                foreach (var e in released) ToWindow(e);
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

        using var timeout = new CancellationTokenSource(max);
        await using var onTimeout = timeout.Token.Register(() => Release(prompt.RequestId, NoDecision, keepListed: true));
        // The hook went away (session closed or answered elsewhere): nothing is left to answer.
        await using var onCancel = ct.Register(() => Release(prompt.RequestId, NoDecision, keepListed: false));
        return await waiter.Task;
    }

    /// <summary>False when no wait with this id is pending (already answered, released or unknown).</summary>
    public bool Answer(string requestId, PermissionDecisionDto decision) => Release(requestId, decision, keepListed: false);

    /// <summary>The session a pending or listed prompt belongs to, or null.</summary>
    public string? SessionOf(string requestId)
    {
        lock (_gate) return _entries.FirstOrDefault(e => e.Prompt.RequestId == requestId)?.SessionId;
    }

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

    public void Dispose()
    {
        _leaseTimer.Dispose();
        SetFocused(false);
    }

    private void OnLeaseTimer()
    {
        lock (_gate)
        {
            // A renewal may have raced the timer; only a lease that really ran out ends focus.
            if (!_focused || Environment.TickCount64 < _leaseEnds) return;
        }
        SetFocused(false);
    }

    /// <summary>Keeps the entry, without its waiter, as a prompt to answer in the session window. Call under _gate.</summary>
    private void ToWindow(Entry e)
    {
        var i = _entries.IndexOf(e);
        if (i >= 0) _entries[i] = e with { Prompt = e.Prompt with { Answerable = false }, Waiter = null };
    }

    private bool Release(string requestId, PermissionDecisionDto decision, bool keepListed)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = _entries.FirstOrDefault(e => e.Prompt.RequestId == requestId && e.Waiter != null);
            if (entry != null)
            {
                if (keepListed) ToWindow(entry);
                else _entries.Remove(entry);
            }
        }
        if (entry == null) return false;
        entry.Waiter!.TrySetResult(decision);
        PromptsChanged?.Invoke(entry.SessionId);
        return true;
    }
}
