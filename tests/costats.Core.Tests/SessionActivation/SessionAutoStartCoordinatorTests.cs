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
    public async Task Codex_accounts_are_never_eligible()
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

    private static SessionAutoStartCoordinator CreateCoordinator(
        FakeOrchestrator orchestrator,
        AppSettings settings,
        FakeActivator activator,
        FakeStateStore store,
        FakeClock clock) =>
        new(
            orchestrator,
            settings,
            activator,
            store,
            clock,
            NullLogger<SessionAutoStartCoordinator>.Instance);

    private static PulseState State(
        string providerId,
        DateTimeOffset resetAt,
        TimeSpan? duration = null) =>
        new(
            new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase)
            {
                [providerId] = Reading(providerId, resetAt, duration ?? TimeSpan.FromHours(5))
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
        DateTimeOffset resetAt,
        TimeSpan duration) =>
        new(
            new UsagePulse(
                providerId,
                Baseline,
                0,
                100,
                0,
                100,
                null,
                null,
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
            Coordinator = CreateCoordinator(orchestrator, settings, activator, store, clock);
        }

        public AppSettings Settings { get; }
        public FakeClock Clock { get; }
        public FakeOrchestrator Orchestrator { get; }
        public FakeActivator Activator { get; }
        public FakeStateStore Store { get; }
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

        public Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken)
        {
            Refreshes.Add(providerId);
            OnRefresh?.Invoke(providerId);
            return Task.CompletedTask;
        }

        public void UpdateRefreshInterval(TimeSpan interval)
        {
        }
    }
}
