using costats.Application.Abstractions;
using costats.Application.Pulse;
using costats.Application.SessionActivation;
using costats.Application.Settings;
using costats.Core.Pulse;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace costats.Core.Tests.SessionActivation;

public sealed class SessionAutoStartCoordinatorTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Expired_window_is_started_once_and_not_duplicated()
    {
        var resetAt = Baseline.AddMinutes(-1);
        var harness = Harness.Claude(resetAt, Baseline);

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        var call = Assert.Single(harness.Activator.Calls);
        Assert.Equal("claude:work", call.ProviderId);
        Assert.Equal(SessionActivationProvider.Claude, call.Provider);
        Assert.Equal(@"C:\profiles\claude-work", call.ConfigDirectory);
        Assert.Equal(2, harness.Orchestrator.Refreshes.Count); // preflight + post-success
    }

    [Fact]
    public async Task User_activity_that_creates_a_new_window_wins_the_preflight_race()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Orchestrator.OnRefresh = providerId =>
            harness.Orchestrator.CurrentState = State(providerId, Baseline.AddHours(5));

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Activator.Calls);
        var checkpoint = Assert.Single(harness.Store.Current).Value;
        Assert.Equal(Baseline.AddHours(5), checkpoint.ObservedResetAt);
        Assert.Equal(0, checkpoint.Attempts);
        Assert.False(checkpoint.Completed);
    }

    [Fact]
    public async Task Failed_verification_refresh_never_sends_or_consumes_an_attempt()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Orchestrator.VerificationSucceeded = false;

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Activator.Calls);
        var checkpoint = Assert.Single(harness.Store.Current).Value;
        Assert.Equal(0, checkpoint.Attempts);
        Assert.Equal(Baseline.AddMinutes(5), checkpoint.NextAttemptAt);
    }

    [Fact]
    public async Task Three_retries_are_spaced_five_minutes_apart_after_the_initial_attempt()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Activator.DefaultResult = SessionActivationResult.Failure("expected test failure");

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None); // initial
        Assert.Single(harness.Activator.Calls);

        harness.Clock.UtcNow = Baseline.AddMinutes(4).AddSeconds(59);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);
        Assert.Single(harness.Activator.Calls);

        harness.Clock.UtcNow = Baseline.AddMinutes(5);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None); // retry 1
        harness.Clock.UtcNow = Baseline.AddMinutes(10);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None); // retry 2
        harness.Clock.UtcNow = Baseline.AddMinutes(15);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None); // retry 3
        harness.Clock.UtcNow = Baseline.AddHours(1);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None); // exhausted

        Assert.Equal(4, harness.Activator.Calls.Count);
        var checkpoint = Assert.Single(harness.Store.Current).Value;
        Assert.Equal(SessionAutoStartCoordinator.MaximumAttempts, checkpoint.Attempts);
        Assert.True(checkpoint.Completed);
        Assert.False(checkpoint.Succeeded);
    }

    [Fact]
    public async Task Attempt_is_persisted_before_the_external_prompt_runs()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Activator.OnActivate = _ =>
        {
            var persisted = Assert.Single(harness.Store.Current).Value;
            Assert.Equal(1, persisted.Attempts);
            Assert.Equal(Baseline.AddMinutes(5), persisted.NextAttemptAt);
        };

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Single(harness.Activator.Calls);
    }

    [Fact]
    public async Task Completed_reset_survives_restart_without_another_prompt()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);
        Assert.Single(harness.Activator.Calls);

        var replacementActivator = new FakeActivator();
        var restarted = CreateCoordinator(
            harness.Orchestrator,
            harness.Settings,
            replacementActivator,
            harness.Store,
            harness.Clock);

        await restarted.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(replacementActivator.Calls);
    }

    [Fact]
    public async Task Unreliable_persisted_state_suppresses_a_past_window_until_a_new_one_is_observed()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Store.IsReliable = false;
        var coordinator = CreateCoordinator(
            harness.Orchestrator,
            harness.Settings,
            harness.Activator,
            harness.Store,
            harness.Clock);

        await coordinator.CheckOnceAsync(CancellationToken.None);
        Assert.Empty(harness.Activator.Calls);

        harness.Orchestrator.CurrentState = State("claude:work", Baseline.AddHours(5));
        await coordinator.CheckOnceAsync(CancellationToken.None);
        harness.Clock.UtcNow = Baseline.AddHours(5).AddSeconds(1);
        harness.Orchestrator.CurrentState = State("claude:work", Baseline.AddHours(5));
        await coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Single(harness.Activator.Calls);
    }

    [Fact]
    public async Task Corrupt_state_recovery_is_persisted_independently_for_each_provider()
    {
        var settings = new AppSettings
        {
            AutoStartClaudeFiveHourWindow = true,
            AutoStartCodexFiveHourWindow = true,
            AutoStartZaiFiveHourWindow = true,
            ZAiCodingApiKey = "test-only-key",
            Accounts =
            [
                new MonitoredAccountSettings
                {
                    Id = "work",
                    Type = MonitoredAccountSettings.ClaudeType,
                    DisplayName = "Claude",
                    ConfigDir = @"C:\profiles\claude-work"
                },
                new MonitoredAccountSettings
                {
                    Id = "gpt",
                    Type = MonitoredAccountSettings.CodexType,
                    DisplayName = "GPT",
                    ConfigDir = @"C:\profiles\codex-gpt"
                }
            ]
        };
        var clock = new FakeClock { UtcNow = Baseline };
        var orchestrator = new FakeOrchestrator
        {
            CurrentState = new PulseState(
                new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase)
                {
                    ["claude:work"] = Reading("claude:work", Baseline.AddHours(5), TimeSpan.FromHours(5)),
                    ["codex:gpt"] = Reading("codex:gpt", Baseline.AddMinutes(-1), TimeSpan.FromHours(5)),
                    ["zai"] = Reading("zai", Baseline.AddMinutes(-1), TimeSpan.FromHours(5))
                },
                Baseline,
                [],
                false,
                RefreshTrigger.Scheduled)
        };
        var store = new FakeStateStore { IsReliable = false };
        var activator = new FakeActivator();
        var coordinator = CreateCoordinator(orchestrator, settings, activator, store, clock);

        await coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.False(store.Current["claude:work"].RequiresFutureObservation);
        Assert.True(store.Current["codex:gpt"].RequiresFutureObservation);
        Assert.True(store.Current["zai"].RequiresFutureObservation);
        Assert.Empty(activator.Calls);

        // The recovery barrier survives once the repaired state file is loaded
        // as valid on the next process start.
        store.IsReliable = true;
        var restarted = CreateCoordinator(orchestrator, settings, activator, store, clock);
        await restarted.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(activator.Calls);
        Assert.True(store.Current["codex:gpt"].RequiresFutureObservation);
        Assert.True(store.Current["zai"].RequiresFutureObservation);
    }

    [Fact]
    public async Task Unavailable_provider_never_sends_and_does_not_consume_an_attempt()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Orchestrator.OnRefresh = providerId =>
            harness.Orchestrator.CurrentState = UnavailableState(providerId);

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Activator.Calls);
        var checkpoint = Assert.Single(harness.Store.Current).Value;
        Assert.Equal(0, checkpoint.Attempts);
        Assert.Equal(Baseline.AddMinutes(5), checkpoint.NextAttemptAt);
    }

    [Fact]
    public async Task Codex_accounts_require_their_own_toggle()
    {
        var settings = new AppSettings
        {
            AutoStartClaudeFiveHourWindow = true,
            AutoStartZaiFiveHourWindow = true,
            Accounts =
            [
                new MonitoredAccountSettings
                {
                    Id = "gpt",
                    Type = MonitoredAccountSettings.CodexType,
                    DisplayName = "GPT",
                    ConfigDir = @"C:\profiles\codex"
                }
            ]
        };
        var clock = new FakeClock { UtcNow = Baseline };
        var orchestrator = new FakeOrchestrator
        {
            CurrentState = State("codex:gpt", Baseline.AddMinutes(-1))
        };
        var activator = new FakeActivator();
        var coordinator = CreateCoordinator(
            orchestrator,
            settings,
            activator,
            new FakeStateStore(),
            clock);

        await coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(activator.Calls);
        Assert.Empty(orchestrator.Refreshes);
    }

    [Fact]
    public async Task A_non_five_hour_session_window_is_not_eligible()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Orchestrator.CurrentState = State(
            "claude:work",
            Baseline.AddMinutes(-1),
            TimeSpan.FromHours(1));

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Activator.Calls);
        Assert.Empty(harness.Orchestrator.Refreshes);
    }

    [Fact]
    public async Task Scheduled_activation_waits_until_the_daily_start_hour()
    {
        var now = new DateTimeOffset(2026, 8, 24, 5, 59, 0, TimeSpan.Zero);
        var harness = Harness.Claude(now.AddHours(-1), now);
        harness.Settings.SessionActivationScheduleEnabled = true;
        harness.Settings.SessionActivationScheduleStartHour = 6;
        harness.Settings.SessionActivationScheduleEndHour = 18;

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);
        Assert.Empty(harness.Activator.Calls);
        Assert.Empty(harness.Orchestrator.Refreshes);

        harness.Clock.UtcNow = new DateTimeOffset(2026, 8, 24, 6, 0, 0, TimeSpan.Zero);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Single(harness.Activator.Calls);
    }

    [Fact]
    public async Task Scheduled_activation_does_not_start_at_the_daily_end_hour()
    {
        var now = new DateTimeOffset(2026, 8, 24, 18, 0, 0, TimeSpan.Zero);
        var harness = Harness.Claude(now.AddHours(-1), now);
        harness.Settings.SessionActivationScheduleEnabled = true;
        harness.Settings.SessionActivationScheduleStartHour = 6;
        harness.Settings.SessionActivationScheduleEndHour = 18;

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Activator.Calls);
        Assert.Empty(harness.Orchestrator.Refreshes);
    }

    [Fact]
    public async Task Scheduled_activation_catches_up_when_the_app_resumes_inside_the_window()
    {
        var now = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
        var harness = Harness.Claude(now.AddHours(-9), now);
        harness.Settings.SessionActivationScheduleEnabled = true;
        harness.Settings.SessionActivationScheduleStartHour = 6;
        harness.Settings.SessionActivationScheduleEndHour = 18;

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Single(harness.Activator.Calls);
    }

    [Fact]
    public async Task User_activity_still_wins_the_preflight_at_the_scheduled_start()
    {
        var now = new DateTimeOffset(2026, 8, 24, 6, 0, 0, TimeSpan.Zero);
        var harness = Harness.Claude(now.AddHours(-1), now);
        harness.Settings.SessionActivationScheduleEnabled = true;
        harness.Settings.SessionActivationScheduleStartHour = 6;
        harness.Settings.SessionActivationScheduleEndHour = 18;
        harness.Orchestrator.OnRefresh = providerId =>
            harness.Orchestrator.CurrentState = State(providerId, now.AddHours(5));

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Activator.Calls);
        Assert.Single(harness.Orchestrator.Refreshes);
    }

    [Fact]
    public async Task Glm_uses_its_own_toggle_and_target()
    {
        var settings = new AppSettings
        {
            AutoStartZaiFiveHourWindow = true,
            ZAiCodingApiKey = "test-only-key"
        };
        var clock = new FakeClock { UtcNow = Baseline };
        var orchestrator = new FakeOrchestrator { CurrentState = State("zai", Baseline.AddSeconds(-1)) };
        var activator = new FakeActivator();
        var coordinator = CreateCoordinator(
            orchestrator,
            settings,
            activator,
            new FakeStateStore(),
            clock);

        await coordinator.CheckOnceAsync(CancellationToken.None);

        var call = Assert.Single(activator.Calls);
        Assert.Equal(SessionActivationProvider.Zai, call.Provider);
        Assert.Null(call.ConfigDirectory);
    }

    [Fact]
    public async Task Glm_legacy_key_alone_is_not_eligible_for_activation()
    {
        var settings = new AppSettings
        {
            AutoStartZaiFiveHourWindow = true,
            ZAiApiKey = "legacy-standard-key"
        };
        var orchestrator = new FakeOrchestrator { CurrentState = State("zai", Baseline.AddSeconds(-1)) };
        var activator = new FakeActivator();
        var coordinator = CreateCoordinator(
            orchestrator,
            settings,
            activator,
            new FakeStateStore(),
            new FakeClock { UtcNow = Baseline });

        await coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(activator.Calls);
        Assert.Empty(orchestrator.Refreshes);
    }

    [Fact]
    public async Task Codex_expired_window_uses_the_matching_account_profile()
    {
        var harness = Harness.Codex(Baseline.AddMinutes(-1), Baseline);

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        var call = Assert.Single(harness.Activator.Calls);
        Assert.Equal("codex:gpt", call.ProviderId);
        Assert.Equal(SessionActivationProvider.Codex, call.Provider);
        Assert.Equal(@"C:\profiles\codex-gpt", call.ConfigDirectory);
    }

    [Fact]
    public async Task Glm_idle_window_without_reset_timestamp_starts_a_new_window()
    {
        var settings = new AppSettings
        {
            AutoStartZaiFiveHourWindow = true,
            ZAiCodingApiKey = "test-only-key"
        };
        var clock = new FakeClock { UtcNow = Baseline };
        var orchestrator = new FakeOrchestrator { CurrentState = State("zai", null) };
        var activator = new FakeActivator();
        var coordinator = CreateCoordinator(
            orchestrator,
            settings,
            activator,
            new FakeStateStore(),
            clock);

        await coordinator.CheckOnceAsync(CancellationToken.None);

        var call = Assert.Single(activator.Calls);
        Assert.Equal(SessionActivationProvider.Zai, call.Provider);
    }

    [Fact]
    public async Task Claude_idle_null_reset_activates_immediately_after_explicit_opt_in()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Orchestrator.CurrentState = State("claude:work", null);

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        var call = Assert.Single(harness.Activator.Calls);
        Assert.Equal(SessionActivationProvider.Claude, call.Provider);
        Assert.True(harness.Store.Current["claude:work"].Succeeded);
    }

    [Fact]
    public async Task Successful_claude_activation_arms_a_visible_deadline_while_the_api_is_delayed()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Orchestrator.CurrentState = State("claude:work", null);

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        var checkpoint = harness.Store.Current["claude:work"];
        Assert.Equal(Baseline.AddHours(5), checkpoint.ObservedResetAt);
        Assert.Equal(Baseline.AddHours(5), checkpoint.NextAttemptAt);
        Assert.Equal(0, checkpoint.Attempts);
        Assert.False(checkpoint.Completed);
        Assert.True(checkpoint.Succeeded);
        Assert.True(harness.WindowRegistry.TryGetActive(
            "claude:work",
            Baseline,
            out var visibleReset));
        Assert.Equal(Baseline.AddHours(5), visibleReset);
    }

    [Fact]
    public async Task Claude_idle_null_reset_stays_closed_when_persisted_state_is_unreliable()
    {
        var harness = Harness.Claude(Baseline.AddMinutes(-1), Baseline);
        harness.Store.IsReliable = false;
        harness.Orchestrator.CurrentState = State("claude:work", null);

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Activator.Calls);
        Assert.True(harness.Store.Current["claude:work"].RequiresFutureObservation);
    }

    [Fact]
    public async Task Claude_idle_null_reset_activates_after_its_persisted_reset_is_due()
    {
        var resetAt = Baseline.AddHours(5);
        var harness = Harness.Claude(resetAt, Baseline);

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);
        Assert.Empty(harness.Activator.Calls);

        harness.Clock.UtcNow = resetAt.AddSeconds(1);
        harness.Orchestrator.CurrentState = State("claude:work", null);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        var call = Assert.Single(harness.Activator.Calls);
        Assert.Equal("claude:work", call.ProviderId);
        Assert.True(harness.Store.Current["claude:work"].Succeeded);
    }

    [Fact]
    public async Task Claude_null_reset_with_nonzero_usage_never_activates()
    {
        var resetAt = Baseline.AddHours(5);
        var harness = Harness.Claude(resetAt, Baseline);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        harness.Clock.UtcNow = resetAt.AddSeconds(1);
        harness.Orchestrator.CurrentState = new PulseState(
            new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude:work"] = Reading(
                    "claude:work",
                    null,
                    TimeSpan.FromHours(5),
                    sessionUsed: 3)
            },
            resetAt.AddSeconds(1),
            [],
            false,
            RefreshTrigger.Scheduled);

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Activator.Calls);
        Assert.Empty(harness.Orchestrator.Refreshes);
    }

    [Fact]
    public async Task Codex_idle_null_reset_activates_immediately_after_explicit_opt_in()
    {
        var harness = Harness.Codex(Baseline.AddHours(5), Baseline);

        harness.Orchestrator.CurrentState = State("codex:gpt", null);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        var call = Assert.Single(harness.Activator.Calls);
        Assert.Equal(SessionActivationProvider.Codex, call.Provider);
        Assert.True(harness.Store.Current["codex:gpt"].Succeeded);
    }

    [Fact]
    public async Task Successful_codex_activation_arms_the_next_stable_deadline()
    {
        var harness = Harness.Codex(Baseline.AddMinutes(-1), Baseline);

        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);
        harness.Orchestrator.CurrentState = State("codex:gpt", null);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        Assert.Single(harness.Activator.Calls);
        var checkpoint = harness.Store.Current["codex:gpt"];
        Assert.Equal(Baseline.AddHours(5), checkpoint.ObservedResetAt);
        Assert.Equal(Baseline.AddHours(5), checkpoint.NextAttemptAt);
        Assert.Equal(0, checkpoint.Attempts);
        Assert.False(checkpoint.Completed);
        Assert.True(checkpoint.Succeeded);
        Assert.True(harness.WindowRegistry.TryGetActive(
            "codex:gpt",
            Baseline,
            out var visibleReset));
        Assert.Equal(Baseline.AddHours(5), visibleReset);
    }

    [Fact]
    public async Task Legacy_codex_rolling_placeholder_migrates_to_immediate_idle_activation()
    {
        var resetAt = Baseline.AddHours(5);
        var harness = Harness.Codex(resetAt, Baseline);
        await harness.Coordinator.CheckOnceAsync(CancellationToken.None);

        harness.Store.Current["codex:gpt"].ObservedActiveWindow = false;
        harness.Clock.UtcNow = resetAt.AddSeconds(1);
        harness.Orchestrator.CurrentState = State("codex:gpt", null);
        var restarted = CreateCoordinator(
            harness.Orchestrator,
            harness.Settings,
            harness.Activator,
            harness.Store,
            harness.Clock);

        await restarted.CheckOnceAsync(CancellationToken.None);

        Assert.Single(harness.Activator.Calls);
    }

    private static SessionAutoStartCoordinator CreateCoordinator(
        FakeOrchestrator orchestrator,
        AppSettings settings,
        FakeActivator activator,
        FakeStateStore store,
        FakeClock clock,
        ISessionActivationWindowRegistry? windowRegistry = null) =>
        new(
            orchestrator,
            settings,
            activator,
            store,
            clock,
            NullLogger<SessionAutoStartCoordinator>.Instance,
            windowRegistry,
            TimeZoneInfo.Utc);

    private static PulseState State(
        string providerId,
        DateTimeOffset? resetAt,
        TimeSpan? duration = null,
        long sessionUsed = 0) =>
        new(
            new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase)
            {
                [providerId] = Reading(providerId, resetAt, duration ?? TimeSpan.FromHours(5), sessionUsed)
            },
            Baseline,
            [],
            false,
            RefreshTrigger.Scheduled);

    private static PulseState UnavailableState(string providerId) =>
        new(
            new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase)
            {
                [providerId] = new ProviderReading(
                    null,
                    null,
                    "Unavailable",
                    Baseline,
                    ReadingConfidence.Low,
                    ReadingSource.Api)
            },
            Baseline,
            [],
            false,
            RefreshTrigger.Silent);

    private static ProviderReading Reading(
        string providerId,
        DateTimeOffset? resetAt,
        TimeSpan duration,
        long sessionUsed = 0) =>
        new(
            new UsagePulse(
                providerId,
                Baseline,
                sessionUsed,
                100,
                0,
                100,
                new QuotaWindow(duration, resetAt),
                new QuotaWindow(TimeSpan.FromDays(7), Baseline.AddDays(4))),
            null,
            "Available",
            Baseline,
            ReadingConfidence.High,
            ReadingSource.Api);

    private sealed class Harness
    {
        private Harness(
            AppSettings settings,
            FakeClock clock,
            FakeOrchestrator orchestrator,
            FakeActivator activator,
            FakeStateStore store)
        {
            Settings = settings;
            Clock = clock;
            Orchestrator = orchestrator;
            Activator = activator;
            Store = store;
            WindowRegistry = new SessionActivationWindowRegistry();
            Coordinator = CreateCoordinator(
                orchestrator,
                settings,
                activator,
                store,
                clock,
                WindowRegistry);
        }

        public AppSettings Settings { get; }
        public FakeClock Clock { get; }
        public FakeOrchestrator Orchestrator { get; }
        public FakeActivator Activator { get; }
        public FakeStateStore Store { get; }
        public SessionActivationWindowRegistry WindowRegistry { get; }
        public SessionAutoStartCoordinator Coordinator { get; }

        public static Harness Claude(DateTimeOffset resetAt, DateTimeOffset now)
        {
            var settings = new AppSettings
            {
                AutoStartClaudeFiveHourWindow = true,
                Accounts =
                [
                    new MonitoredAccountSettings
                    {
                        Id = "work",
                        Type = MonitoredAccountSettings.ClaudeType,
                        DisplayName = "Claude",
                        ConfigDir = @"C:\profiles\claude-work"
                    }
                ]
            };
            var clock = new FakeClock { UtcNow = now };
            var orchestrator = new FakeOrchestrator
            {
                CurrentState = State("claude:work", resetAt)
            };
            return new Harness(
                settings,
                clock,
                orchestrator,
                new FakeActivator(),
                new FakeStateStore());
        }

        public static Harness Codex(DateTimeOffset resetAt, DateTimeOffset now)
        {
            var settings = new AppSettings
            {
                AutoStartCodexFiveHourWindow = true,
                Accounts =
                [
                    new MonitoredAccountSettings
                    {
                        Id = "gpt",
                        Type = MonitoredAccountSettings.CodexType,
                        DisplayName = "GPT",
                        ConfigDir = @"C:\profiles\codex-gpt"
                    }
                ]
            };
            var clock = new FakeClock { UtcNow = now };
            var orchestrator = new FakeOrchestrator
            {
                CurrentState = State("codex:gpt", resetAt, sessionUsed: 1)
            };
            return new Harness(
                settings,
                clock,
                orchestrator,
                new FakeActivator(),
                new FakeStateStore());
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class FakeActivator : ISessionWindowActivator
    {
        public List<SessionActivationTarget> Calls { get; } = [];
        public SessionActivationResult DefaultResult { get; set; } = SessionActivationResult.Success();
        public Action<SessionActivationTarget>? OnActivate { get; set; }

        public Task<SessionActivationResult> ActivateAsync(
            SessionActivationTarget target,
            CancellationToken cancellationToken)
        {
            Calls.Add(target);
            OnActivate?.Invoke(target);
            return Task.FromResult(DefaultResult);
        }
    }

    private sealed class FakeStateStore : ISessionActivationStateStore
    {
        public bool IsReliable { get; set; } = true;

        public Dictionary<string, SessionActivationCheckpoint> Current { get; private set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<SessionActivationLoadResult> LoadAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionActivationLoadResult(Clone(Current), IsReliable));

        public Task SaveAsync(
            IReadOnlyDictionary<string, SessionActivationCheckpoint> checkpoints,
            CancellationToken cancellationToken)
        {
            Current = Clone(checkpoints);
            return Task.CompletedTask;
        }

        private static Dictionary<string, SessionActivationCheckpoint> Clone(
            IReadOnlyDictionary<string, SessionActivationCheckpoint> source) =>
            source.ToDictionary(
                pair => pair.Key,
                pair => new SessionActivationCheckpoint
                {
                    ObservedResetAt = pair.Value.ObservedResetAt,
                    ObservedActiveWindow = pair.Value.ObservedActiveWindow,
                    RequiresFutureObservation = pair.Value.RequiresFutureObservation,
                    Attempts = pair.Value.Attempts,
                    NextAttemptAt = pair.Value.NextAttemptAt,
                    Completed = pair.Value.Completed,
                    Succeeded = pair.Value.Succeeded
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeOrchestrator : IPulseOrchestrator
    {
        public IObservable<PulseState> PulseStream { get; } = new PulseBroadcaster();
        public PulseState? CurrentState { get; set; }
        public List<string> Refreshes { get; } = [];
        public Action<string>? OnRefresh { get; set; }
        public bool VerificationSucceeded { get; set; } = true;

        public Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RefreshIfStaleAsync(TimeSpan maxAge, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken)
        {
            Refreshes.Add(providerId);
            OnRefresh?.Invoke(providerId);
            return Task.CompletedTask;
        }

        public Task<ProviderRefreshResult> RefreshProviderForVerificationAsync(
            string providerId,
            CancellationToken cancellationToken)
        {
            Refreshes.Add(providerId);
            OnRefresh?.Invoke(providerId);
            if (!VerificationSucceeded || CurrentState is null ||
                !CurrentState.Providers.TryGetValue(providerId, out var reading))
            {
                return Task.FromResult(ProviderRefreshResult.Failure());
            }

            return Task.FromResult(ProviderRefreshResult.Success(reading));
        }

        public void UpdateRefreshInterval(TimeSpan interval)
        {
        }

        public bool IsFullRefreshStale(TimeSpan maxAge) => false;

        public void RepublishLastState()
        {
        }
    }
}
