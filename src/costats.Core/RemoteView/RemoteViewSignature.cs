using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace costats.Core.RemoteView;

/// <summary>
/// Signs remote-view writes so the relay can tell a real client apart from a
/// scripted flood and give the two different rate-limit budgets.
/// </summary>
/// <remarks>
/// <para>
/// Worth being blunt about what this is not. The relay is an unauthenticated
/// capability service and this repository is public, so
/// <see cref="DefaultSigningKey"/> is public knowledge. A signature made with
/// it identifies nobody. Its only job is to make the request format explicit
/// and to separate well-behaved clients from naive ones for rate limiting; the
/// relay's per-address limit is the actual defence. An operator running a
/// private relay can configure a real key on both sides and gain real
/// separation.
/// </para>
/// <para>
/// The relay never requires the headers, so an older relay that ignores them
/// keeps working unchanged. The canonical string and the authoritative test
/// vector are documented in <c>remote/server/README.md</c> and asserted by
/// <c>remote/server/request-signing.test.mjs</c>.
/// </para>
/// </remarks>
public static class RemoteViewSignature
{
    /// <summary>Canonical-string version prefix. Bump only with a protocol change.</summary>
    public const string Version = "v1";

    /// <summary>Public, non-secret fallback key. See the remarks on this class.</summary>
    public const string DefaultSigningKey = "ai-usage-tray-public-default-key-v1";

    /// <summary>Header carrying the unix-seconds timestamp that was signed.</summary>
    public const string TimestampHeader = "X-Costats-Timestamp";

    /// <summary>Header carrying the lowercase hex HMAC-SHA256 signature.</summary>
    public const string SignatureHeader = "X-Costats-Signature";

    /// <summary>
    /// The exact bytes both sides sign:
    /// <c>"v1\n" + timestamp + "\n" + METHOD + "\n" + path + "\n" + sha256hex(body)</c>.
    /// </summary>
    /// <param name="timestamp">Unix time in whole seconds.</param>
    /// <param name="method">HTTP method; canonicalised to uppercase.</param>
    /// <param name="path">Request path without the query string, for example <c>/u/{writeId}</c>.</param>
    /// <param name="body">The raw request body exactly as it will be sent.</param>
    public static string CanonicalString(long timestamp, string method, string path, string body)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body ?? string.Empty)));
        return string.Join(
            '\n',
            Version,
            timestamp.ToString(CultureInfo.InvariantCulture),
            method.ToUpperInvariant(),
            path,
            digest);
    }

    /// <summary>The lowercase hex HMAC-SHA256 of the canonical string.</summary>
    public static string Sign(string key, long timestamp, string method, string path, string body)
    {
        ArgumentNullException.ThrowIfNull(key);

        var canonical = CanonicalString(timestamp, method, path, body);
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(signature);
    }

    /// <summary>
    /// The signing key to use: a configured value when the build ships one,
    /// otherwise the public default. Blank counts as absent so an empty entry in
    /// <c>appsettings.json</c> does not produce signatures nothing can verify.
    /// </summary>
    public static string ResolveKey(string? configuredKey) =>
        string.IsNullOrWhiteSpace(configuredKey) ? DefaultSigningKey : configuredKey.Trim();

    /// <summary>Unix seconds for <paramref name="moment"/>, the value that goes in the header.</summary>
    public static long Timestamp(DateTimeOffset moment) => moment.ToUnixTimeSeconds();
}
