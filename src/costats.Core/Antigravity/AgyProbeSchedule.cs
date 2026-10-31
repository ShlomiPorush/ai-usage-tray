using System.Globalization;
using System.Text;
using System.Text.Json;

namespace costats.Core.Antigravity;

/// <summary>
/// The persisted record of the last Antigravity check, shared by every tray
/// instance of the user. <see cref="LastAttemptUptimeMs"/> is
/// <see cref="Environment.TickCount64"/> at that moment, so a clock change
/// cannot shorten the wait and a restart is recognised.
/// <see cref="ConsecutiveFailures"/> counts failed checks in a row, so the
/// back-off holds across app restarts and between tray instances.
/// <see cref="BootId"/> is the Windows boot counter when the record was
/// written, or null when it could not be read; a different value means the
/// machine restarted.
/// </summary>
public sealed record AgyProbeScheduleState(
    DateTimeOffset LastAttemptAt,
    long LastAttemptUptimeMs,
    bool InProgress,
    DateTimeOffset? BackoffUntil,
    int ConsecutiveFailures = 0,
    long? BootId = null);

/// <summary>How the schedule file looked when it was read.</summary>
public enum AgyScheduleFileKind
{
    /// <summary>No file: there was no previous attempt.</summary>
    Missing,

    /// <summary>A file that could not be read or understood.</summary>
    Corrupt,

    /// <summary>A valid version 1 file.</summary>
    Valid
}

public sealed record AgyScheduleFile(AgyScheduleFileKind Kind, AgyProbeScheduleState? State)
{
    public static AgyScheduleFile Missing { get; } = new(AgyScheduleFileKind.Missing, null);
    public static AgyScheduleFile Corrupt { get; } = new(AgyScheduleFileKind.Corrupt, null);
    public static AgyScheduleFile Valid(AgyProbeScheduleState state) => new(AgyScheduleFileKind.Valid, state);
}

public enum AgyScheduleVerdict
{
    /// <summary>The check may run now.</summary>
    Launch,

    /// <summary>Too early; nothing is written.</summary>
    Wait,

    /// <summary>
    /// The stored record cannot be trusted (unreadable, an unfinished attempt,
    /// a restart, a wall-clock change, a stamp in the future, or a back-off
    /// beyond the clamp limit): it is replaced by an attempt at now without
    /// starting agy, keeping the failure count and the (clamped) back-off, so
    /// the next check waits a full interval.
    /// </summary>
    RecordWithoutLaunch
}

/// <param name="NextAllowedAt">Earliest moment the next check may run (UTC).</param>
/// <param name="Rewrite">The record to store for <see cref="AgyScheduleVerdict.RecordWithoutLaunch"/>.</param>
public sealed record AgyScheduleDecision(
    AgyScheduleVerdict Verdict,
    DateTimeOffset NextAllowedAt,
    AgyProbeScheduleState? Rewrite);

/// <summary>
/// Pure rules for when the tray may start agy. Every rule is evaluated with
/// the interval the user has set now, and a stored back-off is never made
/// shorter except by the clamp that guards against a stamp from a wrong clock
/// (further away than any setting can produce).
/// </summary>
public static class AgyProbeSchedule
{
    public const int CurrentVersion = 1;

