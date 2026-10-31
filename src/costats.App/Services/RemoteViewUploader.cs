using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using costats.Application.Diagnostics;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Core.Remote;
using costats.Core.RemoteView;
using costats.Core.Tray;
using costats.Infrastructure.Analytics;
using Serilog;

namespace costats.App.Services
{
    /// <summary>
    /// Optional "remote view": after each refresh, PUTs a small non-sensitive
    /// usage snapshot to the built-in endpoint (or a user override) so it can be
    /// read from a phone. Entirely opt-in and best effort: a failed upload never affects
    /// the tray. When usage sharing is on as well, <see cref="Sharing"/> shares
    /// the Usage window's report beside it. Every relay write, of either kind,
    /// goes through one <see cref="RelayWriteQueue"/>.
    /// </summary>
    public sealed class RemoteViewUploader : IObserver<PulseState>, IDisposable
    {
        /// <summary>Floor between two successful uploads, so a burst of manual refreshes doesn't hammer the endpoint.</summary>
        private static readonly TimeSpan MinimumUploadInterval = TimeSpan.FromSeconds(60);

        /// <summary>How often usage sharing checks whether something is due.</summary>
        private static readonly TimeSpan SharingTick = TimeSpan.FromMinutes(1);

        private readonly IDisposable _pulseSubscription;
        private readonly IEnumerable<ISignalSource> _staticSources;
        private readonly IAccountSourceRegistry _accountSources;
        private readonly AppSettings _settings;
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
        private readonly ILogger? _log;
        private readonly RelayWriteQueue _queue = new();
        private readonly RemoteRelayClient _relay;
        private readonly Timer? _sharingTimer;

        private DateTimeOffset _lastSuccessfulUpload = DateTimeOffset.MinValue;
        private int _consecutiveFailures;
        private int _uploadInFlight;
        private int _refreshAfterUpload;
        private long _snapshotGeneration;
        private int _warnedAboutRejectedUrl;
        private PulseState? _lastState;

        public RemoteViewUploader(
            IPulseOrchestrator pulseOrchestrator,
            IEnumerable<ISignalSource> sources,
            IAccountSourceRegistry accountSources,
            AppSettings settings,
            IUsageAnalyticsService analytics,
            ISettingsStore settingsStore)
            : this(pulseOrchestrator, sources, accountSources, settings, new HttpClient(), log: null,
                analytics, settingsStore, TimeProvider.System, startSharingTimer: true, limits: null)
        {
        }

        /// <summary>Test seam: the handler stands in for the relay, the logger for the log.</summary>
        internal RemoteViewUploader(
            IPulseOrchestrator pulseOrchestrator,
            IEnumerable<ISignalSource> sources,
            IAccountSourceRegistry accountSources,
            AppSettings settings,
            HttpMessageHandler handler,
            ILogger log,
            IUsageAnalyticsService? analytics = null,
            ISettingsStore? settingsStore = null,
            TimeProvider? clock = null,
            RemoteUsageLimits? limits = null)
            : this(pulseOrchestrator, sources, accountSources, settings, new HttpClient(handler), log,
                analytics, settingsStore, clock ?? TimeProvider.System, startSharingTimer: false, limits)
        {
        }

        private RemoteViewUploader(
            IPulseOrchestrator pulseOrchestrator,
            IEnumerable<ISignalSource> sources,
            IAccountSourceRegistry accountSources,
            AppSettings settings,
            HttpClient http,
            ILogger? log,
            IUsageAnalyticsService? analytics,
            ISettingsStore? settingsStore,
            TimeProvider clock,
            bool startSharingTimer,
            RemoteUsageLimits? limits)
        {
            _staticSources = sources;
            _accountSources = accountSources;
            _settings = settings;
            _log = log;
            _http = http;
            _http.Timeout = TimeSpan.FromSeconds(10);
            _relay = new RemoteRelayClient(_http, () => _settings.EffectiveRemoteViewSigningKey, () => clock.GetUtcNow(), () => Logger);

            Sharing = new RemoteUsageSharing(
                settings,
                settingsStore is null ? _ => Task.CompletedTask : token => settingsStore.SaveAsync(settings, token),
                _queue,
                _relay,
                analytics is null ? null : analytics.GetRemoteUsageSourceAsync,
                () => analytics?.TimeZone ?? TimeZoneInfo.Local,
                clock,
                () => Logger,
                limits,
                startCyclesOnChange: startSharingTimer);
            Sharing.SnapshotRefreshRequested += (_, _) => PublishNow();

            _pulseSubscription = pulseOrchestrator.PulseStream.Subscribe(this);

            if (startSharingTimer)
            {
                // The first tick also retries removals saved by an earlier run.
                _sharingTimer = new Timer(_ => _ = Sharing.TickAsync(), null, TimeSpan.FromSeconds(30), SharingTick);
            }
        }

