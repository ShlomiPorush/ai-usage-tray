using System.Text;
using costats.Application.Pulse;
using costats.Application.Storage;

namespace costats.Infrastructure.Pulse;

/// <summary>
/// The startup usage cache file, <c>last-usage.json</c> in the data folder.
/// Each write goes to its own temporary file first and then replaces the
/// cache, so two tray instances never share a temporary file and a reader
/// never sees half a file.
/// </summary>
public sealed class JsonUsageSnapshotStore : IUsageSnapshotStore
{
    public const string FileName = "last-usage.json";

    private readonly string _path;

    public JsonUsageSnapshotStore(string? basePath = null)
    {
        _path = Path.Combine(AppDataPaths.ResolveRoot(basePath), FileName);
    }

    public string? Read()
    {
        try
        {
            return File.ReadAllText(_path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public async Task WriteAsync(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the temporary file is only left behind if it cannot be removed.
            }
        }
    }

    public Task DeleteAsync()
    {
        File.Delete(_path);
        return Task.CompletedTask;
    }
}
