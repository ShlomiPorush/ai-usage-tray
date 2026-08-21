using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Core.Remote;
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

                var uploadUrl = _settings.EffectiveRemoteViewUploadUrl;
                if (!_settings.RemoteViewEnabled ||
                    string.IsNullOrWhiteSpace(_settings.RemoteViewId) ||
                    string.IsNullOrWhiteSpace(uploadUrl))
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                if (now - _lastSuccessfulUpload < MinimumUploadInterval)
                {
                    return;
                }

                var snapshot = Compose(state, now);
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
                    pair.Value.Usage))
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return RemoteSnapshotComposer.Compose(_settings.PrimaryAccountId, entries, generatedAt);
        }

        /// <summary>Same account filter the tray tooltip uses, plus Copilot when it is enabled.</summary>
        private bool IsPublished(string providerId) =>
            providerId.StartsWith("claude:", StringComparison.OrdinalIgnoreCase) ||
            providerId.StartsWith("codex:", StringComparison.OrdinalIgnoreCase) ||
            providerId.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
            (providerId.Equals("zai", StringComparison.OrdinalIgnoreCase) && _settings.HasZaiKey) ||
            (providerId.Equals("copilot", StringComparison.OrdinalIgnoreCase) && _settings.CopilotEnabled);

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