        /// <summary>Usage sharing: schedule, uploads, removals and the snapshot's usage field.</summary>
        public RemoteUsageSharing Sharing { get; }

        /// <summary>The queue every relay write goes through; tests wait on it.</summary>
        internal RelayWriteQueue Queue => _queue;

        // Resolved on every call: the static logger is replaced once the host starts.
        // Log lines carry the relay's host name at most: the upload URL ends in
        // the secret write id, so no URL or id is ever logged.
        private ILogger Logger => _log ?? Log.Logger;

        private static string HostOf(string url) => RemoteRelayClient.HostOf(url);

        public void OnNext(PulseState state)
        {
            var claimedSlot = false;
            try
            {
                if (state.IsRefreshing || state.Providers.Count == 0)
                {
                    return;
                }

                _lastState = state;
                var uploadUrl = ResolveUploadUrl();
                if (!_settings.RemoteViewEnabled ||
                    !RemoteViewIds.IsValidId(_settings.RemoteViewId) ||
                    uploadUrl is null)
                {
                    if (_settings.RemoteViewEnabled)
                    {
                        Logger.Debug(
                            "Remote view upload skipped: {Reason}",
                            uploadUrl is null ? "no usable upload address" : "no valid write id yet");
                    }

                    return;
                }

                var now = DateTimeOffset.UtcNow;
                if (now - _lastSuccessfulUpload < MinimumUploadInterval)
                {
                    Logger.Debug(
                        "Remote view upload skipped: the last one was {Seconds:F0} s ago",
                        (now - _lastSuccessfulUpload).TotalSeconds);
                    return;
                }

                // One upload at a time; a slow endpoint must not queue refreshes up.
                if (Interlocked.Exchange(ref _uploadInFlight, 1) == 1)
                {
                    Logger.Debug("Remote view upload skipped: the previous one is still running");
                    return;
                }

                claimedSlot = true;
                _ = EnqueueSnapshotUpload(PrepareSnapshotUpload(state, now));
            }
            catch (Exception ex)
            {
                if (claimedSlot)
                {
                    Interlocked.Exchange(ref _uploadInFlight, 0);
                }

                // OnNext must never throw: it would tear down the pulse stream.
                Logger.Warning("Remote view snapshot could not be prepared: {ErrorKind}", ex.GetType().Name);
            }
        }

        /// <summary>
        /// Builds a snapshot upload bound to where it goes and to the delete
        /// generation it was built in, so it can be dropped if remote view was
        /// turned off, or its snapshot deleted, before its turn in the queue.
        /// </summary>
        internal SnapshotUpload PrepareSnapshotUpload(PulseState state, DateTimeOffset now)
        {
            var generation = Interlocked.Read(ref _snapshotGeneration);
            // The write id, never the read id: only this app can PUT.
            var url = SnapshotUrl() ?? throw new InvalidOperationException("Remote view has no upload address.");
            return new SnapshotUpload(url, Compose(state, now), generation);
        }

        internal Task EnqueueSnapshotUpload(SnapshotUpload upload) => _queue.Enqueue(() => UploadAsync(upload));

