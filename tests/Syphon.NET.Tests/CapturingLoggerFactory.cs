using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Syphon.NET.Tests;

internal sealed record LogEntry(
    string Category,
    int EventId,
    LogLevel Level,
    string Message,
    Exception? Exception
);

// Keeps every entry logged through it, for tests to check what was logged.
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries;

    public LogEntry Single(int eventId) => Entries.Single(e => e.EventId == eventId);

    public bool Has(int eventId) => Entries.Any(e => e.EventId == eventId);

    // A directory logs every server on the machine, not only the test's: these pick the test's own.
    public LogEntry Single(int eventId, string about) =>
        Entries.Single(e =>
            e.EventId == eventId && e.Message.Contains(about, StringComparison.Ordinal)
        );

    public bool Has(int eventId, string about) =>
        Entries.Any(e =>
            e.EventId == eventId && e.Message.Contains(about, StringComparison.Ordinal)
        );

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            entries.Enqueue(
                new(category, eventId.Id, logLevel, formatter(state, exception), exception)
            );
    }
}
