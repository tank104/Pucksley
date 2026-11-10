using Microsoft.Extensions.Logging;

namespace Pucksley.Plugins.Mcp;

/// <summary>Gives the MCP SDK the plugin's logger, passing on only messages at or above <paramref name="minimum"/> (the SDK is chatty).</summary>
internal sealed class ForwardingLoggerFactory(ILogger target, LogLevel minimum) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new Forwarder(target, minimum);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    sealed class Forwarder(ILogger target, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => target.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && target.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) target.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
