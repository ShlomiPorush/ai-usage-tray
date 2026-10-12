using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace costats.Application.Settings;

/// <summary>
/// Builds and reads the settings file used to move accounts and preferences
/// to another computer. Pure logic: no file, registry or vault access, so the
/// caller owns IO and the folder-exists check is injected.
///
/// The export carries no secrets. Everything below is left out on purpose and
/// is preserved on the target machine during import:
/// <list type="bullet">
/// <item><see cref="AppSettings.RemoteViewId"/>: the secret write id of this
/// machine's remote view.</item>
/// <item><see cref="AppSettings.OnboardingState"/>: first-run progress belongs
/// to the installation, not to the person.</item>
/// <item><see cref="AppSettings.FloatingPanelWidth"/> and
/// <see cref="AppSettings.FloatingPanelHeight"/>: window geometry for this
/// machine's screens, read only when the panel window is created.</item>
/// <item>Every <see cref="JsonIgnoreAttribute"/> property, which is never
/// serialized at all: the Z.AI keys (Credential Manager), the shipped
/// remote-view defaults from <c>appsettings.json</c> and runtime-only values.
/// The Copilot token never lives in <see cref="AppSettings"/>.</item>
/// <item><see cref="AppSettings.LegacyKeepWidgetOpen"/>: a read-only migration
/// shim that always serializes as absent.</item>
/// </list>
/// State files next to <c>settings.json</c> (reset state, activation state,
/// caches, snapshots) are never part of the export.
/// </summary>
public static class SettingsTransfer
{
    public const string Format = "ai-usage-tray-settings";
    public const int CurrentSchema = 1;
    public const string DefaultFileName = "ai-usage-tray-settings.json";

