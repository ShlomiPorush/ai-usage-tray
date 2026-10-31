using System.Buffers;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using costats.Application.Storage;
using costats.Core.Antigravity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Infrastructure.Antigravity;

/// <summary>The closed set of outcomes of one call to <see cref="IAgyProbeRunner.RunAsync"/>.</summary>
public enum AgyProbeStatus
{
    /// <summary>agy reported quota with at least one Gemini bucket.</summary>
    Success,

    /// <summary>The schedule does not allow a check yet; agy was not started.</summary>
    Waiting,

    /// <summary>Another check (in this or another tray instance) holds the lock. Not a failure.</summary>
    Busy,

    AgyNotFound,
    LaunchFailed,
    StorageFailed,

    /// <summary>The deadline passed, or agy exited, without a payload that had quota.</summary>
    NoQuota,

    /// <summary>agy reported quota, but no Gemini bucket this app understands.</summary>
    UnsupportedFormat,

    /// <summary>The caller cancelled the check. Not a failure.</summary>
    Cancelled
}

/// <param name="Quota">
/// Set for <see cref="AgyProbeStatus.Success"/>, and for
/// <see cref="AgyProbeStatus.Waiting"/> when the last successful quota is
/// stored and still current (its windows have not reset, and the last check
/// did not fail).
/// </param>
/// <param name="NextAllowedAt">Set for <see cref="AgyProbeStatus.Waiting"/>: the earliest next check (UTC).</param>
/// <param name="CompletedAt">When the attempt finished, for outcomes that started one, or when a stored quota was read.</param>
public sealed record AgyProbeResult(
    AgyProbeStatus Status,
    AgyQuotaReading? Quota = null,
    DateTimeOffset? NextAllowedAt = null,
    DateTimeOffset? CompletedAt = null)
{
    /// <summary>Outcomes that count toward the back-off.</summary>
    public bool IsFailure => Status is AgyProbeStatus.AgyNotFound
        or AgyProbeStatus.LaunchFailed
        or AgyProbeStatus.StorageFailed
        or AgyProbeStatus.NoQuota
        or AgyProbeStatus.UnsupportedFormat;
}

/// <param name="Interval">The minimum time between checks the user has set now.</param>
public sealed record AgyProbeRequest(TimeSpan Interval);

