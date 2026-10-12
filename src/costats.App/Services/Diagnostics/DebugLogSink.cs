using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace costats.App.Services.Diagnostics;

/// <summary>
/// The debug log: a Serilog sink that sits in the app's logger permanently and
/// writes to <c>logs\debug\debug-YYYYMMDD.log</c> only while it is open.
///
/// Serilog's file sink keeps its file open for as long as it lives, so the
/// folder could never be deleted while debug logging is merely filtered out.
/// This wrapper instead owns a private file logger that it builds on
/// <see cref="Open"/> and disposes on <see cref="Close"/>; disposing it
/// flushes and releases the file handle, after which the folder can go.
/// While closed it holds no file and drops every event.
/// </summary>
public sealed class DebugLogSink : ILogEventSink, IDisposable
{
    public const string FileNamePattern = "debug-.log";
    public const long FileSizeLimitBytes = 20L * 1024 * 1024;
    public const int RetainedFileCountLimit = 7;

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    private readonly object _gate = new();
    private Logger? _file;

    public DebugLogSink(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    /// <summary>The folder the debug log is written to.</summary>
    public string Directory { get; }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _file is not null;
            }
        }
    }

    /// <summary>Starts writing. Safe to call when already open.</summary>
    public void Open()
    {
        lock (_gate)
        {
            if (_file is not null)
            {
                return;
            }

            System.IO.Directory.CreateDirectory(Directory);
            _file = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    Path.Combine(Directory, FileNamePattern),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: RetainedFileCountLimit,
                    outputTemplate: OutputTemplate)
                .CreateLogger();
        }
    }

    /// <summary>
    /// Stops writing, flushes and closes the file. When this returns no handle
    /// on the debug folder is left. Safe to call when already closed.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            // Disposing under the lock guarantees no Emit is mid-write on the
            // logger being torn down.
            _file?.Dispose();
            _file = null;
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            _file?.Write(logEvent);
        }
    }

    public void Dispose() => Close();
}
