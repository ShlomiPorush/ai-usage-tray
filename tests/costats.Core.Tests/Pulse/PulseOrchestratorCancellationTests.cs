using costats.Application.Abstractions;
using costats.Application.Pulse;
using costats.Core.Pulse;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace costats.Core.Tests.Pulse;

/// <summary>
/// A refresh cancelled halfway through (settings change, shutdown) used to
/// publish an empty "No data" state over the last good one, which then reached
/// the tray and the remote viewer.
/// </summary>
public sealed class PulseOrchestratorCancellationTests
{
    private static readonly ProviderProfile Profile = new("claude:work", "Claude Work", "#FF7A00");

    [Fact]
    public async Task Cancellation_after_the_last_provider_read_publishes_nothing_and_keeps_the_last_good_state()
    {
        using var cts = new CancellationTokenSource();
        var selector = new ScriptedSelector();
        using var harness = new Harness(selector);

        selector.Next = (_, _) => Task.FromResult(Reading("good"));
        await harness.Orchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);

        var publishedAfterGoodRefresh = harness.Observer.States.Count;

        // Cancellation lands while the last provider group is being read.
        selector.Next = (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(Reading("poisoned"));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Orchestrator.RefreshOnceAsync(RefreshTrigger.Scheduled, cts.Token));

        Assert.Equal(publishedAfterGoodRefresh, harness.Observer.States.Count);
        Assert.Equal("good", harness.Observer.States[^1].Providers["claude:work"].StatusSummary);

        // A manual refresh replays the retained state, proving it was never poisoned.
        selector.Next = (_, _) => Task.FromResult(Reading("fresh"));
        await harness.Orchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);

        var shimmer = harness.Observer.States[publishedAfterGoodRefresh];
        Assert.True(shimmer.IsRefreshing);
        Assert.Equal("good", shimmer.Providers["claude:work"].StatusSummary);
    }

    [Fact]
    public async Task A_source_that_throws_cancellation_does_not_publish_an_error_state()
    {
        using var cts = new CancellationTokenSource();
        var selector = new ScriptedSelector();
        using var harness = new Harness(selector);

        selector.Next = (_, _) => Task.FromResult(Reading("good"));
        await harness.Orchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        var published = harness.Observer.States.Count;

        selector.Next = (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Reading("never"));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Orchestrator.RefreshOnceAsync(RefreshTrigger.Scheduled, cts.Token));

        Assert.Equal(published, harness.Observer.States.Count);
        Assert.Empty(harness.Observer.States[^1].Errors);
    }

    [Fact]
    public async Task A_real_failure_still_publishes_an_error_state()
    {
        var selector = new ScriptedSelector();
        using var harness = new Harness(selector);

        selector.Next = (_, _) => throw new InvalidOperationException("boom");

        await harness.Orchestrator.RefreshOnceAsync(RefreshTrigger.Scheduled, CancellationToken.None);

        var state = Assert.Single(harness.Observer.States);
        Assert.Equal("boom", Assert.Single(state.Errors));
    }

    [Fact]
    public async Task Cancelled_silent_provider_refresh_publishes_nothing()
    {
        using var cts = new CancellationTokenSource();
        var selector = new ScriptedSelector();
        using var harness = new Harness(selector);

        selector.Next = (_, _) => Task.FromResult(Reading("good"));
        await harness.Orchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        var published = harness.Observer.States.Count;

        selector.Next = (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(Reading("poisoned"));
        };

        await harness.Orchestrator.RefreshProviderAsync("claude:work", cts.Token);

        Assert.Equal(published, harness.Observer.States.Count);
        Assert.Equal("good", harness.Observer.States[^1].Providers["claude:work"].StatusSummary);
    }

    [Fact]
    public async Task Full_refresh_is_stale_only_after_the_requested_age()
    {
        var selector = new ScriptedSelector();
        using var harness = new Harness(selector);

        Assert.True(harness.Orchestrator.IsFullRefreshStale(TimeSpan.FromMinutes(5)));
        selector.Next = (_, _) => Task.FromResult(Reading("good"));
        await harness.Orchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);

        Assert.False(harness.Orchestrator.IsFullRefreshStale(TimeSpan.FromMinutes(5)));
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(5);
        Assert.True(harness.Orchestrator.IsFullRefreshStale(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Stale_refresh_is_skipped_while_another_refresh_is_in_progress()
    {
        var selector = new ScriptedSelector();
        using var harness = new Harness(selector);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;

        selector.Next = async (_, _) =>
        {
            Interlocked.Increment(ref reads);
            readStarted.SetResult();
            await releaseRead.Task;
            return Reading("good");
        };

        var activeRefresh = harness.Orchestrator.RefreshOnceAsync(RefreshTrigger.Scheduled, CancellationToken.None);
        await readStarted.Task;
        await harness.Orchestrator.RefreshIfStaleAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        releaseRead.SetResult();
        await activeRefresh;

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Stale_refresh_reads_all_providers_when_no_refresh_is_active()
    {
        var selector = new ScriptedSelector();
        using var harness = new Harness(selector);
        var reads = 0;
        selector.Next = (_, _) =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(Reading("good"));
        };

        await harness.Orchestrator.RefreshIfStaleAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        await harness.Orchestrator.RefreshIfStaleAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(1, reads);
        Assert.Equal(RefreshTrigger.Silent, Assert.Single(harness.Observer.States).Trigger);
    }

    private static ProviderReading Reading(string summary) => new(
        Usage: null,
        Identity: null,
        StatusSummary: summary,
        CapturedAt: DateTimeOffset.UnixEpoch,
        Confidence: ReadingConfidence.High,
        Source: ReadingSource.Api);

    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _subscription;

        public Harness(ISourceSelector selector)
        {
            var broadcaster = new PulseBroadcaster();
            Observer = new RecordingObserver();
            _subscription = broadcaster.Subscribe(Observer);
            Clock = new MutableClock();

            Orchestrator = new PulseOrchestrator(
                [new StubSource()],
                new EmptyRegistry(),
                selector,
                Clock,
                broadcaster,
                Options.Create(new PulseOptions()),
                NullLogger<PulseOrchestrator>.Instance);
        }

        public PulseOrchestrator Orchestrator { get; }

        public RecordingObserver Observer { get; }

        public MutableClock Clock { get; }

        public void Dispose()
        {
            _subscription.Dispose();
            Orchestrator.Dispose();
        }
    }

    private sealed class ScriptedSelector : ISourceSelector
    {
        public Func<string, CancellationToken, Task<ProviderReading>> Next { get; set; } =
            (_, _) => Task.FromResult(Reading("unset"));

        public Task<ProviderReading> SelectAsync(
            string providerId,
            IReadOnlyList<ISignalSource> sources,
            CancellationToken cancellationToken)
            => Next(providerId, cancellationToken);
    }

    private sealed class StubSource : ISignalSource
    {
        public ProviderProfile Profile => PulseOrchestratorCancellationTests.Profile;

        public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(Reading("stub"));
    }

    private sealed class EmptyRegistry : IAccountSourceRegistry
    {
        public IReadOnlyList<ISignalSource> Current => [];

        public void Reload()
        {
        }
    }

    public sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingObserver : IObserver<PulseState>
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
