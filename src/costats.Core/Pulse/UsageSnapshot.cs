using System.Text.Json;

namespace costats.Core.Pulse;

/// <summary>
/// One provider's last good usage, kept so the next start can show it before
/// the first refresh. Only usage and the plan name: no display name, email,
/// organisation, reset credits or sign-in state.
/// </summary>
public sealed record UsageSnapshotEntry(
    string ProviderId,
    UsagePulse Usage,
    string? Plan,
    DateTimeOffset CapturedAt,
    ReadingSource Source,
    ReadingConfidence Confidence);

/// <summary>The startup cache file (<c>last-usage.json</c>).</summary>
public sealed record UsageSnapshot(DateTimeOffset SavedAt, IReadOnlyList<UsageSnapshotEntry> Entries)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The entry to store for a live reading, or null when it has no usage to keep.</summary>
    public static UsageSnapshotEntry? EntryFor(string providerId, ProviderReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Usage is not { } usage)
        {
            return null;
        }

        return new UsageSnapshotEntry(
            providerId,
            usage with
            {
                IsBlocked = false,
                ResetCreditsAvailable = 0,
                ResetCredits = null,
                ResetCreditExpiresAt = null
            },
            reading.Identity?.Plan,
            reading.CapturedAt,
            reading.Source,
            reading.Confidence);
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new FileShape(CurrentVersion, SavedAt.ToUniversalTime(), Entries), Options);

    /// <summary>The stored snapshot, or null when the text is not a known version or is stamped in the future.</summary>
    public static UsageSnapshot? Parse(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        FileShape? file;
        try
        {
            file = JsonSerializer.Deserialize<FileShape>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }

        if (file is null || file.Version != CurrentVersion || RestoredUsage.IsFromTheFuture(file.SavedAt, now))
        {
            return null;
        }

        var entries = (file.Entries ?? [])
            .Where(entry => entry is { ProviderId: not null, Usage: not null })
            .ToList();
        return new UsageSnapshot(file.SavedAt, entries);
    }

    private sealed record FileShape(int Version, DateTimeOffset SavedAt, IReadOnlyList<UsageSnapshotEntry>? Entries);
}
