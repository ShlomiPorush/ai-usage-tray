using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using costats.Application.Diagnostics;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Core.Remote;
using costats.Core.RemoteView;
using costats.Core.Tray;
using Serilog;

namespace costats.App.Services
{
    /// <summary>
    /// Optional "remote view": after each refresh, PUTs a small non-sensitive
    /// usage snapshot to the built-in endpoint (or a user override) so it can be
    /// read from a phone. Entirely opt-in and best effort: a failed upload never affects
    /// the tray.
    /// </summary>
    public sealed class RemoteViewUploader : IObserver<PulseState>, IDisposable
    {
        /// <summary>Floor between two successful uploads, so a burst of manual refreshes doesn't hammer the endpoint.</summary>
        private static readonly TimeSpan MinimumUploadInterval = TimeSpan.FromSeconds(60);

        private readonly IDisposable _pulseSubscription;
        private readonly IEnumerable<ISignalSource> _staticSources;
        private readonly IAccountSourceRegistry _accountSources;
        private readonly AppSettings _settings;
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
        private readonly ILogger? _log;

        private DateTimeOffset _lastSuccessfulUpload = DateTimeOffset.MinValue;
        private int _consecutiveFailures;
        private int _uploadInFlight;
        private int _warnedAboutRejectedUrl;

        public RemoteViewUploader(
            IPulseOrchestrator pulseOrchestrator,
            IEnumerable<ISignalSource> sources,
            IAccountSourceRegistry accountSources,
            AppSettings settings)
            : this(pulseOrchestrator, sources, accountSources, settings, new HttpClient(), log: null)
        {
        }

        /// <summary>Test seam: the handler stands in for the relay, the logger for the log.</summary>
        internal RemoteViewUploader(
            IPulseOrchestrator pulseOrchestrator,
            IEnumerable<ISignalSource> sources,
            IAccountSourceRegistry accountSources,
            AppSettings settings,
            HttpMessageHandler handler,
            ILogger log)
            : this(pulseOrchestrator, sources, accountSources, settings, new HttpClient(handler), log)
        {
        }

        private RemoteViewUploader(
            IPulseOrchestrator pulseOrchestrator,
            IEnumerable<ISignalSource> sources,
            IAccountSourceRegistry accountSources,
            AppSettings settings,
            HttpClient http,
            ILogger? log)
        {
            _staticSources = sources;
            _accountSources = accountSources;
            _settings = settings;
            _log = log;
            _http = http;
            _http.Timeout = TimeSpan.FromSeconds(10);
            _pulseSubscription = pulseOrchestrator.PulseStream.Subscribe(this);
        }

        // Resolved on every call: the static logger is replaced once the host starts.
        // Log lines carry the relay's host name at most: the upload URL ends in
        // the secret write id, so no URL or id is ever logged.
        private ILogger Logger => _log ?? Log.Logger;

        /// <summary>The relay's host name for logs, without the path that carries the write id.</summary>
        private static string HostOf(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid address)";

        public void OnNext(PulseState state)
        {
            try
            {
                if (state.IsRefreshing || state.Providers.Count == 0)
                {
                    return;
                }

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

                var snapshot = Compose(state, now);
                // The write id, never the read id: only this app can PUT.
                var url = $"{uploadUrl.TrimEnd('/')}/u/{_settings.RemoteViewId}";

                // One upload at a time; a slow endpoint must not queue refreshes up.
                if (Interlocked.Exchange(ref _uploadInFlight, 1) == 1)
                {
                    Logger.Debug("Remote view upload skipped: the previous one is still running");
                    return;
                }

                _ = Task.Run(() => UploadAsync(url, snapshot));
            }
            catch (Exception ex)
            {
                // OnNext must never throw: it would tear down the pulse stream.
                Logger.Warning(ex, "Remote view snapshot could not be prepared");
            }
        }

        public void OnError(Exception error)
        {
            Logger.Warning(error, "Remote view usage stream failed");
        }

        public void OnCompleted()
        {
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

            return RemoteSnapshotComposer.Compose(
                _settings.PrimaryAccountId,
                entries,
                generatedAt,
                _settings.ShowRemainingPercentages);
        }

        /// <summary>
        /// Exactly the account filter the tray surfaces use. Shared so the two
        /// cannot drift: what you see in the tray is what the phone sees.
        /// </summary>
        private bool IsPublished(string providerId) =>
            TrayAccountFilter.IsVisible(providerId, _settings.HasZaiKey, _settings.CopilotEnabled);

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
        /// anything was stored.
        /// </summary>
        public async Task<bool> DeleteAsync(string? writeId)
        {
            var uploadUrl = ResolveUploadUrl();
            if (!RemoteViewIds.IsValidId(writeId) || uploadUrl is null)
            {
                return false;
            }

            var url = $"{uploadUrl.TrimEnd('/')}/u/{writeId}";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                Sign(request, string.Empty);

                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                Logger.Debug("Remote view delete at {Host}: HTTP {StatusCode}", HostOf(url), (int)response.StatusCode);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                Logger.Warning(
                    "Remote view delete answered {StatusCode}", (int)response.StatusCode);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Remote view delete failed");
                return false;
            }
        }

        /// <summary>
        /// Adds the remote-view signing headers. The relay never requires them:
        /// they buy a larger share of its per-address write budget, and an older
        /// relay ignores them, so signing can only ever help. Failing to sign is
        /// therefore not a reason to skip the upload.
        /// </summary>
        private void Sign(HttpRequestMessage request, string body)
        {
            try
            {
                var timestamp = RemoteViewSignature.Timestamp(DateTimeOffset.UtcNow);
                var signature = RemoteViewSignature.Sign(
                    _settings.EffectiveRemoteViewSigningKey,
                    timestamp,
                    request.Method.Method,
                    request.RequestUri!.AbsolutePath,
                    body);

                request.Headers.TryAddWithoutValidation(
                    RemoteViewSignature.TimestampHeader,
                    timestamp.ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation(
                    RemoteViewSignature.SignatureHeader,
                    signature);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Remote view request could not be signed; sending it unsigned");
            }
        }

        private async Task UploadAsync(string url, RemoteSnapshot snapshot)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var json = JsonSerializer.Serialize(snapshot, _serializerOptions);
                using var content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                // The signature covers the exact bytes of the body, so it is
                // taken over the serialized JSON that is about to be sent.
                Sign(request, json);

                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                Logger.Debug(
                    "Remote view upload to {Host}: HTTP {StatusCode} in {ElapsedMs} ms ({Bytes} bytes)",
                    HostOf(url),
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    json.Length);
                response.EnsureSuccessStatusCode();

                _lastSuccessfulUpload = DateTimeOffset.UtcNow;

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
                    Logger.Warning(ex, "Remote view upload failed; further failures are silent until it recovers");
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
            }
        }

        public void Dispose()
        {
            _pulseSubscription.Dispose();
            _http.Dispose();
        }
    }
}
