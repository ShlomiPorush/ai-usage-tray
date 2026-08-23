using System.Text.Json;
using System.Text.Json.Nodes;
using costats.Application.Security;
using costats.Application.Settings;

namespace costats.Infrastructure.Settings;

/// <summary>
/// Reads and writes <c>%LOCALAPPDATA%\costats\settings.json</c>.
///
/// Two things are deliberate here. Writes go to a temp file and are then moved
/// over the real one, so a crash mid-save can never leave a truncated or empty
/// settings file (which the next launch would treat as corrupt and silently
/// reset). And the Z.AI API keys never touch the JSON at all: they are stored
/// in Windows Credential Manager and hydrated into the in-memory
/// <see cref="AppSettings"/> on load.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private const string BackupFileName = "settings.bad.json";
    private const string RedactedMarker = "[redacted: moved to Windows Credential Manager]";

    /// <summary>
    /// Property names the Z.AI keys used to be written under, before they moved
    /// into the credential vault. Matched case-insensitively.
    /// </summary>
    private const string LegacyCodingKeyProperty = "zAiCodingApiKey";
    private const string LegacyApiKeyProperty = "zAiApiKey";

    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ICredentialVault? _credentialVault;
    private readonly string _settingsPath;

    /// <param name="credentialVault">
    /// Where the Z.AI keys live. When null the keys are simply not persisted,
    /// which is only useful in tests.
    /// </param>
    /// <param name="basePath">
    /// Root folder to place the <c>costats</c> directory in. Defaults to
    /// LocalApplicationData; tests point it at a temp folder so they never touch
    /// the user's real settings.
    /// </param>
    public JsonSettingsStore(ICredentialVault? credentialVault = null, string? basePath = null)
    {
        _credentialVault = credentialVault;
        var root = string.IsNullOrWhiteSpace(basePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : basePath;
        _settingsPath = Path.Combine(root, "costats", "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var path = _settingsPath;
        if (!File.Exists(path))
        {
            return await FinishLoadAsync(new AppSettings(), null, cancellationToken).ConfigureAwait(false);
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The file is locked by something else; fall back to defaults rather
            // than crashing the app on startup.
            return await FinishLoadAsync(new AppSettings(), null, cancellationToken).ConfigureAwait(false);
        }

        // An empty file is what a crash during the old non-atomic save left
        // behind. Treat it exactly like corrupt JSON: back it up, start clean.
        if (string.IsNullOrWhiteSpace(text))
        {
            BackupCorruptSettings(path);
            return await FinishLoadAsync(new AppSettings(), null, cancellationToken).ConfigureAwait(false);
        }

        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(text, _serializerOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            BackupCorruptSettings(path);
            return await FinishLoadAsync(new AppSettings(), null, cancellationToken).ConfigureAwait(false);
        }

        return await FinishLoadAsync(settings, text, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicallyAsync(settings, cancellationToken).ConfigureAwait(false);
            await PersistSecretsAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task WriteAtomicallyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var path = _settingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, settings, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Runs the vault hydration, the one-time plaintext migration and the
    /// backup redaction that every load path needs.
    /// </summary>
    private async Task<AppSettings> FinishLoadAsync(
        AppSettings settings, string? rawJson, CancellationToken cancellationToken)
    {
        var legacyCodingKey = ReadLegacyKey(rawJson, LegacyCodingKeyProperty);
        var legacyApiKey = ReadLegacyKey(rawJson, LegacyApiKeyProperty);
        var hasLegacyKeys = legacyCodingKey is not null || legacyApiKey is not null;

        await HydrateSecretsAsync(settings, cancellationToken).ConfigureAwait(false);

        // A plaintext key in the file wins over the vault copy: it is the value
        // the user last edited by hand.
        settings.ZAiCodingApiKey = legacyCodingKey ?? settings.ZAiCodingApiKey;
        settings.ZAiApiKey = legacyApiKey ?? settings.ZAiApiKey;

        if (hasLegacyKeys && _credentialVault is not null)
        {
            // Move the secrets into the vault and rewrite the file without them.
            try
            {
                await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Migration retries on the next load; the keys still work in memory.
            }
        }

        RedactBackupSecrets();
        return settings;
    }

    private async Task HydrateSecretsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_credentialVault is null)
        {
            return;
        }

        try
        {
            settings.ZAiCodingApiKey = NullIfBlank(
                await _credentialVault.LoadAsync(CredentialKeys.ZaiCodingApiKey, cancellationToken)
                    .ConfigureAwait(false));
            settings.ZAiApiKey = NullIfBlank(
                await _credentialVault.LoadAsync(CredentialKeys.ZaiApiKey, cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Credential Manager can be unavailable under some policies. The app
            // still runs; Z.AI just reports "not configured".
        }
    }

    private async Task PersistSecretsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_credentialVault is null)
        {
            return;
        }

        await PersistSecretAsync(CredentialKeys.ZaiCodingApiKey, settings.ZAiCodingApiKey, cancellationToken)
            .ConfigureAwait(false);
        await PersistSecretAsync(CredentialKeys.ZaiApiKey, settings.ZAiApiKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PersistSecretAsync(string key, string? secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            await _credentialVault!.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _credentialVault!.SaveAsync(key, secret.Trim(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a legacy plaintext key out of the raw settings JSON.</summary>
    private static string? ReadLegacyKey(string? rawJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals(propertyName) ||
                    string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == JsonValueKind.String
                        ? NullIfBlank(property.Value.GetString())
                        : null;
                }
            }
        }
        catch (JsonException)
        {
            // Unreadable file; nothing to migrate.
        }

        return null;
    }

    private void BackupCorruptSettings(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            File.Copy(path, Path.Combine(directory, BackupFileName), true);
        }
        catch
        {
            // Ignore backup failures.
        }
    }

    /// <summary>
    /// The corrupt-settings backup is kept for diagnostics, but it must not keep
    /// a copy of the API keys. Replaces just those two fields in place and
    /// leaves everything else readable.
    /// </summary>
    private void RedactBackupSecrets()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var backupPath = Path.Combine(directory, BackupFileName);
            if (!File.Exists(backupPath))
            {
                return;
            }

            var text = File.ReadAllText(backupPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (JsonNode.Parse(text) is not JsonObject root)
            {
                return;
            }

            var changed = false;
            foreach (var propertyName in root.Select(pair => pair.Key).ToList())
            {
                if (!string.Equals(propertyName, LegacyCodingKeyProperty, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(propertyName, LegacyApiKeyProperty, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (root[propertyName] is JsonValue value &&
                    value.TryGetValue<string>(out var stored) &&
                    string.Equals(stored, RedactedMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                root[propertyName] = RedactedMarker;
                changed = true;
            }

            if (changed)
            {
                File.WriteAllText(backupPath, root.ToJsonString(_serializerOptions));
            }
        }
        catch
        {
            // Diagnostics only. Never let this break a load.
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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
            // Best effort cleanup.
        }
    }
}
