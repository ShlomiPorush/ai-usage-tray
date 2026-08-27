using System.Text.Json;
using costats.Core.Pulse;
using costats.Core.Remote;
using Xunit;

namespace costats.Core.Tests.Remote;

public sealed class RemoteSnapshotComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static UsagePulse Pulse(
        long? sessionUsed = null,
        long? weekUsed = null,
        DateTimeOffset? sessionResetsAt = null,
        DateTimeOffset? weekResetsAt = null,
        IReadOnlyList<ScopedQuota>? scopedQuotas = null,
        long resetCreditsAvailable = 0,
        DateTimeOffset? resetCreditExpiresAt = null) =>
        new(
            "claude", Now, sessionUsed, 100, weekUsed, 100,
            sessionResetsAt is null ? null : new QuotaWindow(TimeSpan.FromHours(5), sessionResetsAt),
            weekResetsAt is null ? null : new QuotaWindow(TimeSpan.FromDays(7), weekResetsAt))
        {
            ScopedQuotas = scopedQuotas ?? [],
            ResetCreditsAvailable = resetCreditsAvailable,
            ResetCreditExpiresAt = resetCreditExpiresAt
        };

    [Fact]
    public void Compose_maps_session_weekly_and_scoped_windows_in_order()
    {
        var usage = Pulse(
            sessionUsed: 42,
            weekUsed: 61,
            sessionResetsAt: Now.AddHours(2),
            weekResetsAt: Now.AddDays(3),
            scopedQuotas: [new ScopedQuota("Fable", "weekly", 88, Now.AddDays(4), true)]);

        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [new RemoteSnapshotEntry("claude:claude-1", "Claude Work", "Max 20x", usage)],
            Now);

        var account = Assert.Single(snapshot.Accounts);
        Assert.Collection(
            account.Windows,
            window =>
            {
                Assert.Equal("Session", window.Label);
                Assert.Equal(42, window.UsedPercent);
                Assert.Equal(Now.AddHours(2), window.ResetsAt);
            },
            window =>
            {
                Assert.Equal("Weekly", window.Label);
                Assert.Equal(61, window.UsedPercent);
                Assert.Equal(Now.AddDays(3), window.ResetsAt);
            },
            window =>
            {
                // A scoped window keeps the window label and carries the model
                // name separately, so the viewer can render "Weekly · Fable".
                Assert.Equal("Weekly", window.Label);
                Assert.Equal("Fable", window.Scope);
                Assert.Equal(88, window.UsedPercent);
                Assert.Equal(Now.AddDays(4), window.ResetsAt);
            });

        Assert.False(account.Blocked);
        Assert.Equal(2, snapshot.Version);
        Assert.Equal(Now, snapshot.GeneratedAt);
        Assert.Equal("Claude Work", account.Name);
        Assert.Equal("Max 20x", account.Plan);
    }

    [Fact]
    public void Compose_skips_windows_the_provider_did_not_report()
    {
        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [new RemoteSnapshotEntry("codex:codex-1", "Codex", "Plus", Pulse(weekUsed: 12, weekResetsAt: Now.AddDays(5)))],
            Now);

        var window = Assert.Single(Assert.Single(snapshot.Accounts).Windows);
        Assert.Equal("Weekly", window.Label);
        Assert.Equal(12, window.UsedPercent);
    }

    [Fact]
    public void Compose_leaves_reset_times_null_when_no_window_was_reported()
    {
        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [new RemoteSnapshotEntry("claude:claude-1", "Claude", "Max", Pulse(sessionUsed: 5, weekUsed: 7))],
            Now);

        Assert.All(Assert.Single(snapshot.Accounts).Windows, window => Assert.Null(window.ResetsAt));
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(73, 73)]
    [InlineData(140, 100)]
    public void Compose_clamps_used_percentages_to_the_zero_hundred_range(long reported, long expected)
    {
        var usage = Pulse(
            sessionUsed: reported,
            scopedQuotas: [new ScopedQuota("Fable", "weekly", reported, null, true)]);

        var windows = Assert.Single(
            RemoteSnapshotComposer.Compose(
                null,
                [new RemoteSnapshotEntry("claude:claude-1", "Claude", "Max", usage)],
                Now).Accounts).Windows;

        Assert.All(windows, window => Assert.Equal(expected, window.UsedPercent));
    }

    [Theory]
    [InlineData("claude:claude-1", "claude")]
    [InlineData("codex:codex-2", "codex")]
    [InlineData("zai", "zai")]
    [InlineData("copilot", "copilot")]
    public void Compose_extracts_the_provider_from_prefixed_and_bare_ids(string providerId, string expected)
    {
        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [new RemoteSnapshotEntry(providerId, "Account", "Plan", null)],
            Now);

        var account = Assert.Single(snapshot.Accounts);
        Assert.Equal(providerId, account.Id);
        Assert.Equal(expected, account.Provider);
    }

    [Fact]
    public void Compose_includes_accounts_without_usage_with_an_empty_window_list()
    {
        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [new RemoteSnapshotEntry("codex:codex-1", "Codex", string.Empty, null)],
            Now);

        var account = Assert.Single(snapshot.Accounts);
        Assert.Empty(account.Windows);
        Assert.Equal("Codex", account.Name);
    }

    [Fact]
    public void Compose_passes_the_primary_account_through_and_normalizes_blanks_to_null()
    {
        Assert.Equal(
            "claude:claude-1",
            RemoteSnapshotComposer.Compose("claude:claude-1", [], Now).Primary);

        Assert.Null(RemoteSnapshotComposer.Compose(null, [], Now).Primary);
        Assert.Null(RemoteSnapshotComposer.Compose("  ", [], Now).Primary);
    }

    [Fact]
    public void Compose_normalizes_the_timestamp_to_utc()
    {
        var local = new DateTimeOffset(2026, 8, 2, 15, 0, 0, TimeSpan.FromHours(3));

        var snapshot = RemoteSnapshotComposer.Compose(null, [], local);

        Assert.Equal(TimeSpan.Zero, snapshot.GeneratedAt.Offset);
        Assert.Equal(local.UtcDateTime, snapshot.GeneratedAt.UtcDateTime);
    }

    [Theory]
    [InlineData(false, RemoteSnapshotComposer.UsedDisplayMode)]
    [InlineData(true, RemoteSnapshotComposer.RemainingDisplayMode)]
    public void Compose_carries_the_desktop_percentage_preference(
        bool showRemainingPercentages,
        string expectedDisplayMode)
    {
        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [],
            Now,
            showRemainingPercentages);

        Assert.Equal(expectedDisplayMode, snapshot.DisplayMode);
    }

    [Fact]
    public void Serialized_snapshot_uses_the_camel_case_json_contract()
    {
        var usage = Pulse(
            sessionUsed: 42,
            weekUsed: 61,
            sessionResetsAt: Now.AddHours(2),
            weekResetsAt: Now.AddDays(3));

        var snapshot = RemoteSnapshotComposer.Compose(
            "claude:claude-1",
            [new RemoteSnapshotEntry("claude:claude-1", "Claude Work", "Max 20x", usage)],
            Now);

        var json = JsonSerializer.Serialize(snapshot, WebOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.String, root.GetProperty("generatedAt").ValueKind);
        Assert.Equal("claude:claude-1", root.GetProperty("primary").GetString());
        Assert.Equal("used", root.GetProperty("displayMode").GetString());

        var account = Assert.Single(root.GetProperty("accounts").EnumerateArray().ToList());
        Assert.Equal("claude:claude-1", account.GetProperty("id").GetString());
        Assert.Equal("claude", account.GetProperty("provider").GetString());
        Assert.Equal("Claude Work", account.GetProperty("name").GetString());
        Assert.Equal("Max 20x", account.GetProperty("plan").GetString());

        Assert.False(account.GetProperty("blocked").GetBoolean());

        var window = account.GetProperty("windows").EnumerateArray().First();
        Assert.Equal("Session", window.GetProperty("label").GetString());
        Assert.Equal(42, window.GetProperty("usedPercent").GetInt64());
        Assert.Equal(JsonValueKind.String, window.GetProperty("resetsAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, window.GetProperty("scope").ValueKind);
        Assert.Equal(JsonValueKind.Null, window.GetProperty("severity").ValueKind);
    }

    [Fact]
    public void Compose_publishes_reset_credits_when_the_provider_reports_any()
    {
        var expiresAt = Now.AddDays(28);

        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [
                new RemoteSnapshotEntry(
                    "codex:codex-1",
                    "Codex",
                    "Plus",
                    Pulse(weekUsed: 82, resetCreditsAvailable: 2, resetCreditExpiresAt: expiresAt))
            ],
            Now);

        var credits = Assert.Single(snapshot.Accounts).ResetCredits;
        Assert.NotNull(credits);
        Assert.Equal(2, credits!.Available);
        Assert.Equal(expiresAt, credits.ExpiresAt);
    }

    [Fact]
    public void Compose_omits_reset_credits_when_there_is_nothing_to_redeem()
    {
        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [
                new RemoteSnapshotEntry("codex:codex-1", "Codex", "Plus", Pulse(weekUsed: 12)),
                new RemoteSnapshotEntry("zai", "GLM", "Coding", null)
            ],
            Now);

        Assert.All(snapshot.Accounts, account => Assert.Null(account.ResetCredits));
    }

    [Fact]
    public void Serialized_snapshot_keeps_reset_credits_out_of_the_json_unless_present()
    {
        var expiresAt = Now.AddDays(28);

        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [
                new RemoteSnapshotEntry(
                    "codex:codex-1",
                    "Codex",
                    "Plus",
                    Pulse(weekUsed: 82, resetCreditsAvailable: 1, resetCreditExpiresAt: expiresAt)),
                new RemoteSnapshotEntry("claude:claude-1", "Claude", "Max", Pulse(weekUsed: 10))
            ],
            Now);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, WebOptions));
        var accounts = document.RootElement.GetProperty("accounts").EnumerateArray().ToList();

        var credits = accounts[0].GetProperty("resetCredits");
        Assert.Equal(1, credits.GetProperty("available").GetInt64());
        Assert.Equal(expiresAt, credits.GetProperty("expiresAt").GetDateTimeOffset());

        // Absent, not null: an account with nothing to redeem carries no key.
        Assert.False(accounts[1].TryGetProperty("resetCredits", out _));
    }

    [Fact]
    public void Serialized_snapshot_writes_null_for_a_missing_primary_and_reset_time()
    {
        var snapshot = RemoteSnapshotComposer.Compose(
            null,
            [new RemoteSnapshotEntry("zai", "GLM", "Coding", Pulse(weekUsed: 10))],
            Now);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, WebOptions));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("primary").ValueKind);

        var window = root.GetProperty("accounts").EnumerateArray().First()
            .GetProperty("windows").EnumerateArray().First();
        Assert.Equal(JsonValueKind.Null, window.GetProperty("resetsAt").ValueKind);
    }
}
