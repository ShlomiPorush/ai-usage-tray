using System.Text.Json;
using costats.Application.Alerts;
using costats.Core.Alerts;

namespace costats.Infrastructure.Alerts;

/// <summary>Atomic local persistence for reset-alert baselines.</summary>
public sealed class JsonUsageResetStateStore : IUsageResetStateStore
{
    private const string FileName = "usage-reset-state.json";
    private readonly string path;
    private readonly object gate = new();
    private readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public JsonUsageResetStateStore(string? basePath = null)
    {
        var root = string.IsNullOrWhiteSpace(basePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : basePath;
        path = Path.Combine(root, "costats", FileName);
    }

    public IReadOnlyList<UsageResetCheckpoint> Load()
    {
        lock (gate)
        {
            if (!File.Exists(path))
            {
                return [];
            }

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<UsageResetCheckpoint>>(json, options) ?? [];
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        }
    }

    public void Save(IReadOnlyCollection<UsageResetCheckpoint> checkpoints)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(checkpoints, options));
                File.Move(temporary, path, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // Best-effort temporary cleanup only.
                }
            }
        }
    }
}
