using System.Globalization;
using costats.Application.Abstractions;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Core.Tray;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Application.Pulse;

/// <summary>
/// Keeps each provider's last good usage on disk, so the next start can show
/// it while the first refresh runs. It is display only: the orchestrator never
/// makes it the current state, so nothing acts on it.
///
/// A generation guards against account changes: <see cref="Clear"/> bumps it,
/// and a refresh that captured an older generation can neither refill the map
/// nor write the file. Writes go through one sequential writer that only ever
/// writes the newest snapshot.
/// </summary>
public sealed class UsageStartupCache
{
    private readonly IUsageSnapshotStore _store;
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageSnapshotEntry> _lastGood = new(StringComparer.OrdinalIgnoreCase);

    private long _generation;
    private string? _pendingText;
    private long _pendingGeneration;
    private bool _pendingDelete;
    private bool _writerRunning;
    private Task _writer = Task.CompletedTask;
    private int _warnedWrite;

    public UsageStartupCache(IUsageSnapshotStore store, AppSettings settings, IClock clock, ILogger<UsageStartupCache>? logger = null)
    {
        _store = store;
        _settings = settings;
        _clock = clock;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Taken before a refresh reads its sources; see <see cref="Observe"/>.</summary>
    public long CaptureGeneration()
    {
        lock (_gate)
        {
            return _generation;
        }
    }

    /// <summary>
    /// Records the readings of a finished refresh. A reading with usage
    /// replaces its provider's entry, a sign-in prompt removes it, and a
    /// reading without usage (a passing failure) keeps it. A full refresh also
    /// drops providers it no longer reports. Restored readings never count.
    /// </summary>
    public void Observe(long generation, IReadOnlyDictionary<string, ProviderReading> readings, bool fullRefresh)
    {
        ArgumentNullException.ThrowIfNull(readings);
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            foreach (var (providerId, reading) in readings)
            {
                if (reading.IsRestored)
                {
                    continue;
                }

                if (reading.AuthenticationState == ProviderAuthenticationState.SignInRequired)
                {
                    _lastGood.Remove(providerId);
                }
                else if (UsageSnapshot.EntryFor(providerId, reading) is { } entry)
                {
                    _lastGood[providerId] = entry;
                }
            }

            if (fullRefresh)
            {
                foreach (var gone in _lastGood.Keys.Where(providerId => !readings.ContainsKey(providerId)).ToList())
                {
                    _lastGood.Remove(gone);
                }
            }

            _pendingText = new UsageSnapshot(_clock.UtcNow, _lastGood.Values.ToList()).Serialize();
            _pendingGeneration = generation;
        }

        StartWriter();
    }

    /// <summary>
    /// Forgets everything after an account change, so a later start never
    /// shows an earlier account's numbers. Never waits for the disk.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _lastGood.Clear();
            _pendingText = null;
            _pendingDelete = true;
        }

        StartWriter();
    }

    /// <summary>
    /// The stored usage as a display-only refreshing state, or null when
    /// nothing current is stored. Only providers among
    /// <paramref name="currentProviderIds"/> that the tray would show now are
    /// kept, each with only its still-current windows, marked restored.
    /// </summary>
    public PulseState? BuildStartupState(IEnumerable<string> currentProviderIds)
    {
        ArgumentNullException.ThrowIfNull(currentProviderIds);
        var now = _clock.UtcNow;
        var snapshot = UsageSnapshot.Parse(_store.Read(), now);
        if (snapshot is null)
        {
            return null;
        }

        var current = new HashSet<string>(currentProviderIds, StringComparer.OrdinalIgnoreCase);
        var providers = new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<UsageSnapshotEntry>();
        foreach (var entry in snapshot.Entries)
        {
            if (!current.Contains(entry.ProviderId) ||
                !TrayAccountFilter.IsVisible(entry.ProviderId, _settings.HasZaiKey, _settings.CopilotEnabled, _settings.AntigravityEnabled) ||
                RestoredUsage.KeepCurrent(entry.Usage, entry.CapturedAt, now) is not { } usage)
            {
                continue;
            }

            kept.Add(entry);
            providers[entry.ProviderId] = new ProviderReading(
                usage,
                new IdentityCard(entry.ProviderId, null, null, null, entry.Plan, null),
                $"Updated {TimeZoneInfo.ConvertTime(entry.CapturedAt, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture)}",
                entry.CapturedAt,
                entry.Confidence,
                entry.Source)
            {
                IsRestored = true
            };
        }

        if (providers.Count == 0)
        {
            return null;
        }

        // The stored entries stay the last good ones until a refresh replaces
        // them, so a first refresh that fails for a provider does not drop it.
        lock (_gate)
        {
            foreach (var entry in kept)
            {
                _lastGood.TryAdd(entry.ProviderId, entry);
            }
        }

        return new PulseState(providers, snapshot.SavedAt, [], IsRefreshing: true, RefreshTrigger.Cached);
    }

    /// <summary>Completes when the writer has nothing left to do; for tests.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _writer;
        }
    }

    private void StartWriter()
    {
        lock (_gate)
        {
            if (_writerRunning)
            {
                return;
            }

            _writerRunning = true;
            _writer = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            bool delete;
            string? text;
            long generation;
            lock (_gate)
            {
                delete = _pendingDelete;
                text = _pendingText;
                generation = _pendingGeneration;
                _pendingDelete = false;
                _pendingText = null;
                if (!delete && text is null)
                {
                    _writerRunning = false;
                    return;
                }
            }

            try
            {
                if (delete)
                {
                    await _store.DeleteAsync().ConfigureAwait(false);
                }

                bool current;
                lock (_gate)
                {
                    current = generation == _generation;
                }

                // A Clear() after this check queues a delete, which the next
                // pass runs, so a stale write never outlives it.
                if (text is not null && current)
                {
                    await _store.WriteAsync(text).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _warnedWrite, 1) == 0)
                {
                    _logger.LogWarning("Usage cache could not be updated ({ErrorKind})", ex.GetType().Name);
                }
            }
        }
    }
}
