using costats.Application.Abstractions;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace costats.Core.Tests.Pulse;

/// <summary>
/// The startup cache shows the last known usage while the first refresh runs.
/// It is display only, it never brings back an earlier account's numbers, and
/// it never feeds an alert.
/// </summary>
public sealed class UsageStartupCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private const string Claude = "claude:claude-1";

    [Fact]
    public async Task A_refresh_is_stored_and_shown_at_the_next_start_as_a_restored_refreshing_state()
    {
        var store = new MemoryStore();
        var cache = Cache(store);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        await cache.WhenIdleAsync();

        var next = Cache(store);
        var state = next.BuildStartupState([Claude])!;

        Assert.True(state.IsRefreshing);
        Assert.Equal(RefreshTrigger.Cached, state.Trigger);
        Assert.Equal(Now, state.LastRefresh);
        var reading = state.Providers[Claude];
        Assert.True(reading.IsRestored);
        Assert.Equal(40, reading.Usage!.WeekUsed);
        Assert.Equal("Max 20x", reading.Identity!.Plan);
        Assert.Null(reading.Identity.Email);
        Assert.DoesNotContain("person@example.com", store.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_passing_failure_keeps_the_last_good_reading_and_a_sign_in_prompt_removes_it()
    {
        var store = new MemoryStore();
        var cache = Cache(store);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(null))), fullRefresh: true);
        await cache.WhenIdleAsync();
        Assert.NotNull(Cache(store).BuildStartupState([Claude]));

        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(null) with { AuthenticationState = ProviderAuthenticationState.SignInRequired })), fullRefresh: true);
        await cache.WhenIdleAsync();
        Assert.Null(Cache(store).BuildStartupState([Claude]));
    }

    [Fact]
    public async Task Accounts_that_are_gone_or_hidden_are_not_shown()
    {
        var store = new MemoryStore();
        var cache = Cache(store);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40)), ("copilot", Live(10))), fullRefresh: true);
        await cache.WhenIdleAsync();

        // Copilot is turned off, and Claude is no longer among the sources.
        var state = Cache(store, new AppSettings { CopilotEnabled = false }).BuildStartupState(["copilot"]);

        Assert.Null(state);
    }

    [Fact]
    public async Task A_first_refresh_that_fails_before_the_startup_display_keeps_the_stored_usage()
    {
        var store = new MemoryStore();
        var seed = Cache(store);
        seed.Observe(seed.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        await seed.WhenIdleAsync();

        // The next run: its first refresh fails for Claude before anything is shown.
        var next = Cache(store);
        next.Observe(next.CaptureGeneration(), Readings((Claude, Live(null))), fullRefresh: true);
        await next.WhenIdleAsync();

        Assert.Equal(40, Cache(store).BuildStartupState([Claude])!.Providers[Claude].Usage!.WeekUsed);
    }

    [Fact]
    public async Task Usage_stored_for_another_folder_under_the_same_account_id_is_not_shown()
    {
        var store = new MemoryStore();
        var settings = new AppSettings { Accounts = [new MonitoredAccountSettings { Id = "claude-1", Type = "claude", ConfigDir = @"C:\profiles\one" }] };
        var cache = Cache(store, settings);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        await cache.WhenIdleAsync();
        Assert.NotNull(Cache(store, settings).BuildStartupState([Claude]));

        // Changed by another instance, or before a crash: same id, other folder.
        settings.Accounts[0].ConfigDir = @"C:\profiles\two";

        Assert.Null(Cache(store, settings).BuildStartupState([Claude]));
    }

    [Fact]
    public async Task A_stored_scoped_row_without_a_group_or_label_is_dropped_not_shown()
    {
        var store = new MemoryStore();
        var settings = new AppSettings { Accounts = [new MonitoredAccountSettings { Id = "claude-1", Type = "claude", ConfigDir = @"C:\profiles\one" }] };
        var cache = Cache(store, settings);
        var live = Live(40);
        cache.Observe(
            cache.CaptureGeneration(),
            Readings((Claude, live with { Usage = live.Usage! with { ScopedQuotas = [new ScopedQuota("Fable", "Weekly", 5, Now.AddDays(3), true)] } })),
            fullRefresh: true);
        await cache.WhenIdleAsync();

        // A damaged file: a row without its group, a null row, no provider id.
        store.Seed(store.Text!
            .Replace("\"group\":\"Weekly\"", "\"group\":null", StringComparison.Ordinal)
            .Replace("\"scopedQuotas\":[", "\"scopedQuotas\":[null,", StringComparison.Ordinal)
            .Replace("\"usage\":{\"providerId\":\"claude:claude-1\"", "\"usage\":{\"providerId\":null", StringComparison.Ordinal));

        var usage = Cache(store, settings).BuildStartupState([Claude])!.Providers[Claude].Usage!;

        Assert.Empty(usage.ScopedQuotas);
        Assert.Equal(Claude, usage.ProviderId);
        Assert.Equal(40, usage.WeekUsed);
    }

    [Fact]
    public async Task A_refresh_that_started_before_Clear_cannot_refill_the_cache()
    {
        var store = new MemoryStore();
        var cache = Cache(store);
        var before = cache.CaptureGeneration();

        cache.Clear();
        cache.Observe(before, Readings((Claude, Live(40))), fullRefresh: true);
        await cache.WhenIdleAsync();

        Assert.Null(store.Text);
        Assert.Null(Cache(store).BuildStartupState([Claude]));
    }

    [Fact]
    public async Task A_write_already_on_its_way_when_Clear_runs_is_removed_again()
    {
        var store = new MemoryStore { HoldWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = Cache(store);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        await store.WriteStarted.Task;

        cache.Clear();
        store.HoldWrites.SetResult();
        await cache.WhenIdleAsync();
        await cache.WhenIdleAsync();

        Assert.Null(store.Text);
    }

    [Fact]
    public async Task ClearAsync_returns_only_once_the_stored_usage_is_gone_from_disk()
    {
        var store = new MemoryStore { HoldWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = Cache(store);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        await store.WriteStarted.Task;

        var clearing = cache.ClearAsync(TimeSpan.FromSeconds(30));
        Assert.False(clearing.IsCompleted);
        store.HoldWrites.SetResult();
        await clearing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(store.Text);
    }

    [Fact]
    public async Task Nothing_read_during_a_sign_in_is_stored_even_by_a_refresh_that_starts_after_the_clear()
    {
        var store = new MemoryStore();
        var cache = Cache(store);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        await cache.WhenIdleAsync();

        using (cache.Suspend())
        {
            Assert.True(await cache.ClearAsync(TimeSpan.FromSeconds(30)));

            // A refresh during the sign-in still reads the previous person.
            cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(70))), fullRefresh: true);
            await cache.WhenIdleAsync();
            Assert.Null(store.Text);
        }

        // After the sign-in, the new account's usage is stored again.
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(5))), fullRefresh: true);
        await cache.WhenIdleAsync();
        Assert.Equal(5, Cache(store).BuildStartupState([Claude])!.Providers[Claude].Usage!.WeekUsed);
    }

    [Fact]
    public async Task ClearAsync_gives_up_after_its_time_limit_when_the_disk_is_stuck()
    {
        var store = new MemoryStore { HoldWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = Cache(store);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        await store.WriteStarted.Task;

        Assert.False(await cache.ClearAsync(TimeSpan.FromMilliseconds(200)));
        store.HoldWrites.SetResult();
    }

    [Fact]
    public async Task ClearAsync_reports_failure_when_the_file_cannot_be_deleted()
    {
        var store = new MemoryStore { FailDeletes = true };
        var cache = Cache(store);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        await cache.WhenIdleAsync();

        Assert.False(await cache.ClearAsync(TimeSpan.FromSeconds(30)));
        Assert.NotNull(store.Text);
    }

    [Fact]
    public void Clear_returns_at_once_while_a_write_is_held()
    {
        var store = new MemoryStore { HoldWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = Cache(store);
        cache.Observe(cache.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        Assert.True(store.WriteStarted.Task.Wait(TimeSpan.FromSeconds(5)));

        // Timed from inside the task, so a slow thread pool cannot fail the test.
        var elapsed = Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            cache.Clear();
            return watch.Elapsed;
        });

        Assert.True(elapsed.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(elapsed.Result < TimeSpan.FromSeconds(1));
        store.HoldWrites.SetResult();
    }

    [Fact]
    public async Task The_orchestrator_shows_the_stored_usage_only_as_a_display_and_never_as_the_current_state()
    {
        var store = new MemoryStore();
        var seed = Cache(store);
        seed.Observe(seed.CaptureGeneration(), Readings((Claude, Live(40))), fullRefresh: true);
        await seed.WhenIdleAsync();

        var observer = new Recorder();
        var broadcaster = new PulseBroadcaster();
        using var _ = broadcaster.Subscribe(observer);
        using var orchestrator = Orchestrator(broadcaster, Cache(store));

        orchestrator.PublishStartupState();

        var shown = Assert.Single(observer.States);
        Assert.Equal(RefreshTrigger.Cached, shown.Trigger);
        Assert.True(shown.IsRefreshing);
        Assert.Null(orchestrator.CurrentState);
    }

    [Fact]
    public async Task A_first_refresh_that_finished_before_the_observers_subscribed_is_delivered_by_the_startup_call()
    {
        var broadcaster = new PulseBroadcaster();
        using var orchestrator = Orchestrator(broadcaster, Cache(new MemoryStore()));
        await orchestrator.RefreshOnceAsync(RefreshTrigger.Scheduled, CancellationToken.None);

        var observer = new Recorder();
        using var _ = broadcaster.Subscribe(observer);
        orchestrator.PublishStartupState();

        var shown = Assert.Single(observer.States);
        Assert.NotEqual(RefreshTrigger.Cached, shown.Trigger);
        Assert.False(shown.IsRefreshing);
    }

    [Fact]
    public void Restored_readings_are_removed_before_the_alert_trackers()
    {
        var state = new PulseState(
            Readings((Claude, Live(40) with { IsRestored = true }), ("zai", Live(10))),
            Now,
            [],
            false,
            RefreshTrigger.Scheduled);

        var observed = RestoredUsage.WithoutRestoredReadings(state);

        Assert.Equal(["zai"], observed.Providers.Keys);
    }

    private static UsageStartupCache Cache(MemoryStore store, AppSettings? settings = null) =>
        new(store, settings ?? new AppSettings { CopilotEnabled = true }, new FixedClock());

    private static PulseOrchestrator Orchestrator(PulseBroadcaster broadcaster, UsageStartupCache cache) =>
        new(
            [new Source(Claude)],
            new NoAccounts(),
            new LiveSelector(),
            new FixedClock(),
            broadcaster,
            Options.Create(new PulseOptions()),
            NullLogger<PulseOrchestrator>.Instance,
            cache);

    private static Dictionary<string, ProviderReading> Readings(params (string Id, ProviderReading Reading)[] readings) =>
        readings.ToDictionary(pair => pair.Id, pair => pair.Reading, StringComparer.OrdinalIgnoreCase);

    private static ProviderReading Live(long? weekUsed) => new(
        weekUsed is null
            ? null
            : new UsagePulse(Claude, Now, null, null, weekUsed, 100, null, new QuotaWindow(TimeSpan.FromDays(7), Now.AddDays(3))),
        new IdentityCard(Claude, "Personal", "person@example.com", null, "Max 20x", "oauth"),
        "ok",
        Now,
        ReadingConfidence.High,
        ReadingSource.Api);

    private sealed class MemoryStore : IUsageSnapshotStore
    {
        public string? Text { get; private set; }

        public TaskCompletionSource? HoldWrites { get; init; }

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Read() => Text;

        public void Seed(string text) => Text = text;

        public async Task WriteAsync(string text)
        {
            WriteStarted.TrySetResult();
            if (HoldWrites is not null)
            {
                await HoldWrites.Task;
            }

            Text = text;
        }

        public bool FailDeletes { get; init; }

        public Task DeleteAsync()
        {
            if (FailDeletes)
            {
                throw new UnauthorizedAccessException("locked");
            }

            Text = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Source(string providerId) : ISignalSource
    {
        public ProviderProfile Profile { get; } = new(providerId, providerId, "#000000");

        public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Live(40));
    }

    private sealed class LiveSelector : ISourceSelector
    {
        public Task<ProviderReading> SelectAsync(string providerId, IReadOnlyList<ISignalSource> sources, CancellationToken cancellationToken) =>
            Task.FromResult(Live(50));
    }

    private sealed class NoAccounts : IAccountSourceRegistry
    {
        public IReadOnlyList<ISignalSource> Current => [];

        public void Reload()
        {
        }
    }

    private sealed class Recorder : IObserver<PulseState>
    {
        public List<PulseState> States { get; } = [];

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(PulseState value) => States.Add(value);
    }
}