        /// <summary>Where the snapshot goes right now, or null while remote view is off or unusable.</summary>
        private string? SnapshotUrl()
        {
            var uploadUrl = _settings.EffectiveRemoteViewUploadUrl;
            return _settings.RemoteViewEnabled && RemoteViewIds.IsValidId(_settings.RemoteViewId) && uploadUrl is not null
                ? $"{uploadUrl.TrimEnd('/')}/u/{_settings.RemoteViewId}"
                : null;
        }

        internal sealed record SnapshotUpload(string Url, RemoteSnapshot Snapshot, long Generation);

        public void OnError(Exception error)
        {
            Logger.Warning("Remote view usage stream failed: {ErrorKind}", error.GetType().Name);
        }

        public void OnCompleted()
        {
        }

        /// <summary>
        /// Uploads the snapshot again now, skipping the throttle, so the viewer
        /// learns about a new usage report without waiting for the next refresh.
        /// An upload already running is followed by one more.
        /// </summary>
        private void PublishNow()
        {
            if (Volatile.Read(ref _uploadInFlight) == 1)
            {
                Interlocked.Exchange(ref _refreshAfterUpload, 1);
                return;
            }

            RequestImmediateUpload();
            if (_lastState is { } state)
            {
                OnNext(state);
            }
        }

        private RemoteSnapshot Compose(PulseState state, DateTimeOffset generatedAt)
        {
            // Display names are recomputed every time so account renames in
            // Settings show up on the next upload without a restart.
            var displayNames = _staticSources
                .Concat(_accountSources.Current)
                .Select(source => source.Profile)
                .GroupBy(profile => profile.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);

            var entries = state.Providers
                .Where(pair => IsPublished(pair.Key))
                .Select(pair => new RemoteSnapshotEntry(
                    pair.Key,
                    displayNames.TryGetValue(pair.Key, out var displayName) ? displayName : pair.Key,
                    pair.Value.Identity?.Plan ?? string.Empty,
                    pair.Value.Usage,
                    new RemoteAlertSettings(
                        _settings.UsageAlertsEnabled && _settings.IsUsageAlertProviderEnabled(pair.Key),
                        _settings.GetUsageAlertThreshold(pair.Key),
                        _settings.UsageAlertsEnabled &&
                        _settings.UsageResetAlertsEnabled &&
                        _settings.IsUsageAlertProviderEnabled(pair.Key))))
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // The primary account is named only when it is published itself, so
            // an account kept off the remote view (Antigravity) never leaks its
            // id through the "primary" field either.
            var primary = _settings.PrimaryAccountId;
            return RemoteSnapshotComposer.Compose(
                !string.IsNullOrWhiteSpace(primary) && IsPublished(primary) ? primary : null,
                entries,
                generatedAt,
                _settings.ShowRemainingPercentages,
                Sharing.SnapshotStatus());
        }

        /// <summary>
        /// Exactly the account filter the tray surfaces use. Shared so the two
        /// cannot drift: what you see in the tray is what the phone sees. The
        /// one deliberate exception is Antigravity, which is never published:
        /// the remote viewer does not support it yet.
        /// </summary>
        private bool IsPublished(string providerId) =>
            !providerId.Equals(TrayAccountFilter.AntigravityProviderId, StringComparison.OrdinalIgnoreCase) &&
            TrayAccountFilter.IsVisible(providerId, _settings.HasZaiKey, _settings.CopilotEnabled, antigravityEnabled: false);

        /// <summary>
        /// The upload endpoint, or null when none is configured or the user's
        /// override fails the https rule. A rejected override disables uploads
        /// rather than falling back to the built-in endpoint, so the snapshot
        /// never goes somewhere the user configured away from. Warns once per
        /// session so the feature does not just look broken.
        /// </summary>
        private string? ResolveUploadUrl()
        {
            var resolved = _settings.EffectiveRemoteViewUploadUrl;
            var overrideUrl = _settings.RemoteViewUploadUrl;

            if (!string.IsNullOrWhiteSpace(overrideUrl) &&
                !RemoteViewEndpoints.IsAllowed(overrideUrl) &&
                Interlocked.Exchange(ref _warnedAboutRejectedUrl, 1) == 0)
            {
                Logger.Warning(
                    "Remote view upload URL is not https; uploads stay off until it is fixed or cleared");
            }

            return resolved;
        }

