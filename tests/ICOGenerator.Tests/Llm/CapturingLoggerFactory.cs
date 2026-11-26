using Microsoft.Extensions.Logging;

namespace ICOGenerator.Tests.Llm;

/// <summary>Logger factory giữ lại mọi dòng log (mức + nội dung đã format) để test đọc.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._entries)
                owner._entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
