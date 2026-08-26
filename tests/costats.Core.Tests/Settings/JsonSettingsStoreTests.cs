using System.Text.Json;
using costats.Application.Security;
using costats.Application.Settings;
using costats.Infrastructure.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

/// <summary>
/// Covers the two things that used to bite users: API keys sitting in plaintext
/// in settings.json, and a truncated settings.json after a crash mid-save
/// silently resetting every setting.
/// </summary>
public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _root;

    public JsonSettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "costats-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    private string SettingsPath => Path.Combine(_root, "costats", "settings.json");
    private string BackupPath => Path.Combine(_root, "costats", "settings.bad.json");

    private void WriteSettingsFile(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, content);
    }

    [Fact]
    public void Zai_keys_are_never_serialized()
    {
        var settings = new AppSettings
        {
            ZAiCodingApiKey = "coding-secret-value",
            ZAiApiKey = "standard-secret-value",
            ZAiDisplayName = "GLM"
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("zAiCodingApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zAiApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coding-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("standard-secret-value", json, StringComparison.Ordinal);
        // The non-secret display name still round-trips.
        Assert.Contains("zAiDisplayName", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_writes_valid_json_and_leaves_no_temp_file()
    {
        var vault = new FakeCredentialVault();
        var store = new JsonSettingsStore(vault, _root);

        await store.SaveAsync(new AppSettings { RefreshMinutes = 15, Theme = "dark" }, CancellationToken.None);

        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));

        var json = File.ReadAllText(SettingsPath);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(15, document.RootElement.GetProperty("refreshMinutes").GetInt32());
    }

    [Fact]
    public async Task Save_then_load_round_trips_settings_and_keys()
    {
        var vault = new FakeCredentialVault();
        var store = new JsonSettingsStore(vault, _root);

        await store.SaveAsync(
            new AppSettings
            {
                RefreshMinutes = 3,
                ZAiDisplayName = "GLM 4.6",
                ZAiCodingApiKey = "coding-secret-value",
                ZAiApiKey = "standard-secret-value"
            },
            CancellationToken.None);

        Assert.DoesNotContain("coding-secret-value", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
        Assert.Equal("coding-secret-value", vault.Secrets[CredentialKeys.ZaiCodingApiKey]);
        Assert.Equal("standard-secret-value", vault.Secrets[CredentialKeys.ZaiApiKey]);

        var loaded = await new JsonSettingsStore(vault, _root).LoadAsync(CancellationToken.None);

        Assert.Equal(3, loaded.RefreshMinutes);
        Assert.Equal("GLM 4.6", loaded.ZAiDisplayName);
        Assert.Equal("coding-secret-value", loaded.ZAiCodingApiKey);
        Assert.Equal("standard-secret-value", loaded.ZAiApiKey);
        Assert.True(loaded.HasZaiKey);
    }

    [Fact]
    public async Task Automatic_session_toggles_round_trip_but_default_off()
    {
        var store = new JsonSettingsStore(new FakeCredentialVault(), _root);
        var defaults = new AppSettings();
        Assert.False(defaults.AutoStartClaudeFiveHourWindow);
        Assert.False(defaults.AutoStartCodexFiveHourWindow);
        Assert.False(defaults.AutoStartZaiFiveHourWindow);

        await store.SaveAsync(
            new AppSettings
            {
                AutoStartClaudeFiveHourWindow = true,
                AutoStartCodexFiveHourWindow = true,
                AutoStartZaiFiveHourWindow = true
            },
            CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.True(loaded.AutoStartClaudeFiveHourWindow);
        Assert.True(loaded.AutoStartCodexFiveHourWindow);
        Assert.True(loaded.AutoStartZaiFiveHourWindow);
    }

    [Fact]
    public async Task Desktop_display_preferences_round_trip_but_default_off()
    {
        var store = new JsonSettingsStore(new FakeCredentialVault(), _root);
        var defaults = new AppSettings();
        Assert.False(defaults.ShowRemainingPercentages);
        Assert.True(defaults.ShowWeeklyBeforeSession);
        Assert.False(defaults.ShowFloatingStatusPanel);

        await store.SaveAsync(
            new AppSettings
            {
                ShowRemainingPercentages = true,
                ShowWeeklyBeforeSession = false,
                ShowFloatingStatusPanel = true
            },
            CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.True(loaded.ShowRemainingPercentages);
        Assert.False(loaded.ShowWeeklyBeforeSession);
        Assert.True(loaded.ShowFloatingStatusPanel);
    }

    [Fact]
    public async Task Legacy_main_widget_setting_migrates_to_the_floating_panel()
    {
        var settingsDirectory = Path.Combine(_root, "costats");
        Directory.CreateDirectory(settingsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(settingsDirectory, "settings.json"),
            """{"keepWidgetOpen":true}""");

        var store = new JsonSettingsStore(new FakeCredentialVault(), _root);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.ShowFloatingStatusPanel);
    }

    [Fact]
    public void Glm_activation_requires_the_coding_plan_key_specifically()
    {
        var legacyOnly = new AppSettings { ZAiApiKey = "standard-secret-value" };
        Assert.True(legacyOnly.HasZaiKey);
        Assert.False(legacyOnly.HasZaiCodingKey);

        legacyOnly.ZAiCodingApiKey = "coding-secret-value";
        Assert.True(legacyOnly.HasZaiCodingKey);
    }

    [Fact]
    public async Task Clearing_a_key_deletes_the_vault_entry()
    {
        var vault = new FakeCredentialVault();
        var store = new JsonSettingsStore(vault, _root);
        var settings = new AppSettings { ZAiCodingApiKey = "coding-secret-value" };

        await store.SaveAsync(settings, CancellationToken.None);
        Assert.True(vault.Secrets.ContainsKey(CredentialKeys.ZaiCodingApiKey));

        settings.ZAiCodingApiKey = null;
        await store.SaveAsync(settings, CancellationToken.None);

        Assert.False(vault.Secrets.ContainsKey(CredentialKeys.ZaiCodingApiKey));
        Assert.Contains(CredentialKeys.ZaiCodingApiKey, vault.Deleted);
    }

    [Fact]
    public async Task Missing_settings_file_still_hydrates_keys_from_the_vault()
    {
        var vault = new FakeCredentialVault();
        vault.Secrets[CredentialKeys.ZaiCodingApiKey] = "coding-secret-value";

        var loaded = await new JsonSettingsStore(vault, _root).LoadAsync(CancellationToken.None);

        Assert.Equal("coding-secret-value", loaded.ZAiCodingApiKey);
        Assert.Equal(5, loaded.RefreshMinutes);
    }

    [Fact]
    public async Task Load_removes_the_unused_legacy_pulse_snapshot()
    {
        var snapshot = Path.Combine(_root, "costats", "snapshots", "pulse.json");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        File.WriteAllText(snapshot, "{\"email\":\"local@example.com\"}");

        await new JsonSettingsStore(new FakeCredentialVault(), _root)
            .LoadAsync(CancellationToken.None);

        Assert.False(File.Exists(snapshot));
        Assert.False(Directory.Exists(Path.GetDirectoryName(snapshot)));
    }

    [Fact]
    public async Task Empty_settings_file_is_backed_up_and_reset()
    {
        // What a crash during the old truncate-in-place save left behind.
        WriteSettingsFile(string.Empty);
        var vault = new FakeCredentialVault();
        vault.Secrets[CredentialKeys.ZaiApiKey] = "standard-secret-value";

        var loaded = await new JsonSettingsStore(vault, _root).LoadAsync(CancellationToken.None);

        Assert.True(File.Exists(BackupPath));
        Assert.Equal(5, loaded.RefreshMinutes);
        // A settings reset must not lose the secrets.
        Assert.Equal("standard-secret-value", loaded.ZAiApiKey);
    }

    [Fact]
    public async Task Corrupt_settings_file_is_backed_up_and_reset()
    {
        WriteSettingsFile("{ this is not json");

        var loaded = await new JsonSettingsStore(new FakeCredentialVault(), _root)
            .LoadAsync(CancellationToken.None);

        Assert.True(File.Exists(BackupPath));
        Assert.Equal("{ this is not json", File.ReadAllText(BackupPath));
        Assert.Equal(5, loaded.RefreshMinutes);
    }

    [Fact]
    public async Task Plaintext_keys_are_migrated_into_the_vault_and_removed_from_the_file()
    {
        WriteSettingsFile("""
            {
              "refreshMinutes": 10,
              "zAiCodingApiKey": "coding-secret-value",
              "zAiApiKey": "standard-secret-value",
              "zAiDisplayName": "GLM"
            }
            """);
        var vault = new FakeCredentialVault();

        var loaded = await new JsonSettingsStore(vault, _root).LoadAsync(CancellationToken.None);

        Assert.Equal("coding-secret-value", loaded.ZAiCodingApiKey);
        Assert.Equal("standard-secret-value", loaded.ZAiApiKey);
        Assert.Equal("coding-secret-value", vault.Secrets[CredentialKeys.ZaiCodingApiKey]);
        Assert.Equal("standard-secret-value", vault.Secrets[CredentialKeys.ZaiApiKey]);

        var rewritten = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("coding-secret-value", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("standard-secret-value", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("zAiCodingApiKey", rewritten, StringComparison.OrdinalIgnoreCase);
        // Everything else survives the rewrite.
        Assert.Equal(10, loaded.RefreshMinutes);
        Assert.Contains("\"refreshMinutes\": 10", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_accounts_are_migrated_once_and_removed_from_json()
    {
        WriteSettingsFile("""
            {
              "refreshMinutes": 5,
              "claudeConfigDir": "/home/u/.claude-custom",
              "openAiAccounts": [
                { "id": "openai-1", "displayName": "PA", "codexHome": "/home/u/.codex-pa" }
              ]
            }
            """);

        var loaded = await new JsonSettingsStore(new FakeCredentialVault(), _root)
            .LoadAsync(CancellationToken.None);

        var accounts = loaded.GetEffectiveAccounts();
        Assert.Equal(2, accounts.Count);
        Assert.Equal("/home/u/.claude-custom", accounts[0].ConfigDir);
        Assert.Equal("PA", accounts[1].DisplayName);
        Assert.Equal("/home/u/.codex-pa", accounts[1].ConfigDir);

        var rewritten = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("claudeConfigDir", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("openAiAccounts", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"accounts\"", rewritten, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_plaintext_key_wins_over_a_stale_vault_copy()
    {
        WriteSettingsFile("""{ "zAiCodingApiKey": "hand-edited-value" }""");
        var vault = new FakeCredentialVault();
        vault.Secrets[CredentialKeys.ZaiCodingApiKey] = "stale-vault-value";

        var loaded = await new JsonSettingsStore(vault, _root).LoadAsync(CancellationToken.None);

        Assert.Equal("hand-edited-value", loaded.ZAiCodingApiKey);
        Assert.Equal("hand-edited-value", vault.Secrets[CredentialKeys.ZaiCodingApiKey]);
    }

    [Fact]
    public async Task Backup_file_keys_are_redacted_but_the_rest_is_kept()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, """
            {
              "refreshMinutes": 2,
              "zAiCodingApiKey": "coding-secret-value",
              "zAiApiKey": "standard-secret-value"
            }
            """);
        WriteSettingsFile("""{ "refreshMinutes": 5 }""");

        await new JsonSettingsStore(new FakeCredentialVault(), _root).LoadAsync(CancellationToken.None);

        var backup = File.ReadAllText(BackupPath);
        Assert.DoesNotContain("coding-secret-value", backup, StringComparison.Ordinal);
        Assert.DoesNotContain("standard-secret-value", backup, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(backup);
        Assert.Equal(2, document.RootElement.GetProperty("refreshMinutes").GetInt32());
        Assert.Contains("redacted", document.RootElement.GetProperty("zAiCodingApiKey").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_saves_leave_a_readable_file()
    {
        var store = new JsonSettingsStore(new FakeCredentialVault(), _root);
        var settings = new AppSettings { RefreshMinutes = 5 };

        // Without the save gate these overlap on the same file and throw a
        // sharing violation, or leave a partially written file behind.
        await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => store.SaveAsync(settings, CancellationToken.None))));

        Assert.False(File.Exists(SettingsPath + ".tmp"));
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(5, document.RootElement.GetProperty("refreshMinutes").GetInt32());
    }

    private sealed class FakeCredentialVault : ICredentialVault
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public List<string> Deleted { get; } = [];

        public Task SaveAsync(string key, string secret, CancellationToken cancellationToken)
        {
            Secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> LoadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Secrets.TryGetValue(key, out var secret) ? secret : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Secrets.Remove(key);
            Deleted.Add(key);
            return Task.CompletedTask;
        }
    }
}
