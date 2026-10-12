using System.Text.Json;

namespace costats.Application.Settings;

/// <summary>
/// The one JSON shape for <see cref="AppSettings"/>: camelCase web defaults,
/// indented. Shared by the settings store and the settings export so a file
/// exported on one computer reads exactly like <c>settings.json</c> on another.
/// </summary>
public static class SettingsJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
