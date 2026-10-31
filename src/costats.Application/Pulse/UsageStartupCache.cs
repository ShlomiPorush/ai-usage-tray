using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private int _suspended;
    private string? _pendingText;
    private long _pendingGeneration;
    private bool _pendingDelete;
    private bool _writerRunning;
    private Task _writer = Task.CompletedTask;
    private int _warnedWrite;

    // What the previous run stored, read once before any refresh can run.
    private readonly IReadOnlyList<UsageSnapshotEntry> _seed;
    private readonly DateTimeOffset _seedSavedAt;

    public UsageStartupCache(IUsageSnapshotStore store, AppSettings settings, IClock clock, ILogger<UsageStartupCache>? logger = null)
    {
        _store = store;
        _settings = settings;
        _clock = clock;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        // Loaded before the first refresh, so a first refresh that fails for a
        // provider keeps that provider's stored usage instead of writing a
        // file without it. Usage stored for an account whose configuration
        // has changed since (another folder under the same id, or a change
        // made by another instance or before a crash) is dropped here.
        var stored = UsageSnapshot.Parse(_store.Read(), _clock.UtcNow);
        _seed = stored?.Entries.Where(entry => entry.AccountKey == AccountKey(entry.ProviderId)).ToList() ?? [];
        _seedSavedAt = stored?.SavedAt ?? default;
        foreach (var entry in _seed)
        {
            _lastGood[entry.ProviderId] = entry;
        }
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
            if (generation != _generation || _suspended > 0)
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
                    _lastGood[providerId] = entry with { AccountKey = AccountKey(providerId) };
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
    /// <see cref="Clear"/>, then waits until the file is gone from disk (or its
    /// removal failed and was logged). For a change that must hold even if the
    /// app stops right after it.
    /// </summary>
    /// <returns>False when the file was not removed within <paramref name="timeout"/>.</returns>
    public async Task<bool> ClearAsync(TimeSpan timeout)
    {
        Clear();
        var deadline = DateTimeOffset.UtcNow + timeout;
        Task writer;
        do
        {
            writer = WhenIdleAsync();
            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                await writer.WaitAsync(left).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        while (!ReferenceEquals(writer, WhenIdleAsync()));

        return true;
    }

    /// <summary>
    /// Stops recording refreshes until the returned handle is disposed, for an
    /// account change in progress (a sign-in): nothing read meanwhile, which
    /// may still be the previous person's usage, is stored.
    /// </summary>
    public IDisposable Suspend()
    {
        lock (_gate)
        {
            _suspended++;
        }

        return new Suspension(this);
    }

    private sealed class Suspension(UsageStartupCache owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (owner._gate)
                {
                    owner._suspended--;
                }
            }
        }
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
        lock (_gate)
        {
            // After an account change nothing from the previous run is shown.
            if (_generation != 0)
            {
                return null;
            }
        }

        var now = _clock.UtcNow;

        var current = new HashSet<string>(currentProviderIds, StringComparer.OrdinalIgnoreCase);
        var providers = new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _seed)
        {
            if (!current.Contains(entry.ProviderId) ||
                !TrayAccountFilter.IsVisible(entry.ProviderId, _settings.HasZaiKey, _settings.CopilotEnabled, _settings.AntigravityEnabled) ||
                RestoredUsage.KeepCurrent(entry.Usage, entry.CapturedAt, now) is not { } usage)
            {
                continue;
            }

            providers[entry.ProviderId] = new ProviderReading(
                usage with { ProviderId = entry.ProviderId },
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

        return new PulseState(providers, _seedSavedAt, [], IsRefreshing: true, RefreshTrigger.Cached);
    }

    /// <summary>
    /// Which account configuration a provider id stands for now: for a Claude or
    /// Codex account a hash of its type and folder, so the same id pointing at
    /// another folder is another account; for the single providers the id.
    /// </summary>
    private string AccountKey(string providerId)
    {
        try
        {
            return AccountKeyCore(providerId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The account list changed while it was read: a key that matches
            // nothing, so this entry is simply not shown at the next start.
            return "?";
        }
    }

    private string AccountKeyCore(string providerId)
    {
        var separator = providerId.IndexOf(':');
        if (separator <= 0)
        {
            return providerId.ToLowerInvariant();
        }

        var type = providerId[..separator];
        var id = providerId[(separator + 1)..];
        var account = _settings.GetEffectiveAccounts().FirstOrDefault(candidate =>
            string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return string.Empty;
        }

        var folder = NormalizeFolder(account.ConfigDir);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(type.ToLowerInvariant() + "\n" + folder));
        return Convert.ToHexString(bytes, 0, 16);
    }

    private static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        try
        {
            folder = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable path still identifies the account as written.
        }

        return folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
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
