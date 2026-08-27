namespace costats.Core.RemoteView;

/// <summary>
/// Guards the remote-view endpoint URLs. The snapshot and the write id travel
/// over these, so a plaintext endpoint would hand both to anyone on the path.
/// </summary>
public static class RemoteViewEndpoints
{
    /// <summary>
    /// True for an absolute https URL, or for plain http on a loopback host
    /// (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) so a self-hoster can
    /// test a remote-view server running on their own machine.
    /// </summary>
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && uri.IsLoopback;
    }

    /// <summary>
    /// The trimmed URL when it passes <see cref="IsAllowed"/>, otherwise null.
    /// Callers treat null as "not configured" and fall back to the next source.
    /// </summary>
    public static string? Normalize(string? url) =>
        IsAllowed(url) ? url!.Trim() : null;
}
