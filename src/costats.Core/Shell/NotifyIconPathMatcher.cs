namespace costats.Core.Shell;

/// <summary>
/// Decides whether an <c>ExecutablePath</c> value that Explorer stored under
/// <c>HKCU\Control Panel\NotifyIconSettings\&lt;id&gt;</c> belongs to this process.
/// </summary>
/// <remarks>
/// Explorer writes either a plain path or a path whose first segment is a
/// known-folder GUID, for example
/// <c>{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\AIUsageTray\app\AIUsageTray.exe</c>
/// for LocalAppData. The GUID is resolved through the injected resolver; when
/// it cannot be resolved the rest of the stored path must match the tail of
/// the process path on a folder boundary.
/// </remarks>
public static class NotifyIconPathMatcher
{
    public static bool Matches(string? storedPath, string? processPath, Func<Guid, string?> resolveKnownFolder)
    {
        ArgumentNullException.ThrowIfNull(resolveKnownFolder);
        if (string.IsNullOrWhiteSpace(storedPath) || string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var stored = Normalize(storedPath);
        var process = Normalize(processPath);

        if (!TrySplitKnownFolder(stored, out var folderId, out var remainder))
        {
            return string.Equals(stored, process, StringComparison.OrdinalIgnoreCase);
        }

        if (remainder.Length == 0)
        {
            return false;
        }

        string? folder;
        try
        {
            folder = resolveKnownFolder(folderId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            folder = null;
        }

        if (!string.IsNullOrWhiteSpace(folder))
        {
            var resolved = Normalize(folder).TrimEnd('\\') + "\\" + remainder;
            return string.Equals(resolved, process, StringComparison.OrdinalIgnoreCase);
        }

        // Unknown folder: accept the same relative path at the end of ours,
        // starting on a folder boundary so "xAIUsageTray.exe" never matches.
        return process.EndsWith("\\" + remainder, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySplitKnownFolder(string stored, out Guid folderId, out string remainder)
    {
        folderId = Guid.Empty;
        remainder = string.Empty;
        if (!stored.StartsWith('{'))
        {
            return false;
        }

        var close = stored.IndexOf('}');
        if (close < 0 || !Guid.TryParse(stored.AsSpan(0, close + 1), out folderId))
        {
            return false;
        }

        remainder = stored[(close + 1)..].TrimStart('\\');
        return true;
    }

    private static string Normalize(string path) => path.Trim().Trim('"').Replace('/', '\\');
}
