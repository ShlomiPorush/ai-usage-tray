using System.Text.Json.Nodes;
using costats.Application.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

public sealed class SettingsTransferTests
{
    private const string WriteId = "0123456789abcdef0123456789abcdef";
    private const string OtherWriteId = "fedcba9876543210fedcba9876543210";
    private const string SourceProfile = @"C:\Users\shlomi";
    private const string TargetProfile = @"D:\Profiles\Shlomi Porush";

    private static readonly DateTimeOffset ExportedAt = new(2026, 10, 12, 8, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// Every serialized AppSettings property, split into what moves and what
    /// stays. A new property fails this test until someone decides which side
    /// it belongs on, so a future secret cannot slip into exports unnoticed.
    /// </summary>
    [Fact]
    public void Every_serialized_setting_is_deliberately_included_or_excluded()
    {
        string[] included =
        [
            nameof(AppSettings.RefreshMinutes),
            nameof(AppSettings.HotkeyEnabled),
            nameof(AppSettings.Hotkey),
            nameof(AppSettings.StartAtLogin),
            nameof(AppSettings.AutomaticUpdateChecksEnabled),
            nameof(AppSettings.UsageHistoryRetentionDays),
            nameof(AppSettings.CopilotEnabled),
            nameof(AppSettings.ShowOverviewResetTimes),
            nameof(AppSettings.ShowRemainingPercentages),
            nameof(AppSettings.ShowWeeklyBeforeSession),
            nameof(AppSettings.UsageAlertsEnabled),
            nameof(AppSettings.UsageResetAlertsEnabled),
            nameof(AppSettings.UsageAlertRules),
            nameof(AppSettings.ShowFloatingStatusPanel),
            nameof(AppSettings.FloatingPanelPosition),
            nameof(AppSettings.FloatingPanelHiddenProviderIds),
            nameof(AppSettings.AutoStartClaudeFiveHourWindow),
            nameof(AppSettings.AutoStartCodexFiveHourWindow),
            nameof(AppSettings.AutoStartZaiFiveHourWindow),
            nameof(AppSettings.SessionActivationScheduleEnabled),
            nameof(AppSettings.SessionActivationScheduleStartHour),
            nameof(AppSettings.SessionActivationScheduleEndHour),
            nameof(AppSettings.Theme),
            nameof(AppSettings.PrimaryAccountId),
            nameof(AppSettings.RemoteViewEnabled),
            nameof(AppSettings.RemoteViewUploadUrl),
            nameof(AppSettings.RemoteViewPageUrl),
            nameof(AppSettings.ZAiDisplayName),
            nameof(AppSettings.Accounts),
        ];
        string[] excluded =
        [
            nameof(AppSettings.RemoteViewId),
            nameof(AppSettings.OnboardingState),
            nameof(AppSettings.FloatingPanelWidth),
            nameof(AppSettings.FloatingPanelHeight),
            nameof(AppSettings.LegacyKeepWidgetOpen),
            nameof(AppSettings.DebugLoggingEnabled),
            nameof(AppSettings.DebugLoggingEnabledAt),
        ];

        Assert.Equal(
            included.Order(StringComparer.Ordinal),
            SettingsTransfer.TransferredProperties.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            excluded.Order(StringComparer.Ordinal),
            SettingsTransfer.ExcludedProperties.Order(StringComparer.Ordinal));

        // Get-only properties are serialized too unless ignored, so none may
        // exist outside the two lists either.
        var unaccounted = typeof(AppSettings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), inherit: true)
                .Cast<System.Text.Json.Serialization.JsonIgnoreAttribute>()
                .All(a => a.Condition != System.Text.Json.Serialization.JsonIgnoreCondition.Always))
            .Select(p => p.Name)
            .Where(name => !included.Contains(name) && !excluded.Contains(name))
            .ToArray();
        Assert.Empty(unaccounted);
    }

    [Fact]
    public void Export_never_contains_secrets_or_the_remote_view_write_id()
    {
        var settings = SourceSettings();
        settings.ZAiCodingApiKey = "zai-coding-secret";
        settings.ZAiApiKey = "zai-payg-secret";
        settings.DefaultRemoteViewSigningKey = "shipped-signing-key";
        settings.DefaultRemoteViewUploadUrl = "https://shipped-relay.example.net";
        settings.DefaultRemoteViewPageUrl = "https://shipped-viewer.example.net";

        var json = Export(settings);

        Assert.DoesNotContain("zai-coding-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("zai-payg-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("shipped-signing-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("shipped-relay.example.net", json, StringComparison.Ordinal);
        Assert.DoesNotContain("shipped-viewer.example.net", json, StringComparison.Ordinal);
        Assert.DoesNotContain(WriteId, json, StringComparison.Ordinal);

        var payload = (JsonObject)JsonNode.Parse(json)!["settings"]!;
        Assert.False(payload.ContainsKey("remoteViewId"));
        Assert.False(payload.ContainsKey("onboardingState"));
        Assert.False(payload.ContainsKey("floatingPanelWidth"));
        Assert.False(payload.ContainsKey("floatingPanelHeight"));
        Assert.False(payload.ContainsKey("keepWidgetOpen"));
        Assert.False(payload.ContainsKey("zAiCodingApiKey"));
        Assert.False(payload.ContainsKey("zAiApiKey"));
        Assert.True(payload.ContainsKey("accounts"));
        Assert.True(payload.ContainsKey("remoteViewEnabled"));
    }

    [Fact]
    public void Debug_logging_stays_on_the_machine_it_was_turned_on()
    {
        var source = SourceSettings();
        source.DebugLoggingEnabled = true;
        source.DebugLoggingEnabledAt = ExportedAt;
        var target = new AppSettings();

        var json = Export(source);
        SettingsTransfer.Apply(target, Parse(json), TargetProfile, _ => true);

        var payload = (JsonObject)JsonNode.Parse(json)!["settings"]!;
        Assert.False(payload.ContainsKey("debugLoggingEnabled"));
        Assert.False(payload.ContainsKey("debugLoggingEnabledAt"));
        Assert.False(target.DebugLoggingEnabled);
        Assert.Null(target.DebugLoggingEnabledAt);
    }

    [Fact]
    public void Export_writes_the_envelope()
    {
        var root = JsonNode.Parse(Export(SourceSettings()))!.AsObject();

        Assert.Equal("ai-usage-tray-settings", root["format"]!.GetValue<string>());
        Assert.Equal(1, root["schema"]!.GetValue<int>());
        Assert.Equal("2.18.0", root["appVersion"]!.GetValue<string>());
        Assert.Equal(SourceProfile, root["sourceUserProfile"]!.GetValue<string>());
        Assert.Equal(ExportedAt, DateTimeOffset.Parse(root["exportedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsType<JsonObject>(root["settings"]);
    }

    [Fact]
    public void Import_preserves_target_secrets_write_id_onboarding_and_panel_size()
    {
        var target = new AppSettings
        {
            ZAiCodingApiKey = "target-coding-key",
            ZAiApiKey = "target-payg-key",
            RemoteViewId = OtherWriteId,
            OnboardingState = OnboardingStates.Completed,
            FloatingPanelWidth = 640,
            FloatingPanelHeight = 220,
            DefaultRemoteViewUploadUrl = "https://relay.example.com",
            DefaultRemoteViewSigningKey = "target-signing-key",
            AutoStartZaiFiveHourWindow = false
        };
        var source = SourceSettings();
        source.AutoStartZaiFiveHourWindow = true;

        SettingsTransfer.Apply(target, Parse(Export(source)), TargetProfile, _ => true);

        Assert.Equal("target-coding-key", target.ZAiCodingApiKey);
        Assert.Equal("target-payg-key", target.ZAiApiKey);
        Assert.Equal(OtherWriteId, target.RemoteViewId);
        Assert.Equal(OnboardingStates.Completed, target.OnboardingState);
        Assert.Equal(640, target.FloatingPanelWidth);
        Assert.Equal(220, target.FloatingPanelHeight);
        Assert.Equal("https://relay.example.com", target.DefaultRemoteViewUploadUrl);
        Assert.Equal("target-signing-key", target.DefaultRemoteViewSigningKey);

        // The target has a coding key, so the imported GLM choice stands.
        Assert.True(target.AutoStartZaiFiveHourWindow);
        Assert.True(target.RemoteViewEnabled);
    }

    [Fact]
    public void Import_keeps_a_missing_write_id_missing_so_the_app_mints_its_own()
    {
        var target = new AppSettings();

        SettingsTransfer.Apply(target, Parse(Export(SourceSettings())), TargetProfile, _ => true);

        Assert.Null(target.RemoteViewId);
        Assert.True(target.RemoteViewEnabled);
    }

    [Fact]
    public void Import_turns_off_glm_auto_start_without_a_coding_key()
    {
        var source = SourceSettings();
        source.AutoStartZaiFiveHourWindow = true;
        var target = new AppSettings();

        SettingsTransfer.Apply(target, Parse(Export(source)), TargetProfile, _ => true);

        Assert.False(target.AutoStartZaiFiveHourWindow);
    }

    [Fact]
    public void Import_remaps_account_folders_and_reports_missing_ones()
    {
        var target = new AppSettings();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TargetProfile + @"\.claude"
        };

        var result = SettingsTransfer.Apply(target, Parse(Export(SourceSettings())), TargetProfile, existing.Contains);

        var accounts = target.Accounts!;
        Assert.Equal(TargetProfile + @"\.claude", accounts[0].ConfigDir);
        Assert.Equal(TargetProfile + @"\.codex-work", accounts[1].ConfigDir);
        Assert.Equal(@"E:\shared\codex", accounts[2].ConfigDir);
        Assert.True(accounts[1].KeepSessionActive);

        Assert.Equal(
            ["codex-1", "codex-2"],
            result.MissingAccountFolders.Select(a => a.Id));
    }

    [Theory]
    [InlineData(@"C:\Users\shlomi\.claude", @"D:\Profiles\Shlomi Porush\.claude")]
    [InlineData(@"c:\users\SHLOMI\.codex\work", @"D:\Profiles\Shlomi Porush\.codex\work")]
    [InlineData(@"C:\Users\shlomi", @"D:\Profiles\Shlomi Porush")]
    [InlineData(@"C:\Users\shlomi/.claude", @"D:\Profiles\Shlomi Porush/.claude")]
    [InlineData(@"C:\Users\shlomi2\.claude", @"C:\Users\shlomi2\.claude")]
    [InlineData(@"C:\Users\shlomi-old\.codex", @"C:\Users\shlomi-old\.codex")]
    [InlineData(@"E:\accounts\.claude", @"E:\accounts\.claude")]
    [InlineData(@"C:\Users\other\.claude", @"C:\Users\other\.claude")]
    public void Remap_moves_only_paths_inside_the_source_profile(string path, string expected)
    {
        Assert.Equal(expected, SettingsTransfer.RemapProfilePath(path, SourceProfile, TargetProfile));
    }

    [Fact]
    public void Remap_ignores_trailing_separators_on_either_profile()
    {
        Assert.Equal(
            @"D:\Profiles\Shlomi Porush\.claude",
            SettingsTransfer.RemapProfilePath(@"C:\Users\shlomi\.claude", @"C:\Users\shlomi\", TargetProfile + @"\"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Remap_leaves_paths_alone_without_a_source_profile(string? sourceProfile)
    {
        Assert.Equal(
            @"C:\Users\shlomi\.claude",
            SettingsTransfer.RemapProfilePath(@"C:\Users\shlomi\.claude", sourceProfile, TargetProfile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"format\":\"something-else\",\"schema\":1,\"settings\":{}}")]
    [InlineData("{\"schema\":1,\"settings\":{}}")]
    [InlineData("{\"refreshMinutes\":5}")]
    public void Parse_rejects_files_that_are_not_settings_exports(string json)
    {
        var result = SettingsTransfer.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Equal(SettingsImportError.NotSettingsFile, result.Error);
    }

    [Fact]
    public void Parse_rejects_a_newer_schema()
    {
        var result = SettingsTransfer.Parse("{\"format\":\"ai-usage-tray-settings\",\"schema\":2,\"settings\":{}}");

        Assert.False(result.IsSuccess);
        Assert.Equal(SettingsImportError.NewerSchema, result.Error);
    }

    [Theory]
    [InlineData("{\"format\":\"ai-usage-tray-settings\",\"schema\":0,\"settings\":{}}")]
    [InlineData("{\"format\":\"ai-usage-tray-settings\",\"schema\":\"1\",\"settings\":{}}")]
    [InlineData("{\"format\":\"ai-usage-tray-settings\",\"settings\":{}}")]
    [InlineData("{\"format\":\"ai-usage-tray-settings\",\"schema\":1}")]
    [InlineData("{\"format\":\"ai-usage-tray-settings\",\"schema\":1,\"settings\":[]}")]
    [InlineData("{\"format\":\"ai-usage-tray-settings\",\"schema\":1,\"settings\":{\"refreshMinutes\":\"often\"}}")]
    public void Parse_rejects_damaged_exports(string json)
    {
        var result = SettingsTransfer.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Equal(SettingsImportError.Damaged, result.Error);
    }

    [Fact]
    public void Round_trip_restores_every_transferred_preference()
    {
        var source = SourceSettings();
        var target = new AppSettings { RemoteViewId = OtherWriteId };

        var parsed = Parse(Export(source));
        SettingsTransfer.Apply(target, parsed, SourceProfile, _ => true);

        Assert.Equal("2.18.0", parsed.AppVersion);
        Assert.Equal(ExportedAt, parsed.ExportedAt);
        Assert.Equal(SourceProfile, parsed.SourceUserProfile);

        Assert.Equal(10, target.RefreshMinutes);
        Assert.False(target.HotkeyEnabled);
        Assert.Equal("Ctrl+Shift+K", target.Hotkey);
        Assert.True(target.StartAtLogin);
        Assert.False(target.AutomaticUpdateChecksEnabled);
        Assert.True(target.CopilotEnabled);
        Assert.True(target.ShowOverviewResetTimes);
        Assert.True(target.ShowRemainingPercentages);
        Assert.False(target.ShowWeeklyBeforeSession);
        Assert.True(target.UsageAlertsEnabled);
        Assert.True(target.UsageResetAlertsEnabled);
        Assert.True(target.IsUsageAlertProviderEnabled("claude:claude-1"));
        Assert.Equal(75, target.GetUsageAlertThreshold("claude:claude-1"));
        Assert.True(target.ShowFloatingStatusPanel);
        Assert.Equal("top-left", target.FloatingPanelPosition);
        Assert.False(target.IsFloatingPanelProviderVisible("codex:codex-1"));
        Assert.True(target.AutoStartClaudeFiveHourWindow);
        Assert.True(target.AutoStartCodexFiveHourWindow);
        Assert.True(target.SessionActivationScheduleEnabled);
        Assert.Equal(7, target.SessionActivationScheduleStartHour);
        Assert.Equal(19, target.SessionActivationScheduleEndHour);
        Assert.Equal("dark", target.Theme);
        Assert.Equal("codex:codex-2", target.PrimaryAccountId);
        Assert.True(target.RemoteViewEnabled);
        Assert.Equal("https://mine.example.com", target.RemoteViewUploadUrl);
        Assert.Equal("https://mine-view.example.com", target.RemoteViewPageUrl);
        Assert.Equal("Work GLM", target.ZAiDisplayName);
        Assert.Equal(OtherWriteId, target.RemoteViewId);

        Assert.Equal(
            source.Accounts!.Select(a => (a.Id, a.Type, a.DisplayName, a.ConfigDir, a.KeepSessionActive)),
            target.Accounts!.Select(a => (a.Id, a.Type, a.DisplayName, a.ConfigDir, a.KeepSessionActive)));
    }

    private static string Export(AppSettings settings) =>
        SettingsTransfer.Export(settings, "2.18.0", ExportedAt, SourceProfile);

    private static SettingsExportFile Parse(string json)
    {
        var result = SettingsTransfer.Parse(json);
        Assert.True(result.IsSuccess, $"Parse failed: {result.Error}");
        return result.File!;
    }

    private static AppSettings SourceSettings()
    {
        var settings = new AppSettings
        {
            OnboardingState = OnboardingStates.Started,
            RefreshMinutes = 10,
            HotkeyEnabled = false,
            Hotkey = "Ctrl+Shift+K",
            StartAtLogin = true,
            AutomaticUpdateChecksEnabled = false,
            CopilotEnabled = true,
            ShowOverviewResetTimes = true,
            ShowRemainingPercentages = true,
            ShowWeeklyBeforeSession = false,
            UsageAlertsEnabled = true,
            UsageResetAlertsEnabled = true,
            ShowFloatingStatusPanel = true,
            FloatingPanelWidth = 900,
            FloatingPanelHeight = 300,
            FloatingPanelPosition = "top-left",
            AutoStartClaudeFiveHourWindow = true,
            AutoStartCodexFiveHourWindow = true,
            SessionActivationScheduleEnabled = true,
            SessionActivationScheduleStartHour = 7,
            SessionActivationScheduleEndHour = 19,
            Theme = "dark",
            PrimaryAccountId = "codex:codex-2",
            RemoteViewEnabled = true,
            RemoteViewId = WriteId,
            RemoteViewUploadUrl = "https://mine.example.com",
            RemoteViewPageUrl = "https://mine-view.example.com",
            ZAiDisplayName = "Work GLM",
            Accounts =
            [
                new MonitoredAccountSettings
                {
                    Id = "claude-1",
                    Type = MonitoredAccountSettings.ClaudeType,
                    DisplayName = "Claude",
                    ConfigDir = SourceProfile + @"\.claude"
                },
                new MonitoredAccountSettings
                {
                    Id = "codex-1",
                    Type = MonitoredAccountSettings.CodexType,
                    DisplayName = "Work",
                    ConfigDir = SourceProfile + @"\.codex-work",
                    KeepSessionActive = true
                },
                new MonitoredAccountSettings
                {
                    Id = "codex-2",
                    Type = MonitoredAccountSettings.CodexType,
                    DisplayName = "Shared",
                    ConfigDir = @"E:\shared\codex"
                }
            ]
        };
        settings.SetUsageAlertRule("claude:claude-1", enabled: true, thresholdPercent: 75);
        settings.SetFloatingPanelProviderVisible("codex:codex-1", visible: false);
        return settings;
    }
}
