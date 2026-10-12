using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace costats.Core.Tests.Diagnostics;

/// <summary>
/// Microsoft.Extensions.Logging logger that keeps every entry as text: the
/// rendered message, each structured value and the full exception, so a test
/// can assert that a secret appears in none of them.
/// </summary>
internal class CapturingLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Text)> Entries => _entries.ToArray();

    public string AllText => string.Join("\n", _entries.Select(entry => entry.Text));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var parts = new List<string> { formatter(state, exception) };
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            parts.AddRange(values.Select(pair => $"{pair.Key}={pair.Value}"));
        }

        if (exception is not null)
        {
            parts.Add(exception.ToString());
        }

        _entries.Enqueue((logLevel, string.Join(" | ", parts)));
    }
}

internal sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>
{
}

/// <summary>Serilog sink with the same purpose as <see cref="CapturingLogger"/>.</summary>
internal sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => _events.ToArray();

    public string AllText => string.Join(
        "\n",
        _events.Select(logEvent =>
            string.Join(
                " | ",
                new[] { logEvent.RenderMessage(), logEvent.Exception?.ToString() ?? string.Empty }
                    .Concat(logEvent.Properties.Select(pair => $"{pair.Key}={pair.Value}")))));

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