    /// <summary>
    /// Properties left out of the export and kept from the target on import.
    /// Names are C# property names; see the class summary for the reasons.
    /// </summary>
    public static IReadOnlyCollection<string> ExcludedProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AppSettings.RemoteViewId),
        nameof(AppSettings.OnboardingState),
        nameof(AppSettings.FloatingPanelWidth),
        nameof(AppSettings.FloatingPanelHeight),
        nameof(AppSettings.LegacyKeepWidgetOpen),
    };

    /// <summary>
    /// Every <see cref="AppSettings"/> property that the export carries and the
    /// import replaces: public, read-write, serialized and not excluded.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> TransferredProperties { get; } =
        typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property is { CanRead: true, CanWrite: true })
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property => !IsJsonIgnored(property))
            .Where(property => !ExcludedProperties.Contains(property.Name))
            .ToArray();

    /// <summary>Serializes the transferable subset of <paramref name="settings"/> into the envelope.</summary>
    public static string Export(
        AppSettings settings,
        string appVersion,
        DateTimeOffset exportedAt,
        string sourceUserProfile)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var options = SettingsJson.SerializerOptions;
        var payload = JsonSerializer.SerializeToNode(settings, options) as JsonObject ?? new JsonObject();
        foreach (var excluded in ExcludedProperties)
        {
            payload.Remove(JsonNameOf(excluded, options));
        }

        var envelope = new JsonObject
        {
            ["format"] = Format,
            ["schema"] = CurrentSchema,
            ["appVersion"] = appVersion,
            ["exportedAt"] = exportedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["sourceUserProfile"] = sourceUserProfile,
            ["settings"] = payload
        };

        return envelope.ToJsonString(options);
    }

    /// <summary>Validates an export file's text and reads its settings.</summary>
    public static SettingsImportParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return SettingsImportParseResult.Failed(SettingsImportError.NotSettingsFile);
        }

        JsonObject root;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                return SettingsImportParseResult.Failed(SettingsImportError.NotSettingsFile);
            }

            root = parsed;
        }
        catch (JsonException)
        {
            return SettingsImportParseResult.Failed(SettingsImportError.NotSettingsFile);
        }

        if (!TryGetString(root, "format", out var format) ||
            !string.Equals(format, Format, StringComparison.Ordinal))
        {
            return SettingsImportParseResult.Failed(SettingsImportError.NotSettingsFile);
        }

        if (!TryGetInt(root, "schema", out var schema) || schema < 1)
        {
            return SettingsImportParseResult.Failed(SettingsImportError.Damaged);
        }

        if (schema > CurrentSchema)
        {
            return SettingsImportParseResult.Failed(SettingsImportError.NewerSchema);
        }

        if (root["settings"] is not JsonObject settingsNode)
        {
            return SettingsImportParseResult.Failed(SettingsImportError.Damaged);
        }

        AppSettings settings;
        try
        {
            settings = settingsNode.Deserialize<AppSettings>(SettingsJson.SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return SettingsImportParseResult.Failed(SettingsImportError.Damaged);
        }

        TryGetString(root, "sourceUserProfile", out var sourceProfile);
        TryGetString(root, "appVersion", out var appVersion);
        DateTimeOffset? exportedAt = TryGetString(root, "exportedAt", out var exportedText) &&
                                     DateTimeOffset.TryParse(
                                         exportedText,
                                         System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.RoundtripKind,
                                         out var parsedAt)
            ? parsedAt
            : null;

        return new SettingsImportParseResult(
            SettingsImportError.None,
            new SettingsExportFile(schema, appVersion, exportedAt, sourceProfile, settings));
    }

    /// <summary>
    /// Replaces the transferable preferences on <paramref name="target"/> with
    /// the imported ones, in place, so every holder of the shared settings
    /// instance sees them. Excluded and never-serialized values on the target
    /// (secrets, the remote-view write id, onboarding, panel geometry) are left
    /// untouched. Account folders under the source profile are moved under
    /// <paramref name="targetUserProfile"/>; accounts whose folder still does
    /// not exist are imported anyway and listed in the result.
    /// </summary>
    public static SettingsImportResult Apply(
        AppSettings target,
        SettingsExportFile import,
        string targetUserProfile,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(directoryExists);

        var source = import.Settings;
        foreach (var property in TransferredProperties)
        {
            property.SetValue(target, property.GetValue(source));
        }

        var missing = new List<MonitoredAccountSettings>();
        if (target.Accounts is not null)
        {
            target.Accounts = target.Accounts
                .Where(account => account is not null)
                .Select(account => new MonitoredAccountSettings
                {
                    Id = account.Id,
                    Type = account.Type,
                    DisplayName = account.DisplayName,
                    ConfigDir = RemapProfilePath(account.ConfigDir, import.SourceUserProfile, targetUserProfile),
                    KeepSessionActive = account.KeepSessionActive
                })
                .ToList();

            foreach (var account in target.Accounts.Where(account => account.IsValid))
            {
                if (!SafeDirectoryExists(directoryExists, account.ConfigDir))
                {
                    missing.Add(account);
                }
            }
        }

        // Same rule as the Settings screen: a GLM auto-start choice never turns
        // on by itself on a machine without a coding-plan key.
        if (!target.HasZaiCodingKey)
        {
            target.AutoStartZaiFiveHourWindow = false;
        }

        return new SettingsImportResult(missing);
    }

    /// <summary>
    /// Moves <paramref name="path"/> from the source user profile to the target
    /// one when it sits inside the source profile. The prefix must end on a
    /// path boundary, so <c>C:\Users\sam2</c> is not inside <c>C:\Users\sam</c>.
    /// Paths elsewhere are returned unchanged.
    /// </summary>
    public static string RemapProfilePath(string path, string? sourceUserProfile, string? targetUserProfile)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(sourceUserProfile) ||
            string.IsNullOrWhiteSpace(targetUserProfile))
        {
            return path;
        }

        var sourceRoot = TrimTrailingSeparators(sourceUserProfile.Trim());
        var targetRoot = TrimTrailingSeparators(targetUserProfile.Trim());
        if (sourceRoot.Length == 0 || targetRoot.Length == 0 ||
            !path.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (path.Length > sourceRoot.Length && !IsSeparator(path[sourceRoot.Length]))
        {
            return path;
        }

        return targetRoot + path[sourceRoot.Length..];
    }

    private static bool SafeDirectoryExists(Func<string, bool> directoryExists, string path)
    {
        try
        {
            return directoryExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string TrimTrailingSeparators(string value)
    {
        var end = value.Length;
        while (end > 0 && IsSeparator(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }

    private static bool IsSeparator(char value) => value is '\\' or '/';

    private static bool IsJsonIgnored(PropertyInfo property) =>
        property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always };

    private static string JsonNameOf(string propertyName, JsonSerializerOptions options)
    {
        var explicitName = typeof(AppSettings).GetProperty(propertyName)?
            .GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
        return explicitName ?? options.PropertyNamingPolicy?.ConvertName(propertyName) ?? propertyName;
    }

    private static bool TryGetString(JsonObject root, string name, out string? value)
    {
        value = null;
        if (root[name] is JsonValue node && node.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        return false;
    }

    private static bool TryGetInt(JsonObject root, string name, out int value)
    {
        value = 0;
        return root[name] is JsonValue node && node.TryGetValue(out value);
    }
}

/// <summary>Why an export file could not be imported.</summary>
public enum SettingsImportError
{
    None,

    /// <summary>Not JSON, or not a file this app exported.</summary>
    NotSettingsFile,

    /// <summary>Written by a newer version with a schema this version cannot read.</summary>
    NewerSchema,

    /// <summary>The right kind of file, but its contents are unreadable.</summary>
    Damaged
}

/// <summary>A validated export file.</summary>
public sealed record SettingsExportFile(
    int Schema,
    string? AppVersion,
    DateTimeOffset? ExportedAt,
    string? SourceUserProfile,
    AppSettings Settings);

public sealed record SettingsImportParseResult(SettingsImportError Error, SettingsExportFile? File)
{
    public bool IsSuccess => Error == SettingsImportError.None && File is not null;

    public static SettingsImportParseResult Failed(SettingsImportError error) => new(error, null);
}

/// <summary>Outcome of applying an import: accounts whose folder was not found.</summary>
public sealed record SettingsImportResult(IReadOnlyList<MonitoredAccountSettings> MissingAccountFolders);
