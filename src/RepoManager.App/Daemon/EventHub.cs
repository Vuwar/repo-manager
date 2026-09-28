using System.Threading.Channels;
using RepoManager.Contracts;
using RepoManager.Core.Config;
using RepoManager.Core.Logs;
using RepoManager.Core.Processes;

namespace RepoManager.App.Daemon;

/// <summary>Fans out status, task, log and config events to SSE subscribers.</summary>
public sealed class EventHub : IDisposable
{
    private readonly Supervisor _supervisor;
    private readonly LogStore _logs;
    private readonly ConfigService _config;
    private readonly List<Subscriber> _subs = [];

    public event Action<ServiceDto>? StatusChanged;

    public EventHub(Supervisor supervisor, LogStore logs, ConfigService config)
    {
        _supervisor = supervisor;
        _logs = logs;
        _config = config;
        _supervisor.ServiceChanged += OnService;
        _supervisor.TaskChanged += OnTask;
        _logs.LineAdded += OnLog;
        _config.Changed += OnConfig;
    }

    public sealed class Subscriber
    {
        /// <summary>Log sources to stream; "*" streams all; empty streams none.</summary>
        public required HashSet<string> LogSources { get; init; }
        public Channel<EventDto> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<EventDto>(
            new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public bool WantsLog(string source) => LogSources.Contains("*") || LogSources.Contains(source);
    }

    public Subscriber Subscribe(IEnumerable<string> logSources)
    {
        var s = new Subscriber { LogSources = new HashSet<string>(logSources, StringComparer.OrdinalIgnoreCase) };
        lock (_subs) _subs.Add(s);
        return s;
    }

    public void Unsubscribe(Subscriber s)
    {
        lock (_subs) _subs.Remove(s);
        s.Channel.Writer.TryComplete();
    }

    private void Publish(EventDto e, string? logSource = null)
    {
        Subscriber[] subs;
        lock (_subs) subs = [.. _subs];
        foreach (var s in subs)
            if (logSource == null || s.WantsLog(logSource))
                s.Channel.Writer.TryWrite(e);
    }

    private void OnService(ServiceDto dto)
    {
        Publish(new EventDto { Type = "status", Payload = dto });
        StatusChanged?.Invoke(dto);
    }

    private void OnTask(TaskDto dto) => Publish(new EventDto { Type = "task", Payload = dto });
    private void OnLog(LogLineDto line) => Publish(new EventDto { Type = "log", Payload = line }, line.Source);
    private void OnConfig() => Publish(new EventDto { Type = "projects" });

    public void Dispose()
    {
        _supervisor.ServiceChanged -= OnService;
        _supervisor.TaskChanged -= OnTask;
        _logs.LineAdded -= OnLog;
        _config.Changed -= OnConfig;
        lock (_subs)
        {
            foreach (var s in _subs) s.Channel.Writer.TryComplete();
            _subs.Clear();
        }
    }
}