public interface IAgyProbeRunner
{
    /// <summary>Runs one check when the shared schedule allows it. Never throws.</summary>
    Task<AgyProbeResult> RunAsync(AgyProbeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Milliseconds since the machine started and which boot this is; injectable
/// so tests can simulate restarts.
/// </summary>
public interface IUptimeClock
{
    long TickCount64 { get; }

    /// <summary>The Windows boot counter, which grows on every boot; null when it cannot be read.</summary>
    long? BootId { get; }
}

public sealed class SystemUptimeClock : IUptimeClock
{
    internal const string BootIdKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";
    internal const string BootIdValueName = "BootId";

    // A process never outlives a boot, so the value is read once.
    private static readonly Lazy<long?> CachedBootId = new(ReadBootId);

    public static SystemUptimeClock Instance { get; } = new();

    public long TickCount64 => Environment.TickCount64;

    public long? BootId => CachedBootId.Value;

    /// <summary>Reads the REG_DWORD boot counter from HKLM. Never throws; null on any failure.</summary>
    internal static long? ReadBootId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(BootIdKeyPath, writable: false);
            return key?.GetValue(BootIdValueName) switch
            {
                int value => (uint)value,
                long value when value >= 0 => value,
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal sealed record AgyProbeRunnerOptions
{
    /// <summary>Base folder for <see cref="AppDataPaths.ResolveRoot"/>; null uses the real data folder.</summary>
    public string? BasePath { get; init; }

    public Func<string?> ResolveExecutable { get; init; } = AgyExecutableResolver.Resolve;

    /// <summary>Builds the full command line from the executable and the probe folder.</summary>
    public Func<string, string, string> CommandLine { get; init; } = AgyProbeRunner.BuildAgyCommandLine;

    public Func<IReadOnlyDictionary<string, string>> Environment { get; init; } = AgyProbeRunner.BuildEnvironment;

    public TimeSpan Deadline { get; init; } = AgyProbeRunner.DefaultDeadline;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long to keep agy running after the quota arrived, waiting for the plan name.</summary>
    public TimeSpan PlanGrace { get; init; } = TimeSpan.FromSeconds(3);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public IUptimeClock Uptime { get; init; } = SystemUptimeClock.Instance;

    /// <summary>Test hook, called right after the process was created in its job.</summary>
    public Action<JobObjectProcess>? OnLaunched { get; init; }
}

/// <summary>
/// Starts the Antigravity CLI briefly in a hidden console, captures the first
/// statusline payload that carries quota, and ends agy and everything it
/// started. agy's own settings file is never touched: the statusline command
/// is passed inline with <c>--settings</c> and writes <c>payload.json</c> into a
/// fresh per-attempt folder, which is deleted afterwards. No token is read.
///
/// One check at a time per user: a lock file under the data folder is held for
/// the whole attempt, including its clean-up, and the shared schedule file is
/// only read and written under that lock.
/// </summary>
public sealed class AgyProbeRunner : IAgyProbeRunner
{
    public const string FolderName = "antigravity-probe";
    public const string LockFileName = "probe.lock";
    public const string ScheduleFileName = "schedule.json";
    public const string ScheduleTempFileName = "schedule.json.tmp";
    public const string LastQuotaFileName = "last-quota.json";
    public const string LastQuotaTempFileName = "last-quota.json.tmp";
    public const string PayloadFileName = "payload.json";
    public const string LogFileName = "agy.log";

    /// <summary>Inline settings: the statusline pipes each payload into payload.json in agy's working folder.</summary>
    public const string InlineSettings = "{\"statusLine\":{\"type\":\"command\",\"command\":\"more > payload.json\"}}";

    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(15);

    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    internal const int MaxPayloadBytes = 256 * 1024;

    private static readonly TimeSpan TerminateWait = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly AgyProbeRunnerOptions _options;
    private readonly string _directory;
    private readonly object _reportGate = new();
    private readonly HashSet<AgyProbeStatus> _reported = [];
    private bool _swept;

    /// <summary>
    /// Set under the lock once this runner has judged or written the schedule
    /// record; from then on the record belongs to this boot. Until then, a
    /// record whose boot cannot be compared gets a full wait.
    /// </summary>
    private bool _bootVerifiedThisProcess;

    private int _warnedStoredQuota;

    public AgyProbeRunner(ILogger<AgyProbeRunner> logger)
        : this(logger, new AgyProbeRunnerOptions())
    {
    }

    internal AgyProbeRunner(ILogger? logger, AgyProbeRunnerOptions options)
    {
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _directory = options.BasePath is null
            ? AppDataPaths.Combine(FolderName)
            : Path.Combine(AppDataPaths.ResolveRoot(options.BasePath), FolderName);
    }

    /// <summary>The folder holding the lock, the schedule and the per-attempt folders.</summary>
    public string Directory => _directory;

    public async Task<AgyProbeResult> RunAsync(AgyProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return new AgyProbeResult(AgyProbeStatus.Cancelled);
        }

        FileStream lockStream;
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            lockStream = new FileStream(
                Path.Combine(_directory, LockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex) when (ex.HResult is ErrorSharingViolation or ErrorLockViolation)
        {
            _logger.LogDebug("Antigravity check skipped: another check is running");
            return new AgyProbeResult(AgyProbeStatus.Busy);
        }
        catch (Exception ex)
        {
            return Report(new AgyProbeResult(AgyProbeStatus.StorageFailed), DescribeStorageError("opening the lock", ex));
        }

        await using (lockStream.ConfigureAwait(false))
        {
            return await RunLockedAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgyProbeResult> RunLockedAsync(AgyProbeRequest request, CancellationToken cancellationToken)
    {
        if (!_swept)
        {
            _swept = true;
            SweepLeftoverProbeFolders();
        }

        var interval = request.Interval;
        var file = ReadSchedule();
        var now = _options.Time.GetUtcNow();
        var uptime = _options.Uptime.TickCount64;
        var bootId = _options.Uptime.BootId;
        var firstCheck = !_bootVerifiedThisProcess;
        var decision = AgyProbeSchedule.Evaluate(file, now, uptime, interval, bootId, firstCheck);

        // Once the record on disk has been judged (or written) by this process,
        // it belongs to this boot: a process never outlives a restart. The flag
        // is set only when the record on disk reflects that judgement, so a
        // failed write is evaluated as a first check again.
        switch (decision.Verdict)
        {
            case AgyScheduleVerdict.Wait:
                _bootVerifiedThisProcess = true;
                return WaitingResult(decision.NextAllowedAt, file.State?.ConsecutiveFailures ?? 0, now);

            case AgyScheduleVerdict.RecordWithoutLaunch:
                _logger.LogDebug(
                    "Antigravity schedule was {Kind}; recorded a check now without starting agy",
                    DescribeRecordReason(file, now, uptime, bootId, firstCheck));
                if (!TryWriteSchedule(decision.Rewrite!, out var rewriteError))
                {
                    return Report(new AgyProbeResult(AgyProbeStatus.StorageFailed), DescribeStorageError("writing the schedule", rewriteError!));
                }

                _bootVerifiedThisProcess = true;
                return WaitingResult(decision.NextAllowedAt, decision.Rewrite!.ConsecutiveFailures, now);
        }

        var storedBackoff = AgyProbeSchedule.ClampBackoff(file.State?.BackoffUntil, now);
        var storedFailures = file.State?.ConsecutiveFailures ?? 0;
        if (!TryWriteSchedule(new AgyProbeScheduleState(now, uptime, InProgress: true, storedBackoff, storedFailures, bootId), out var startError))
        {
            return Report(new AgyProbeResult(AgyProbeStatus.StorageFailed), DescribeStorageError("writing the schedule", startError!));
        }

        _bootVerifiedThisProcess = true;

        var (status, quota, detail) = await AttemptAsync(cancellationToken).ConfigureAwait(false);

        var completedAt = _options.Time.GetUtcNow();
        var completedUptime = _options.Uptime.TickCount64;
        var result = new AgyProbeResult(status, quota, CompletedAt: completedAt);
        var succeeded = status == AgyProbeStatus.Success;
        if (succeeded && quota is not null)
        {
            TryWriteStoredQuota(quota, completedAt);
        }

        // The failure count lives in the shared schedule, so it survives an app
        // restart and is the same for every tray instance. A cancelled check
        // changes neither the count nor the back-off.
        var failuresAfter = AgyProbeSchedule.FailuresAfter(storedFailures, succeeded, result.IsFailure);
        var backoff = status == AgyProbeStatus.Cancelled
            ? storedBackoff
            : AgyProbeSchedule.BackoffAfter(succeeded, failuresAfter, storedBackoff, completedAt, interval);

        if (!TryWriteSchedule(new AgyProbeScheduleState(completedAt, completedUptime, InProgress: false, backoff, failuresAfter, bootId), out var endError))
        {
            // The record still says "in progress", which the next check reads as
            // an interrupted attempt and answers with a full wait, never a launch.
            _logger.LogWarning(
                "Antigravity schedule could not be updated after a check ({Error})",
                DescribeStorageError("writing the schedule", endError!));
        }

        if (status == AgyProbeStatus.Cancelled)
        {
            _logger.LogDebug("Antigravity check cancelled");
            return result;
        }

        return Report(result, detail);
    }

    private async Task<(AgyProbeStatus Status, AgyQuotaReading? Quota, string? Detail)> AttemptAsync(
        CancellationToken cancellationToken)
    {
        string? executable;
        try
        {
            executable = _options.ResolveExecutable();
        }
        catch (Exception ex)
        {
            return (AgyProbeStatus.AgyNotFound, null, ex.GetType().Name);
        }

        if (string.IsNullOrWhiteSpace(executable))
        {
            return (AgyProbeStatus.AgyNotFound, null, null);
        }

        var probeDirectory = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                System.IO.Directory.CreateDirectory(probeDirectory);
            }
            catch (Exception ex)
            {
                return (AgyProbeStatus.StorageFailed, null, DescribeStorageError("creating the probe folder", ex));
            }

            JobObjectProcess? job = null;
            try
            {
                try
                {
                    job = JobObjectProcess.Start(
                        executable,
                        _options.CommandLine(executable, probeDirectory),
                        probeDirectory,
                        _options.Environment());
                }
                catch (Exception ex)
                {
                    var code = ex is Win32Exception win32 ? win32.NativeErrorCode.ToString(CultureInfo.InvariantCulture) : ex.GetType().Name;
                    return (AgyProbeStatus.LaunchFailed, null, "error " + code);
                }

                _options.OnLaunched?.Invoke(job);
                return await WaitForQuotaAsync(job, Path.Combine(probeDirectory, PayloadFileName), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (job is not null)
                {
                    try
                    {
                        job.Terminate(TerminateWait);
                    }
                    finally
                    {
                        job.Dispose();
                    }
                }
            }
        }
        finally
        {
            await DeleteProbeDirectoryAsync(probeDirectory).ConfigureAwait(false);
        }
    }

    private async Task<(AgyProbeStatus Status, AgyQuotaReading? Quota, string? Detail)> WaitForQuotaAsync(
        JobObjectProcess job,
        string payloadPath,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_options.Deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var sawUnsupported = false;
        var exited = false;
        AgyQuotaReading? accepted = null;
        var planGraceEnds = DateTimeOffset.MaxValue;

        while (!linked.IsCancellationRequested)
        {
            // Checked before reading, so a payload written just before exit is still seen.
            exited = job.HasExited;
            var parsed = TryReadPayload(payloadPath);
            if (parsed?.Kind == AgyPayloadKind.Accepted && parsed.Reading is { } reading)
            {
                // agy reports the plan a moment after the quota; wait briefly for it.
                if (!string.IsNullOrWhiteSpace(reading.PlanTier))
                {
                    return (AgyProbeStatus.Success, reading, null);
                }

                if (accepted is null)
                {
                    planGraceEnds = _options.Time.GetUtcNow() + _options.PlanGrace;
                }

                accepted = reading;
            }

            if (accepted is not null && (exited || _options.Time.GetUtcNow() >= planGraceEnds))
            {
                return (AgyProbeStatus.Success, accepted, null);
            }

            sawUnsupported |= parsed?.Kind == AgyPayloadKind.Unsupported;
            if (exited)
            {
                break;
            }

            try
            {
                await Task.Delay(_options.PollInterval, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (AgyProbeStatus.Cancelled, null, null);
        }

        if (accepted is not null)
        {
            return (AgyProbeStatus.Success, accepted, null);
        }

        var reason = exited ? "agy exited" : "deadline passed";
        return sawUnsupported
            ? (AgyProbeStatus.UnsupportedFormat, null, reason)
            : (AgyProbeStatus.NoQuota, null, reason);
    }

    /// <summary>Null while there is nothing to read yet; never logs or keeps the text.</summary>
    private static AgyParseResult? TryReadPayload(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return ReadPayload(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not written yet, or being rewritten right now: try again on the next poll.
            return null;
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxPayloadBytes"/> bytes. The limit is enforced
    /// on the bytes actually read, not on the length seen when the file was
    /// opened, because agy may still be appending; a larger payload is invalid.
    /// </summary>
    internal static AgyParseResult ReadPayload(Stream stream)
    {
        const int limit = MaxPayloadBytes + 1;
        var buffer = ArrayPool<byte>.Shared.Rent(limit);
        try
        {
            var total = 0;
            while (total < limit)
            {
                var read = stream.Read(buffer, total, limit - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total > MaxPayloadBytes
                ? AgyParseResult.Invalid
                : AgyQuotaParser.Parse(Encoding.UTF8.GetString(buffer, 0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private AgyScheduleFile ReadSchedule()
    {
        var path = Path.Combine(_directory, ScheduleFileName);
        try
        {
            return AgyProbeSchedule.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return AgyScheduleFile.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogDebug("Antigravity schedule could not be read ({ErrorKind})", ex.GetType().Name);
            return AgyScheduleFile.Corrupt;
        }
    }

    /// <summary>
    /// A Waiting result, carrying the stored quota when it may be shown: the
    /// last check did not fail, and some window is still current. Read under
    /// the lock, like the schedule.
    /// </summary>
    private AgyProbeResult WaitingResult(DateTimeOffset nextAllowedAt, int consecutiveFailures, DateTimeOffset now)
    {
        var stored = consecutiveFailures == 0 ? ReadStoredQuota(now) : null;
        return new AgyProbeResult(AgyProbeStatus.Waiting, stored?.Quota, nextAllowedAt, stored?.CompletedAt);
    }

    private AgyStoredQuotaReading? ReadStoredQuota(DateTimeOffset now)
    {
        try
        {
            return AgyStoredQuota.Parse(File.ReadAllText(Path.Combine(_directory, LastQuotaFileName), Encoding.UTF8), now);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogDebug("Antigravity stored quota could not be read ({ErrorKind})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Keeps the quota of a successful check for a restarted app. A failure
    /// only costs that: the check itself still succeeded.
    /// </summary>
    private void TryWriteStoredQuota(AgyQuotaReading quota, DateTimeOffset completedAt)
    {
        var temp = Path.Combine(_directory, LastQuotaTempFileName);
        try
        {
            File.WriteAllText(temp, AgyStoredQuota.Serialize(quota, completedAt), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, Path.Combine(_directory, LastQuotaFileName), overwrite: true);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _warnedStoredQuota, 1) == 0)
            {
                _logger.LogWarning("Antigravity quota could not be stored for the next start ({ErrorKind})", ex.GetType().Name);
            }
        }
    }

    /// <summary>Writes the temp file in full, then moves it over the schedule, so a reader never sees half a file.</summary>
    private bool TryWriteSchedule(AgyProbeScheduleState state, out Exception? error)
    {
        var temp = Path.Combine(_directory, ScheduleTempFileName);
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(AgyProbeSchedule.Serialize(state));
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, Path.Combine(_directory, ScheduleFileName), overwrite: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// Deletes every subfolder of the probe folder: attempt folders left by a
    /// crash or a failed delete hold a payload with the account email. Files
    /// (the lock, the schedule) are left alone, and a link is removed without
    /// following it.
    /// </summary>
    private void SweepLeftoverProbeFolders()
    {
        IEnumerable<string> folders;
        try
        {
            folders = System.IO.Directory.EnumerateDirectories(_directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Antigravity probe folder could not be listed ({ErrorKind})", ex.GetType().Name);
            return;
        }

        foreach (var folder in folders)
        {
            try
            {
                var isLink = (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0;
                System.IO.Directory.Delete(folder, recursive: !isLink);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("A leftover Antigravity probe folder could not be deleted ({ErrorKind})", ex.GetType().Name);
            }
        }
    }

    private async Task DeleteProbeDirectoryAsync(string path)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    _logger.LogDebug(
                        "Antigravity probe folder could not be deleted ({ErrorKind}); the next start removes it",
                        ex.GetType().Name);
                    return;
                }

                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Logs an outcome: a failure warns once per kind, repeats go to Debug, and
    /// the first success after a warning says so. Only the outcome kind and a
    /// short fixed reason are logged, never payload or agy log text.
    /// </summary>
    private AgyProbeResult Report(AgyProbeResult result, string? detail)
    {
        if (result.Status == AgyProbeStatus.Success)
        {
            bool recovered;
            lock (_reportGate)
            {
                recovered = _reported.Count > 0;
                _reported.Clear();
            }

            if (recovered)
            {
                _logger.LogInformation("Antigravity check works again");
            }
            else
            {
                _logger.LogDebug("Antigravity check read the quota");
            }

            return result;
        }

        bool first;
        lock (_reportGate)
        {
            first = _reported.Add(result.Status);
        }

        var reason = detail is null ? string.Empty : " (" + detail + ")";
        if (first)
        {
            _logger.LogWarning(
                "Antigravity check failed: {Outcome}{Reason}; repeats are logged at Debug until it works again",
                OutcomeName(result.Status),
                reason);
        }
        else
        {
            _logger.LogDebug("Antigravity check failed: {Outcome}{Reason}", OutcomeName(result.Status), reason);
        }

        return result;
    }

    internal static string OutcomeName(AgyProbeStatus status) => status switch
    {
        AgyProbeStatus.Success => "success",
        AgyProbeStatus.Waiting => "waiting",
        AgyProbeStatus.Busy => "busy",
        AgyProbeStatus.AgyNotFound => "agy-not-found",
        AgyProbeStatus.LaunchFailed => "launch-failed",
        AgyProbeStatus.StorageFailed => "storage-failed",
        AgyProbeStatus.NoQuota => "no-quota",
        AgyProbeStatus.UnsupportedFormat => "unsupported-format",
        _ => "cancelled"
    };

    private static string DescribeStorageError(string stage, Exception ex) =>
        string.Create(CultureInfo.InvariantCulture, $"{stage}: {ex.GetType().Name} 0x{ex.HResult:X8}");

    private static string DescribeRecordReason(AgyScheduleFile file, DateTimeOffset now, long uptime, long? bootId, bool firstCheck) => file switch
    {
        { Kind: AgyScheduleFileKind.Corrupt } => "unreadable",
        { State.InProgress: true } => "left in progress",
        { State.BootId: { } storedBoot } when AgyProbeSchedule.IsDifferentBoot(storedBoot, bootId) => "from before a restart (boot id)",
        { State: { } state } when firstCheck && (bootId is null || state.BootId is null) => "from a boot that cannot be identified",
        { State: { } state } when state.LastAttemptUptimeMs > uptime => "from before a restart",
        { State: { } state } when state.LastAttemptAt > now + AgyProbeSchedule.FutureTolerance => "stamped in the future",
        { State: { } state } when !AgyProbeSchedule.IsSameBoot(state, now, uptime) => "from another boot or before a clock change",
        { State.BackoffUntil: { } until } when until > now + AgyProbeSchedule.MaximumBackoff => "holding a back-off beyond the limit",
        _ => "not usable"
    };

    /// <summary>
    /// <c>"agy.exe" --settings "{...}" --log-file "&lt;probe&gt;\agy.log"</c>. The log
    /// option keeps agy's per-run log out of the user's own agy folder; the log
    /// is deleted with the probe folder and never read.
    /// </summary>
    public static string BuildAgyCommandLine(string executable, string probeDirectory) =>
        JobObjectProcess.BuildCommandLine(
            executable,
            ["--settings", InlineSettings, "--log-file", Path.Combine(probeDirectory, LogFileName)]);

    /// <summary>
    /// The current environment plus <c>AGY_CLI_DISABLE_AUTO_UPDATE=true</c>. An
    /// explicit block is required: without one agy cannot resolve the profile
    /// folder. The value must be <c>true</c>; <c>1</c> is not honoured.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.Length > 0)
            {
                environment[name] = entry.Value as string ?? string.Empty;
            }
        }

        environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
        return environment;
    }
}
