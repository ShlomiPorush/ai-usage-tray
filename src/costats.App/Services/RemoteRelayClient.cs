using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using costats.Core.RemoteView;
using Serilog;

namespace costats.App.Services
{
    /// <summary>
    /// How the relay answered one write. <see cref="StatusCode"/> is 0 when no
    /// answer came at all; <see cref="ErrorCode"/> is the relay's short error
    /// code (such as <c>stale_write</c>) when it sent a known one.
    /// </summary>
    public sealed record RelayResponse(int StatusCode, string? ErrorCode, TimeSpan? RetryAfter, string? FailureKind)
    {
        public bool IsSuccess => StatusCode is >= 200 and < 300;
    }

    /// <summary>
    /// Sends signed writes to the remote-view relay. Logs the relay's host
    /// name at most: every write URL ends in the secret write id.
    /// </summary>
    public sealed class RemoteRelayClient
    {
        /// <summary>Header that carries a usage write sequence on deletes.</summary>
        public const string UsageSeqHeader = "X-Usage-Seq";

        private readonly HttpClient _http;
        private readonly Func<string> _signingKey;
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<ILogger> _logger;

        public RemoteRelayClient(HttpClient http, Func<string> signingKey, Func<DateTimeOffset> now, Func<ILogger> logger)
        {
            _http = http;
            _signingKey = signingKey;
            _now = now;
            _logger = logger;
        }

        private ILogger Logger => _logger();

        /// <summary>The relay's host name for logs, without the path that carries the write id.</summary>
        public static string HostOf(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid address)";

        /// <summary>
        /// Sends one write and reports the answer instead of throwing. The
        /// body is never logged: it is the shared data itself.
        /// </summary>
        /// <param name="what">A short label for the log, such as "usage upload".</param>
        public async Task<RelayResponse> SendAsync(
            HttpMethod method,
            string url,
            byte[]? body,
            long? usageSeq,
            string what)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                var bodyText = string.Empty;
                if (body is not null)
                {
                    bodyText = Encoding.UTF8.GetString(body);
                    var content = new ByteArrayContent(body);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    request.Content = content;
                }

                if (usageSeq is { } seq)
                {
                    request.Headers.TryAddWithoutValidation(UsageSeqHeader, seq.ToString(CultureInfo.InvariantCulture));
                }

                Sign(request, bodyText);

                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                var errorCode = status >= 400 ? await ReadErrorCodeAsync(response).ConfigureAwait(false) : null;
                Logger.Debug(
                    "Remote view {What} at {Host}: HTTP {StatusCode}{Error} ({Bytes} bytes)",
                    what,
                    HostOf(url),
                    status,
                    errorCode is null ? string.Empty : " " + errorCode,
                    body?.Length ?? 0);
                return new RelayResponse(status, errorCode, RetryAfterOf(response), null);
            }
            catch (Exception ex)
            {
                // Only the exception's type: its message can quote the URL.
                Logger.Debug("Remote view {What} at {Host} failed: {ErrorKind}", what, HostOf(url), ex.GetType().Name);
                return new RelayResponse(0, null, null, ex.GetType().Name);
            }
        }

        /// <summary>
        /// Adds the remote-view signing headers. The relay never requires them:
        /// they buy a larger share of its per-address write budget, and an older
        /// relay ignores them, so signing can only ever help. Failing to sign is
        /// therefore not a reason to skip the write.
        /// </summary>
        public void Sign(HttpRequestMessage request, string body)
        {
            try
            {
                var timestamp = RemoteViewSignature.Timestamp(_now());
                var signature = RemoteViewSignature.Sign(
                    _signingKey(),
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
                Logger.Warning("Remote view request could not be signed; sending it unsigned: {ErrorKind}", ex.GetType().Name);
            }
        }

        /// <summary>
        /// The relay's <c>error</c> code, accepted only when it looks like one
        /// (a short snake_case word), so nothing else from the body is kept.
        /// </summary>
        private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (text.Length > 4096)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.String &&
                    error.GetString() is { Length: > 0 and <= 40 } code &&
                    code.All(character => character is >= 'a' and <= 'z' or '_'))
                {
                    return code;
                }
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException or IOException)
            {
                // Not a relay error body; the status code says enough.
            }

            return null;
        }

        private static TimeSpan? RetryAfterOf(HttpResponseMessage response) =>
            response.Headers.RetryAfter switch
            {
                { Delta: { } delta } => delta,
                { Date: { } date } => date - DateTimeOffset.UtcNow,
                _ => null
            };
    }
}
