using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

        private DateTimeOffset _lastSuccessfulUpload = DateTimeOffset.MinValue;
        private int _consecutiveFailures;
        private int _uploadInFlight;
        private int _warnedAboutRejectedUrl;

        public RemoteViewUploader(
            IPulseOrchestrator pulseOrchestrator,
            IEnumerable<ISignalSource> sources,
            IAccountSourceRegistry accountSources,
            AppSettings settings)
        {
            _staticSources = sources;
            _accountSources = accountSources;
            _settings = settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _pulseSubscription = pulseOrchestrator.PulseStream.Subscribe(this);
        }

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
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                if (now - _lastSuccessfulUpload < MinimumUploadInterval)
                {
                    return;
                }

                var snapshot = Compose(state, now);
                // The write id, never the read id: only this app can PUT.
                var url = $"{uploadUrl.TrimEnd('/')}/u/{_settings.RemoteViewId}";

                // One upload at a time; a slow endpoint must not queue refreshes up.
                if (Interlocked.Exchange(ref _uploadInFlight, 1) == 1)
                {
                    return;
                }

                _ = Task.Run(() => UploadAsync(url, snapshot));
            }
            catch (Exception ex)
            {
                // OnNext must never throw: it would tear down the pulse stream.
                Log.Warning(ex, "Remote view snapshot could not be prepared");
            }
        }

        public void OnError(Exception error)
        {
            Log.Warning(error, "Remote view usage stream failed");
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
        /// override fails the https rule. Warns once per session about a
        /// rejected override so the feature does not just look broken.
        /// </summary>
        private string? ResolveUploadUrl()
        {
            var resolved = _settings.EffectiveRemoteViewUploadUrl;
            var overrideUrl = _settings.RemoteViewUploadUrl;

            if (!string.IsNullOrWhiteSpace(overrideUrl) &&
                !RemoteViewEndpoints.IsAllowed(overrideUrl) &&
                Interlocked.Exchange(ref _warnedAboutRejectedUrl, 1) == 0)
            {
                Log.Warning(
                    "Remote view upload URL is not https and was ignored; using the built-in endpoint instead");
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
                using var response = await _http.DeleteAsync(url).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                Log.Warning(
                    "Remote view delete answered {StatusCode}", (int)response.StatusCode);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Remote view delete failed");
                return false;
            }
        }

        private async Task UploadAsync(string url, RemoteSnapshot snapshot)
        {
            try
            {
                var json = JsonSerializer.Serialize(snapshot, _serializerOptions);
                using var content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var response = await _http.PutAsync(url, content).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                _lastSuccessfulUpload = DateTimeOffset.UtcNow;

                // Only announce recovery when something was actually broken.
                if (Interlocked.Exchange(ref _consecutiveFailures, 0) > 0)
                {
                    Log.Information("Remote view upload recovered");
                }
            }
            catch (Exception ex)
            {
                // Log once per failure streak so an unreachable endpoint does
                // not fill the log with one warning per refresh.
                if (Interlocked.Increment(ref _consecutiveFailures) == 1)
                {
                    Log.Warning(ex, "Remote view upload failed; further failures are silent until it recovers");
                }
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