        /// <summary>
        /// Clears the upload throttle so the next pulse publishes immediately.
        /// Used after the write id is rotated, where waiting a minute would leave
        /// the freshly copied link showing nothing.
        /// </summary>
        public void RequestImmediateUpload() => _lastSuccessfulUpload = DateTimeOffset.MinValue;

        /// <summary>
        /// Removes the snapshot stored under <paramref name="writeId"/>. Best
        /// effort: returns false instead of throwing, because the caller is a UI
        /// toggle that must not fail. The server answers 204 whether or not
        /// anything was stored. Queued behind every earlier write, and it
        /// carries a usage write sequence, so the relay's shared usage report
        /// is retired along with it and never outlives a later upload.
        /// </summary>
        public Task<bool> DeleteAsync(string? writeId)
        {
            var uploadUrl = ResolveUploadUrl();
            if (!RemoteViewIds.IsValidId(writeId) || uploadUrl is null)
            {
                return Task.FromResult(false);
            }

            var url = $"{uploadUrl.TrimEnd('/')}/u/{writeId}";
            var seq = Sharing.NextSeq();
            // Snapshot uploads built before this point must not bring it back.
            Interlocked.Increment(ref _snapshotGeneration);
            return _queue.Enqueue(async () =>
            {
                // The sequence is saved first; a failed save does not hold a removal back.
                await Sharing.PersistAsync().ConfigureAwait(false);
                var response = await _relay.SendAsync(HttpMethod.Delete, url, null, seq, "delete").ConfigureAwait(false);
                if (response.IsSuccess)
                {
                    return true;
                }

                Logger.Warning(
                    "Remote view delete answered {Status}",
                    response.FailureKind ?? $"HTTP {response.StatusCode}");
                return false;
            });
        }

        private async Task UploadAsync(SnapshotUpload upload)
        {
            var (url, snapshot, generation) = upload;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Built before remote view was turned off, its link changed or
                // its snapshot was deleted: sending it now would undo that.
                if (generation != Interlocked.Read(ref _snapshotGeneration) ||
                    !string.Equals(SnapshotUrl(), url, StringComparison.Ordinal))
                {
                    Logger.Debug("Remote view upload dropped: remote view changed before it was sent");
                    return;
                }

                var json = JsonSerializer.Serialize(snapshot, _serializerOptions);
                using var content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                // The signature covers the exact bytes of the body, so it is
                // taken over the serialized JSON that is about to be sent.
                _relay.Sign(request, json);

                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                Logger.Debug(
                    "Remote view upload to {Host}: HTTP {StatusCode} in {ElapsedMs} ms ({Bytes} bytes)",
                    HostOf(url),
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    json.Length);
                response.EnsureSuccessStatusCode();

                _lastSuccessfulUpload = DateTimeOffset.UtcNow;
                Sharing.NoteSnapshotPublished(snapshot.Usage);

                // Only announce recovery when something was actually broken.
                if (Interlocked.Exchange(ref _consecutiveFailures, 0) > 0)
                {
                    Logger.Information("Remote view upload recovered");
                }
            }
            catch (Exception ex)
            {
                // Log once per failure streak so an unreachable endpoint does
                // not fill the log with one warning per refresh.
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures == 1)
                {
                    Logger.Warning(
                        "Remote view upload failed; further failures are silent until it recovers: {ErrorKind}",
                        ex.GetType().Name);
                }

                Logger.Debug(
                    "Remote view upload failed ({Failures} in a row) after {ElapsedMs} ms: {ErrorKind}: {Error}",
                    failures,
                    stopwatch.ElapsedMilliseconds,
                    ex.GetType().Name,
                    LogRedaction.Redact(ex.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _uploadInFlight, 0);
                if (Interlocked.Exchange(ref _refreshAfterUpload, 0) == 1)
                {
                    PublishNow();
                }
            }
        }

        public void Dispose()
        {
            _sharingTimer?.Dispose();
            _pulseSubscription.Dispose();
            _http.Dispose();
        }
    }
}
