using System.Globalization;
using System.Text.Json;
using costats.Application.Pulse;
using costats.Core.Pulse;

namespace costats.Infrastructure.Pulse;

public sealed class JsonPulseSnapshotWriter : IPulseSnapshotWriter
{
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// Writes the snapshot through a temp file in the same directory and moves it
    /// into place. Serializing straight into the destination would leave a
    /// truncated pulse.json behind whenever the refresh is cancelled mid-write.
    /// </summary>
    public async Task WriteAsync(PulseState state, CancellationToken cancellationToken)
    {
        var path = GetSnapshotPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + "." + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture) + ".tmp";

        try
        {
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, state, _serializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; the next write overwrites it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string GetSnapshotPath()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "costats", "snapshots", "pulse.json");
    }
}
