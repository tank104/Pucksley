using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Pucksley.Core.Web;

/// <summary>
/// Live events for the web page (Server-Sent Events): log lines, the conversation, puck and plugin
/// status. Keeps the recent ones so a page that opens late can catch up.
/// </summary>
public sealed class HubEvents
{
    const int KeepLogs = 500;
    const int KeepConversation = 200;

    readonly ConcurrentQueue<JsonObject> _logs = new();
    readonly ConcurrentQueue<JsonObject> _conversation = new();
    readonly ConcurrentDictionary<Guid, Channel<JsonObject>> _subscribers = new();

    public IReadOnlyList<JsonObject> RecentLogs => _logs.ToArray();
    public IReadOnlyList<JsonObject> RecentConversation => _conversation.ToArray();

    public void Publish(string type, JsonObject data)
    {
        data["type"] = type;
        data["at"] = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (_, ch) in _subscribers) ch.Writer.TryWrite(data.DeepClone().AsObject());
    }

    public void Log(LogLevel level, string category, string message)
    {
        var e = new JsonObject { ["level"] = level.ToString(), ["category"] = category, ["message"] = message, ["at"] = DateTimeOffset.UtcNow.ToString("O") };
        _logs.Enqueue(e);
        while (_logs.Count > KeepLogs && _logs.TryDequeue(out _)) { }
        Publish("log", e.DeepClone().AsObject());
    }

    /// <summary>A line of a conversation: role = user / assistant / tool / tool-result / tool-error / error.</summary>
    public void Conversation(string session, string who, string role, string text)
    {
        var e = new JsonObject { ["session"] = session, ["who"] = who, ["role"] = role, ["text"] = text, ["at"] = DateTimeOffset.UtcNow.ToString("O") };
        _conversation.Enqueue(e);
        while (_conversation.Count > KeepConversation && _conversation.TryDequeue(out _)) { }
        Publish("conversation", e.DeepClone().AsObject());
    }

    /// <summary>At most one "status" event per kind in this window, so a chatty plugin can't flood the page.</summary>
    const int StatusIntervalMs = 250;

    sealed class StatusGate
    {
        public long Last = long.MinValue / 2;
        public bool Pending;
    }

    readonly ConcurrentDictionary<string, StatusGate> _statusGates = new();

    /// <summary>
    /// Something about pucks, plugins or settings changed: the page should re-read /api/status.
    /// Throttled per kind: the first change goes out at once, later ones within 250 ms are folded
    /// into one event at the end of the window (so the last change is never lost).
    /// </summary>
    public void StatusChanged(string what)
    {
        var gate = _statusGates.GetOrAdd(what, _ => new StatusGate());
        lock (gate)
        {
            if (gate.Pending) return;
            long now = Environment.TickCount64, wait = gate.Last + StatusIntervalMs - now;
            if (wait <= 0)
            {
                gate.Last = now;
                Publish("status", new JsonObject { ["what"] = what });
                return;
            }
            gate.Pending = true;
            _ = Task.Delay(TimeSpan.FromMilliseconds(wait)).ContinueWith(_ =>
            {
                lock (gate)
                {
                    gate.Pending = false;
                    gate.Last = Environment.TickCount64;
                }
                Publish("status", new JsonObject { ["what"] = what });
            }, TaskScheduler.Default);
        }
    }

    public async IAsyncEnumerable<JsonObject> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = ch;
        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(cancel).ConfigureAwait(false)) yield return e;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}

/// <summary>Feeds log lines into <see cref="HubEvents"/> (and so the web page).</summary>
public sealed class HubEventsLoggerProvider(HubEvents events) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(events, categoryName);
    public void Dispose() { }

    sealed class Logger(HubEvents events, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Microsoft", StringComparison.Ordinal) || category.StartsWith("System", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += $" — {exception.GetType().Name}: {exception.Message}";
            var cat = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            events.Log(logLevel, cat, msg);
        }
    }
}
