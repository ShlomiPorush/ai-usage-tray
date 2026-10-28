using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using costats.Application.Diagnostics;
using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Core.Remote;
using costats.Core.RemoteView;
using Serilog;

namespace costats.App.Services
{
    /// <summary>One place a usage report can be shared to: a relay and a write id.</summary>
    public sealed record RemoteUsageTarget(string UploadUrl, string WriteId)
    {
        /// <summary>The usage route of this target. Never logged: it ends in the write id.</summary>
        public string UsageUrl => $"{UploadUrl.TrimEnd('/')}/u/{WriteId}/usage";

        public bool Matches(RemoteUsagePendingDelete entry) =>
            string.Equals(entry.UploadUrl, UploadUrl, StringComparison.Ordinal) &&
            string.Equals(entry.WriteId, WriteId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shares the Usage window's report through the remote view: composes it
    /// on a schedule, uploads it when it changed, removes it when sharing
    /// stops, and tells the quota snapshot what the viewer may fetch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every write takes a sequence number, <c>max(now in ms, last + 1)</c>,
    /// saved in settings before it is sent, and goes through the one
    /// <see cref="RelayWriteQueue"/>, so the relay can always tell an older
    /// write from a newer one.
    /// </para>
    /// <para>
    /// An upload belongs to the sharing generation (relay, write id, share
    /// flag) it was composed for and is dropped if that changed before it was
    /// sent. A removal is bound to the target it was made for and is never
    /// dropped: it is saved in settings and retried with backoff, at startup
    /// too, until the relay confirms it, whatever the settings say by then.
    /// </para>
    /// </remarks>
    public sealed class RemoteUsageSharing
    {
        /// <summary>Shortest time between two compositions.</summary>
        public static readonly TimeSpan ComposeInterval = TimeSpan.FromMinutes(10);

        /// <summary>An unchanged report is uploaded again after this long, to keep it from expiring.</summary>
        public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(6);

        /// <summary>First retry delay after a failure; it doubles up to <see cref="MaxBackoff"/>.</summary>
        public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(10);

        public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);

        /// <summary>Retry delay while the relay has no usage route.</summary>
        public static readonly TimeSpan MissingRouteRetry = TimeSpan.FromHours(1);

        public const string TimeZonePausedStatus = "Usage sharing is paused: your time zone could not be identified.";
        public const string RemovedStatus = "Usage sharing stopped and the shared usage was removed.";
        public const string RemovalPendingStatus = "Usage sharing stopped. Removal will be retried; the shared usage expires within a week.";

        private readonly AppSettings _settings;
        private readonly Func<CancellationToken, Task> _saveSettings;
        private readonly RelayWriteQueue _queue;
        private readonly RemoteRelayClient _relay;
        private readonly Func<CancellationToken, Task<RemoteUsageSource>>? _source;
        private readonly Func<TimeZoneInfo> _zone;
        private readonly TimeProvider _clock;
        private readonly Func<ILogger> _logger;
        private readonly RemoteUsageLimits? _limits;
        private readonly bool _startCyclesOnChange;

        private readonly object _sync = new();
        private readonly Dictionary<(string Url, string WriteId, long Seq), DeleteRetry> _deleteRetries = [];
        private readonly HashSet<(string Url, string WriteId, long Seq)> _deletesInFlight = [];

        private RemoteUsageTarget? _lastTarget;
        private Success? _success;
        private DateTimeOffset? _checkedAt;
        private DateTimeOffset _nextComposeAt = DateTimeOffset.MinValue;
        private int _failures;
        private bool _waitingForSnapshot;
        private long? _publishedSeq;
        private bool _missingRouteLogged;
        private string? _lastSkipReason;
        private int _cycleRunning;
        private int _cycleWanted;
        private long _generation;
        private string _status = string.Empty;

        /// <param name="settings">The shared settings; sharing state is read from it on every use.</param>
        /// <param name="saveSettings">Saves <paramref name="settings"/> atomically.</param>
        /// <param name="queue">The one queue all relay writes go through.</param>
        /// <param name="relay">Sends the writes.</param>
        /// <param name="source">What the report is composed from, or null to never compose.</param>
        /// <param name="zone">The zone the desktop buckets days by.</param>
        /// <param name="clock">The clock; a fake one in tests.</param>
        /// <param name="logger">Resolved on every use: the static logger is replaced once the host starts.</param>
        /// <param name="limits">Payload limits, the contract's when null.</param>
        /// <param name="startCyclesOnChange">
        /// Compose right away when sharing starts or its target changes, rather
        /// than on the next tick. Tests turn it off to drive every step.
        /// </param>
        public RemoteUsageSharing(
            AppSettings settings,
            Func<CancellationToken, Task> saveSettings,
            RelayWriteQueue queue,
            RemoteRelayClient relay,
            Func<CancellationToken, Task<RemoteUsageSource>>? source,
            Func<TimeZoneInfo> zone,
            TimeProvider clock,
            Func<ILogger> logger,
            RemoteUsageLimits? limits = null,
            bool startCyclesOnChange = true)
        {
            _settings = settings;
            _saveSettings = saveSettings;
            _queue = queue;
            _relay = relay;
            _source = source;
            _zone = zone;
            _clock = clock;
            _logger = logger;
            _limits = limits;
            _startCyclesOnChange = startCyclesOnChange;
            _lastTarget = ActiveTarget();
        }

        /// <summary>Raised when the quota snapshot should be uploaded again to publish a new sequence.</summary>
        public event EventHandler? SnapshotRefreshRequested;

        /// <summary>Raised when <see cref="Status"/> changes; may fire on any thread.</summary>
        public event EventHandler? StatusChanged;

        /// <summary>One line for Settings when sharing cannot run, else empty.</summary>
        public string Status
        {
            get
            {
                lock (_sync)
                {
                    return _status;
                }
            }
        }

        private ILogger Logger => _logger();

        private DateTimeOffset Now => _clock.GetUtcNow();

        /// <summary>
        /// Where the report is shared right now: remote view and usage sharing
        /// on, a valid write id and a usable relay. Null when not shared.
        /// </summary>
        public RemoteUsageTarget? ActiveTarget()
        {
            var url = _settings.EffectiveRemoteViewUploadUrl;
            var writeId = _settings.RemoteViewId;
            return _settings.RemoteViewEnabled &&
                   _settings.RemoteViewShareUsage &&
                   url is not null &&
                   RemoteViewIds.IsValidId(writeId)
                ? new RemoteUsageTarget(url, writeId!)
                : null;
        }

        /// <summary>
        /// The <c>usage</c> field of the next quota snapshot: null while not
        /// shared, otherwise the sequence of the last successful upload to the
        /// current target and when the stored report was last confirmed.
        /// </summary>
        public RemoteUsageShareStatus? SnapshotStatus()
        {
            var target = ActiveTarget();
            if (target is null)
            {
                return null;
            }

            lock (_sync)
            {
                var success = _success is { } last && last.Target == target ? last : null;
                return new RemoteUsageShareStatus(
                    true,
                    success?.Seq,
                    success is not null && _checkedAt is { } checkedAt ? RemoteUsageComposer.FormatInstant(checkedAt) : null);
            }
        }

        /// <summary>
        /// Called after a quota snapshot upload succeeded, with the usage field
        /// it carried. A usage upload that waited for the snapshot runs now.
        /// </summary>
        public void NoteSnapshotPublished(RemoteUsageShareStatus? published)
        {
            bool retry;
            lock (_sync)
            {
                _publishedSeq = published?.Seq;
                retry = _waitingForSnapshot;
                _waitingForSnapshot = false;
                if (retry)
                {
                    _nextComposeAt = Now;
                }
            }

            if (retry)
            {
                // Not awaited: this runs inside the snapshot's own queued write.
                _ = Task.Run(() => RunCycleAsync());
            }
        }

        /// <summary>
        /// Takes the next write sequence and records it in settings. The caller
        /// saves settings before sending the write.
        /// </summary>
        public long NextSeq()
        {
            lock (_sync)
            {
                return NextSeqLocked();
            }
        }

        /// <summary>Saves settings, so a sequence taken with <see cref="NextSeq"/> survives a restart.</summary>
        public Task<bool> PersistAsync() => SaveAsync();

        /// <summary>
        /// Compares the current sharing target with the last one seen. When a
        /// shared target was left (sharing or remote view turned off, a new
        /// link, another relay), queues a removal of the OLD target, recorded
        /// in settings so the caller's next save keeps it. When a target became
        /// shared, its own pending removal is dropped and a prompt upload is
        /// scheduled.
        /// </summary>
        /// <returns>
        /// The removal's outcome (true once the relay confirmed it), or null
        /// when nothing had to be removed.
        /// </returns>
        public Task<bool>? Reconcile()
        {
            Task<bool>? removal = null;
            var startCycle = false;
            // The settings lock first: a save never sees the new target
            // without the removal of the old one.
            lock (_settings.SyncRoot)
            lock (_sync)
            {
                var current = ActiveTarget();
                var previous = _lastTarget;
                if (current == previous)
                {
                    return null;
                }

                _lastTarget = current;
                // Whatever is in flight for the old generation is now stale.
                _generation++;
                if (previous is not null)
                {
                    var entry = new RemoteUsagePendingDelete
                    {
                        UploadUrl = previous.UploadUrl,
                        WriteId = previous.WriteId,
                        Seq = NextSeqLocked()
                    };
                    _settings.RemoteUsagePendingDeletes = [.. PendingDeletes(), entry];
                    _deletesInFlight.Add(KeyOf(entry));
                    removal = _queue.Enqueue(() => RunDeleteAsync(entry));
                    _success = null;
                    _checkedAt = null;
                    _publishedSeq = null;
                }

                if (current is not null)
                {
                    // The new upload's larger sequence would make an old
                    // retried removal a no-op anyway.
                    var remaining = PendingDeletes().Where(entry => !current.Matches(entry)).ToList();
                    if (remaining.Count != PendingDeletes().Count)
                    {
                        _settings.RemoteUsagePendingDeletes = remaining;
                    }

                    _nextComposeAt = Now;
                    _failures = 0;
                    _waitingForSnapshot = false;
                    startCycle = _startCyclesOnChange;
                }
            }

            if (startCycle)
            {
                _ = Task.Run(() => RunCycleAsync());
            }

            return removal;
        }

        /// <summary>
        /// One timer tick: notices settings changes, retries due removals, and
        /// runs a usage cycle when one is due.
        /// </summary>
        public async Task TickAsync()
        {
            try
            {
                if (Reconcile() is not null)
                {
                    await SaveAsync().ConfigureAwait(false);
                }

                await RetryPendingDeletesAsync().ConfigureAwait(false);
                await RunCycleAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A timer tick must never throw.
                Logger.Warning(
                    "Remote usage sharing tick failed: {ErrorKind}: {Error}",
                    ex.GetType().Name,
                    LogRedaction.Redact(ex.Message));
            }
        }

        /// <summary>
        /// Queues every saved removal whose retry is due. Each is sent with the
        /// sequence it was made with, never a new one.
        /// </summary>
        public async Task RetryPendingDeletesAsync()
        {
            var due = new List<RemoteUsagePendingDelete>();
            var expired = false;
            lock (_sync)
            {
                var now = Now;
                var kept = new List<RemoteUsagePendingDelete>();
                foreach (var entry in PendingDeletes())
                {
                    if (!RemoteViewIds.IsValidId(entry.WriteId) ||
                        !RemoteViewEndpoints.IsAllowed(entry.UploadUrl))
                    {
                        expired = true;
                        _deleteRetries.Remove(KeyOf(entry));
                        continue;
                    }

                    kept.Add(entry);
                    var key = KeyOf(entry);
                    if (_deletesInFlight.Contains(key) ||
                        (_deleteRetries.TryGetValue(key, out var retry) && retry.NextAt > now))
                    {
                        continue;
                    }

                    _deletesInFlight.Add(key);
                    due.Add(entry);
                }

                if (expired)
                {
                    _settings.RemoteUsagePendingDeletes = kept;
                }
            }

            if (expired)
            {
                Logger.Information("Remote usage removal dropped: its saved address or id is not valid");
                await SaveAsync().ConfigureAwait(false);
            }

            foreach (var entry in due)
            {
                _ = _queue.Enqueue(() => RunDeleteAsync(entry));
            }
        }

        /// <summary>
        /// Composes the report when due and uploads it when it changed or the
        /// last upload is getting old. One cycle at a time: a cycle asked for
        /// while one runs is run right after it, never dropped.
        /// </summary>
        public async Task RunCycleAsync()
        {
            Interlocked.Exchange(ref _cycleWanted, 1);
            while (Volatile.Read(ref _cycleWanted) == 1)
            {
                // The running cycle sees the request when it finishes.
                if (Interlocked.CompareExchange(ref _cycleRunning, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    Interlocked.Exchange(ref _cycleWanted, 0);
                    await RunOneCycleAsync().ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _cycleRunning, 0);
                }
            }
        }

        private async Task RunOneCycleAsync()
        {
            // Null until this cycle has started; a cycle whose generation is
            // gone by the time it ends leaves the schedule to the new one.
            long? generation = null;
            try
            {
                var now = Now;
                RemoteUsageTarget? target;
                lock (_sync)
                {
                    // The target and its generation are taken together: both
                    // change only together, in Reconcile. A target the
                    // settings already left waits for that Reconcile.
                    target = _lastTarget;

                    // A report waiting for its snapshot is retried as soon as
                    // one is published, and at the next cycle at the latest.
                    if (target is null || target != ActiveTarget() || _source is null || now < _nextComposeAt)
                    {
                        return;
                    }

                    _nextComposeAt = now + ComposeInterval;
                    generation = _generation;
                }

                var zone = _zone();
                if (!RemoteUsageTimeZone.TryGetIanaId(zone, out var ianaId))
                {
                    if (SetStatus(TimeZonePausedStatus))
                    {
                        Logger.Warning("Remote usage sharing paused: the time zone has no IANA name");
                    }

                    return;
                }

                SetStatus(string.Empty);

                var source = await _source(CancellationToken.None).ConfigureAwait(false);
                var composition = RemoteUsageComposer.Compose(source, ianaId, zone, now, _limits);
                if (composition.Payload is not { } payload)
                {
                    lock (_sync)
                    {
                        if (_generation != generation)
                        {
                            return;
                        }

                        _nextComposeAt = now + BackoffLocked();
                        if (_lastSkipReason == composition.SkipReason)
                        {
                            return;
                        }

                        _lastSkipReason = composition.SkipReason;
                    }

                    Logger.Warning("Remote usage report not uploaded: {Reason}", composition.SkipReason);
                    return;
                }

                var changeKey = ChangeKey(payload.ContentHash, target!);
                lock (_sync)
                {
                    if (_generation != generation)
                    {
                        return;
                    }

                    _lastSkipReason = null;
                    if (_success is { } last &&
                        last.Target == target &&
                        last.ChangeKey == changeKey &&
                        now - last.At < HeartbeatInterval)
                    {
                        // Unchanged: the stored report is still current.
                        _checkedAt = now;
                        return;
                    }
                }

                await _queue.Enqueue(() => RunPutAsync(target!, generation.Value, payload, changeKey)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    if (generation is null || _generation == generation)
                    {
                        _nextComposeAt = Now + BackoffLocked();
                    }
                }

                Logger.Warning(
                    "Remote usage report could not be prepared: {ErrorKind}: {Error}",
                    ex.GetType().Name,
                    LogRedaction.Redact(ex.Message));
            }
        }

        /// <summary>True while <paramref name="generation"/> is still the current sharing generation.</summary>
        private bool IsCurrent(RemoteUsageTarget target, long generation)
        {
            lock (_sync)
            {
                return _generation == generation && ActiveTarget() == target;
            }
        }

        private async Task<bool> RunPutAsync(RemoteUsageTarget target, long generation, RemoteUsagePayload payload, string changeKey)
        {
            // Composed for a generation that is gone: never sent.
            if (!IsCurrent(target, generation))
            {
                Logger.Debug("Remote usage upload dropped: sharing changed before it was sent");
                return false;
            }

            var seq = NextSeq();
            if (!await SaveAsync().ConfigureAwait(false))
            {
                lock (_sync)
                {
                    if (_generation == generation)
                    {
                        _nextComposeAt = Now + BackoffLocked();
                    }
                }

                return false;
            }

            if (!IsCurrent(target, generation))
            {
                Logger.Debug("Remote usage upload dropped: sharing changed before it was sent");
                return false;
            }

            var response = await _relay.SendAsync(HttpMethod.Put, target.UsageUrl, payload.ToUtf8Json(seq), null, "usage upload")
                .ConfigureAwait(false);
            var now = Now;
            var refreshSnapshot = false;
            var saveAgain = false;
            lock (_sync)
            {
                if (_generation != generation)
                {
                    // Sharing changed while this upload was out, and a removal
                    // follows it in the queue: its answer must not count as
                    // the current report, nor hold back the next upload.
                    Logger.Debug("Remote usage upload answer ignored: sharing changed while it was sent");
                    return false;
                }

                if (response.IsSuccess)
                {
                    if (_failures > 0)
                    {
                        Logger.Information("Remote usage upload recovered");
                    }

                    _success = new Success(target, seq, changeKey, now);
                    _checkedAt = now;
                    _failures = 0;
                    _nextComposeAt = now + ComposeInterval;
                    refreshSnapshot = _publishedSeq != seq;
                }
                else if (response.StatusCode == 404)
                {
                    // An older relay without the usage route. Quiet and never
                    // permanent: the relay may be updated at any time.
                    _nextComposeAt = now + MissingRouteRetry;
                    if (!_missingRouteLogged)
                    {
                        _missingRouteLogged = true;
                        Logger.Information("Remote usage upload: the relay has no usage route; retrying hourly");
                    }
                }
                else if (response.StatusCode == 409 && response.ErrorCode == "snapshot_missing")
                {
                    // Retried right after the next snapshot upload, or at the
                    // next cycle if none comes.
                    _waitingForSnapshot = true;
                    _nextComposeAt = now + ComposeInterval;
                }
                else if (response.StatusCode == 409 && response.ErrorCode == "stale_write")
                {
                    _settings.RemoteUsageLastSeq = Math.Max(_settings.RemoteUsageLastSeq, now.ToUnixTimeMilliseconds());
                    _nextComposeAt = now + ComposeInterval;
                    saveAgain = true;
                    Logger.Warning("Remote usage upload was older than the stored report; retrying with a newer sequence");
                }
                else
                {
                    var delay = BackoffLocked();
                    if (response.RetryAfter is { } retryAfter && retryAfter > delay)
                    {
                        delay = retryAfter;
                    }

                    _nextComposeAt = now + delay;
                    if (_failures == 1)
                    {
                        Logger.Warning(
                            "Remote usage upload failed ({Status}); retrying with backoff, further failures are silent until it recovers",
                            DescribeFailure(response));
                    }
                }
            }

            if (saveAgain)
            {
                await SaveAsync().ConfigureAwait(false);
            }

            if (refreshSnapshot)
            {
                SnapshotRefreshRequested?.Invoke(this, EventArgs.Empty);
            }

            return response.IsSuccess;
        }

        private async Task<bool> RunDeleteAsync(RemoteUsagePendingDelete entry)
        {
            var key = KeyOf(entry);
            try
            {
                // The entry and its sequence are saved before the request goes
                // out; a failed save does not hold a removal back.
                await SaveAsync().ConfigureAwait(false);

                var target = new RemoteUsageTarget(entry.UploadUrl, entry.WriteId);
                lock (_sync)
                {
                    // The report there is going away, unless a newer upload
                    // already replaced it: the relay keeps that one.
                    if (_success?.Target == target && _success.Seq < entry.Seq)
                    {
                        _success = null;
                        _checkedAt = null;
                    }
                }

                var response = await _relay.SendAsync(HttpMethod.Delete, target.UsageUrl, null, entry.Seq, "usage delete")
                    .ConfigureAwait(false);

                var done = response.IsSuccess ||
                           response.StatusCode == 404 ||
                           (response.StatusCode is >= 400 and < 500 && response.StatusCode != 429);
                if (!done)
                {
                    lock (_sync)
                    {
                        var failures = _deleteRetries.TryGetValue(key, out var retry) ? retry.Failures + 1 : 1;
                        _deleteRetries[key] = new DeleteRetry(Now + Backoff(failures), failures);
                        if (failures == 1)
                        {
                            Logger.Warning("Remote usage removal failed ({Status}); it will be retried", DescribeFailure(response));
                        }
                    }

                    return false;
                }

                if (!response.IsSuccess && response.StatusCode != 404)
                {
                    Logger.Warning("Remote usage removal was refused (HTTP {StatusCode}); not retrying", response.StatusCode);
                }

                lock (_sync)
                {
                    _deleteRetries.Remove(key);
                    _settings.RemoteUsagePendingDeletes = [.. PendingDeletes().Where(pending => KeyOf(pending) != key)];
                }

                await SaveAsync().ConfigureAwait(false);
                return response.IsSuccess || response.StatusCode == 404;
            }
            finally
            {
                lock (_sync)
                {
                    _deletesInFlight.Remove(key);
                }
            }
        }

        private async Task<bool> SaveAsync()
        {
            try
            {
                await _saveSettings(CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    "Remote usage sharing could not save its write sequence: {ErrorKind}: {Error}",
                    ex.GetType().Name,
                    LogRedaction.Redact(ex.Message));
                return false;
            }
        }

        private bool SetStatus(string status)
        {
            lock (_sync)
            {
                if (_status == status)
                {
                    return false;
                }

                _status = status;
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private long NextSeqLocked()
        {
            var seq = Math.Max(Now.ToUnixTimeMilliseconds(), _settings.RemoteUsageLastSeq + 1);
            _settings.RemoteUsageLastSeq = seq;
            return seq;
        }

        private IReadOnlyList<RemoteUsagePendingDelete> PendingDeletes() =>
            _settings.RemoteUsagePendingDeletes ?? [];

        /// <summary>Counts one more failure and returns the delay before the next try.</summary>
        private TimeSpan BackoffLocked() => Backoff(++_failures);

        /// <summary>10 minutes after the first failure, doubling up to 2 hours.</summary>
        public static TimeSpan Backoff(int failures)
        {
            var delay = FirstBackoff;
            for (var step = 1; step < failures && delay < MaxBackoff; step++)
            {
                delay += delay;
            }

            return delay < MaxBackoff ? delay : MaxBackoff;
        }

        /// <summary>
        /// What decides whether an upload is needed: the report's content hash
        /// plus the target, so a new link or relay always gets its own upload.
        /// </summary>
        private static string ChangeKey(string contentHash, RemoteUsageTarget target) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{contentHash}\n{target.UploadUrl}\n{target.WriteId}")));

        private static (string Url, string WriteId, long Seq) KeyOf(RemoteUsagePendingDelete entry) =>
            (entry.UploadUrl, entry.WriteId, entry.Seq);

        private static string DescribeFailure(RelayResponse response) =>
            response.FailureKind ?? $"HTTP {response.StatusCode}{(response.ErrorCode is null ? string.Empty : " " + response.ErrorCode)}";

        private sealed record Success(RemoteUsageTarget Target, long Seq, string ChangeKey, DateTimeOffset At);

        private readonly record struct DeleteRetry(DateTimeOffset NextAt, int Failures);
    }
}