    /// <summary>A last attempt further than this in the future means the clock went back.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How far apart the boot instants of the stored record and of now
    /// (wall clock minus uptime) may be and still count as the same boot.
    /// Anything further is a restart or a wall-clock change, both answered
    /// with a full wait.
    /// </summary>
    public static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>The shortest back-off after repeated failures.</summary>
    public static readonly TimeSpan MinimumBackoff = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The longest back-off any setting can produce (the largest allowed
    /// interval, 240 minutes). A stored back-off further away than this came
    /// from a wrong clock; nothing shorter is ever cut.
    /// </summary>
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(240);

    /// <summary>Consecutive failures that start the back-off.</summary>
    public const int FailuresBeforeBackoff = 3;

    /// <summary>How long a back-off lasts for <paramref name="interval"/>: the interval, at least an hour.</summary>
    public static TimeSpan BackoffLength(TimeSpan interval) =>
        interval > MinimumBackoff ? interval : MinimumBackoff;

    /// <param name="bootId">
    /// The current Windows boot counter, or null when it is unavailable. A
    /// known value that differs from the stored one is a restart.
    /// </param>
    /// <param name="firstCheckInThisProcess">
    /// True for the first evaluation in this tray process. A process cannot
    /// outlive a restart, so once one evaluation has run, every later record
    /// was seen or written in the same boot. When the boot cannot be compared
    /// (either id unknown), the first evaluation of an existing record answers
    /// with a full wait instead of trusting it.
    /// </param>
    public static AgyScheduleDecision Evaluate(
        AgyScheduleFile file,
        DateTimeOffset now,
        long uptimeMs,
        TimeSpan interval,
        long? bootId = null,
        bool firstCheckInThisProcess = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        now = now.ToUniversalTime();

        if (file.Kind == AgyScheduleFileKind.Missing)
        {
            return new AgyScheduleDecision(AgyScheduleVerdict.Launch, now, null);
        }

        if (file.Kind == AgyScheduleFileKind.Corrupt || file.State is null)
        {
            return RecordAtNow(now, uptimeMs, interval, backoffUntil: null, consecutiveFailures: 0, bootId);
        }

        var state = file.State;
        var backoff = ClampBackoff(state.BackoffUntil, now);

        // A back-off beyond the clamp limit is stored clamped, so the limit is
        // not recomputed from a later "now" on every refresh.
        var backoffOutOfRange = state.BackoffUntil is { } storedBackoff &&
                                storedBackoff.ToUniversalTime() > now + MaximumBackoff;

        // The boot id is the reliable restart signal; the uptime and boot-instant
        // rules stay for a missing boot id and for wall-clock changes. Without
        // both ids, the first check of this process does not trust the record.
        if (state.InProgress ||
            IsDifferentBoot(state.BootId, bootId) ||
            (firstCheckInThisProcess && (bootId is null || state.BootId is null)) ||
            state.LastAttemptUptimeMs > uptimeMs ||
            state.LastAttemptAt.ToUniversalTime() > now + FutureTolerance ||
            !IsSameBoot(state, now, uptimeMs) ||
            backoffOutOfRange)
        {
            return RecordAtNow(now, uptimeMs, interval, backoff, state.ConsecutiveFailures, bootId);
        }

        var wallGap = now - state.LastAttemptAt.ToUniversalTime();
        var uptimeGap = TimeSpan.FromMilliseconds(uptimeMs - state.LastAttemptUptimeMs);

        if (wallGap >= interval && uptimeGap >= interval && (backoff is null || now >= backoff.Value))
        {
            return new AgyScheduleDecision(AgyScheduleVerdict.Launch, now, null);
        }

        var next = Max(state.LastAttemptAt.ToUniversalTime() + interval, now + (interval - uptimeGap));
        if (backoff is { } until && until > next)
        {
            next = until;
        }

        return new AgyScheduleDecision(AgyScheduleVerdict.Wait, next, null);
    }

    /// <summary>
    /// The back-off to store after an attempt. A success clears it; the third
    /// consecutive failure (and every one after it) starts a new one; anything
    /// else keeps the stored value, so it is never made shorter.
    /// </summary>
    public static DateTimeOffset? BackoffAfter(
        bool succeeded,
        int consecutiveFailuresAfter,
        DateTimeOffset? storedBackoff,
        DateTimeOffset completedAt,
        TimeSpan interval)
    {
        if (succeeded)
        {
            return null;
        }

        if (consecutiveFailuresAfter >= FailuresBeforeBackoff)
        {
            var until = completedAt.ToUniversalTime() + BackoffLength(interval);
            return storedBackoff is { } stored && stored > until ? stored : until;
        }

        return storedBackoff;
    }

    /// <summary>
    /// A back-off further away than <see cref="MaximumBackoff"/> from now is
    /// treated as <see cref="MaximumBackoff"/>. Lowering the interval never
    /// shortens a back-off that is already stored.
    /// </summary>
    public static DateTimeOffset? ClampBackoff(DateTimeOffset? backoffUntil, DateTimeOffset now)
    {
        if (backoffUntil is not { } until)
        {
            return null;
        }

        var limit = now.ToUniversalTime() + MaximumBackoff;
        return until.ToUniversalTime() > limit ? limit : until.ToUniversalTime();
    }

    public static AgyScheduleFile Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return AgyScheduleFile.Corrupt;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionNumber) ||
                versionNumber != CurrentVersion ||
                !TryReadInstant(root, "lastAttemptAt", out var lastAttemptAt) ||
                lastAttemptAt is null ||
                !root.TryGetProperty("lastAttemptUptimeMs", out var uptime) ||
                uptime.ValueKind != JsonValueKind.Number ||
                !uptime.TryGetInt64(out var uptimeMs) ||
                uptimeMs < 0 ||
                !root.TryGetProperty("inProgress", out var inProgress) ||
                inProgress.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !TryReadInstant(root, "backoffUntil", out var backoffUntil, allowNull: true) ||
                !root.TryGetProperty("consecutiveFailures", out var failures) ||
                failures.ValueKind != JsonValueKind.Number ||
                !failures.TryGetInt32(out var failureCount) ||
                failureCount < 0 ||
                !TryReadBootId(root, out var storedBootId))
            {
                return AgyScheduleFile.Corrupt;
            }

            return AgyScheduleFile.Valid(new AgyProbeScheduleState(
                lastAttemptAt.Value,
                uptimeMs,
                inProgress.GetBoolean(),
                backoffUntil,
                failureCount,
                storedBootId));
        }
        catch (JsonException)
        {
            return AgyScheduleFile.Corrupt;
        }
    }

    public static string Serialize(AgyProbeScheduleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteString("lastAttemptAt", FormatInstant(state.LastAttemptAt));
            writer.WriteNumber("lastAttemptUptimeMs", state.LastAttemptUptimeMs);
            writer.WriteBoolean("inProgress", state.InProgress);
            if (state.BackoffUntil is { } backoff)
            {
                writer.WriteString("backoffUntil", FormatInstant(backoff));
            }
            else
            {
                writer.WriteNull("backoffUntil");
            }

            writer.WriteNumber("consecutiveFailures", Math.Max(0, state.ConsecutiveFailures));
            if (state.BootId is { } boot && boot >= 0)
            {
                writer.WriteNumber("bootId", boot);
            }
            else
            {
                writer.WriteNull("bootId");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// True when the stored record and now describe the same boot: the boot
    /// instant derived from each (wall clock minus uptime) matches within
    /// <see cref="BootTolerance"/>. Machine sleep is counted in the uptime, so
    /// it keeps the boot instant.
    /// </summary>
    public static bool IsSameBoot(AgyProbeScheduleState state, DateTimeOffset now, long uptimeMs)
    {
        ArgumentNullException.ThrowIfNull(state);
        var storedBoot = state.LastAttemptAt.ToUniversalTime() - TimeSpan.FromMilliseconds(state.LastAttemptUptimeMs);
        var currentBoot = now.ToUniversalTime() - TimeSpan.FromMilliseconds(uptimeMs);
        return (storedBoot - currentBoot).Duration() <= BootTolerance;
    }

    /// <summary>
    /// True only when both boot ids are known and differ. Windows increments
    /// the counter on every boot, so this catches a restart however soon it
    /// follows the previous one.
    /// </summary>
    public static bool IsDifferentBoot(long? storedBootId, long? currentBootId) =>
        storedBootId is { } stored && currentBootId is { } current && stored != current;

    /// <summary>
    /// Failures in a row after an attempt: a success resets the count, a
    /// failure adds one, anything else (a cancelled check) keeps it.
    /// </summary>
    public static int FailuresAfter(int storedFailures, bool succeeded, bool failed) =>
        succeeded ? 0 : failed ? storedFailures + 1 : storedFailures;

    private static AgyScheduleDecision RecordAtNow(
        DateTimeOffset now,
        long uptimeMs,
        TimeSpan interval,
        DateTimeOffset? backoffUntil,
        int consecutiveFailures,
        long? bootId)
    {
        var next = now + interval;
        if (backoffUntil is { } until && until > next)
        {
            next = until;
        }

        return new AgyScheduleDecision(
            AgyScheduleVerdict.RecordWithoutLaunch,
            next,
            new AgyProbeScheduleState(now, Math.Max(0, uptimeMs), InProgress: false, backoffUntil, consecutiveFailures, bootId));
    }

    /// <summary>The required <c>bootId</c>: a non-negative integer, or null when it was unavailable.</summary>
    private static bool TryReadBootId(JsonElement root, out long? bootId)
    {
        bootId = null;
        if (!root.TryGetProperty("bootId", out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value) || value < 0)
        {
            return false;
        }

        bootId = value;
        return true;
    }

    private static bool TryReadInstant(JsonElement root, string name, out DateTimeOffset? value, bool allowNull = false)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return allowNull;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
}
