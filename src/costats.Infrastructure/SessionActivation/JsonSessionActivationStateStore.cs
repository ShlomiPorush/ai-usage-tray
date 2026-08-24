using System.Text.Json;
using costats.Application.SessionActivation;

namespace costats.Infrastructure.SessionActivation;

/// <summary>
/// Persists only reset timestamps and attempt counts. Provider credentials and
/// prompt contents never enter this file.
/// </summary>
public sealed class JsonSessionActivationStateStore : ISessionActivationStateStore
{
    private const string FileName = "session-activation-state.json";
    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public JsonSessionActivationStateStore(string? basePath = null)
    {
        var root = string.IsNullOrWhiteSpace(basePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : basePath;
        _path = Path.Combine(root, "costats", FileName);
    }

    public async Task<SessionActivationLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new SessionActivationLoadResult(
                new Dictionary<string, SessionActivationCheckpoint>(StringComparer.OrdinalIgnoreCase),
                IsReliable: true);
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var stored = await JsonSerializer.DeserializeAsync<Dictionary<string, SessionActivationCheckpoint>>(
                    stream,
                    _jsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            var checkpoints = stored is null
                ? new Dictionary<string, SessionActivationCheckpoint>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, SessionActivationCheckpoint>(stored, StringComparer.OrdinalIgnoreCase);
            return new SessionActivationLoadResult(checkpoints, IsReliable: true);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new SessionActivationLoadResult(
                new Dictionary<string, SessionActivationCheckpoint>(StringComparer.OrdinalIgnoreCase),
                IsReliable: false);
        }
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, SessionActivationCheckpoint> checkpoints,
        CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(
                            stream,
                            checkpoints,
                            _jsonOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, _path, overwrite: true);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }
}
