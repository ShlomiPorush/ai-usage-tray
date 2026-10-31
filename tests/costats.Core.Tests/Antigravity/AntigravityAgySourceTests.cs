using costats.Application.Settings;
using costats.Core.Antigravity;
using costats.Core.Pulse;
using costats.Infrastructure.Antigravity;
using Xunit;

namespace costats.Core.Tests.Antigravity;

/// <summary>The source's state machine, driven by a fake runner. No process is started.</summary>
public sealed class AntigravityAgySourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly AppSettings _settings = new() { AntigravityEnabled = true, AntigravityMinIntervalMinutes = 15 };
    private readonly ManualTimeProvider _time = new(T0);
    private readonly FakeRunner _runner = new();

    [Fact]
    public async Task Disabled_returns_a_disabled_reading_without_running_anything()
    {
        _settings.AntigravityEnabled = false;

        var reading = await Source().ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.Equal("Antigravity disabled in Settings", reading.StatusSummary);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Preview_runs_never_start_a_check()
    {
        _settings.AntigravityProbeSuppressed = true;

        var reading = await Source().ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Waiting_without_a_previous_result_names_the_next_check_time()
    {
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Waiting, NextAllowedAt: T0.AddMinutes(15)));

        var reading = await Source().ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.Equal("Waiting for the next Antigravity check (08:15)", reading.StatusSummary);
        Assert.Equal(TimeSpan.FromMinutes(15), _runner.Requests.Single().Interval);
    }

    [Fact]
    public async Task Success_then_failure_then_wait_returns_the_failure_not_the_older_success()
    {
        var source = Source();
        _runner.Enqueue(Success());
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.NoQuota, CompletedAt: T0));
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Waiting, NextAllowedAt: T0.AddMinutes(15)));

        var success = await source.ReadAsync(CancellationToken.None);
        Assert.NotNull(success.Usage);
        Assert.Equal(ReadingSource.Cli, success.Source);
        Assert.Equal("Updated 08:00", success.StatusSummary);
        Assert.Equal("Google AI Plus", success.Identity!.Plan);
        Assert.Null(success.Identity.Email);
        Assert.Equal(100, success.Usage!.WeekUsed);

        var failure = await source.ReadAsync(CancellationToken.None);
        Assert.Null(failure.Usage);
        Assert.Equal("agy returned no quota. If agy is not signed in, run agy once.", failure.StatusSummary);

        var cached = await source.ReadAsync(CancellationToken.None);
        Assert.Same(failure, cached);
    }

    [Fact]
    public async Task A_cached_success_is_returned_while_waiting()
    {
        var source = Source();
        _runner.Enqueue(Success());
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Waiting, NextAllowedAt: T0.AddMinutes(15)));

        var success = await source.ReadAsync(CancellationToken.None);
        var cached = await source.ReadAsync(CancellationToken.None);

        Assert.Same(success, cached);
    }

    [Fact]
    public async Task A_fresh_process_shows_the_stored_quota_while_waiting_marked_restored()
    {
        var stored = Success().Quota!;
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Waiting, stored, T0.AddMinutes(15), T0.AddMinutes(-5)));

        var reading = await Source().ReadAsync(CancellationToken.None);

        Assert.True(reading.IsRestored);
        Assert.Equal(100, reading.Usage!.WeekUsed);
        Assert.Equal("Updated 07:55", reading.StatusSummary);
        Assert.Equal(T0.AddMinutes(-5), reading.CapturedAt);
    }

    [Fact]
    public async Task This_process_own_success_wins_over_the_stored_quota_so_alerts_keep_their_baseline()
    {
        // 40% -> Waiting -> 80%: the reading in between must stay a live one,
        // or the alert tracker would forget the 40% baseline.
        var source = Source();
        _runner.Enqueue(Success());
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Waiting, Success().Quota, T0.AddMinutes(15), T0));

        var live = await source.ReadAsync(CancellationToken.None);
        var waiting = await source.ReadAsync(CancellationToken.None);

        Assert.Same(live, waiting);
        Assert.False(waiting.IsRestored);
    }

    [Fact]
    public async Task This_process_own_failure_wins_over_the_stored_quota()
    {
        var source = Source();
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.NoQuota, CompletedAt: T0));
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Waiting, Success().Quota, T0.AddMinutes(15), T0.AddMinutes(-30)));

        var failure = await source.ReadAsync(CancellationToken.None);
        var waiting = await source.ReadAsync(CancellationToken.None);

        Assert.Same(failure, waiting);
        Assert.Null(waiting.Usage);
    }

    [Theory]
    [InlineData(AgyProbeStatus.AgyNotFound, "agy (Antigravity CLI) was not found.")]
    [InlineData(AgyProbeStatus.LaunchFailed, "agy could not be started.")]
    [InlineData(AgyProbeStatus.StorageFailed, "The Antigravity check folder could not be used.")]
    [InlineData(AgyProbeStatus.UnsupportedFormat, "Gemini quota not reported")]
    public async Task Every_failure_has_its_own_fixed_text_and_never_asks_for_sign_in(AgyProbeStatus status, string text)
    {
        _runner.Enqueue(new AgyProbeResult(status, CompletedAt: T0));

        var reading = await Source().ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.StartsWith(text, reading.StatusSummary, StringComparison.Ordinal);
        Assert.NotEqual(ProviderAuthenticationState.SignInRequired, reading.AuthenticationState);
    }

    [Fact]
    public async Task Busy_is_not_a_failure_and_returns_the_last_result()
    {
        var source = Source();
        _runner.Enqueue(Success());
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Busy));

        var success = await source.ReadAsync(CancellationToken.None);
        var busy = await source.ReadAsync(CancellationToken.None);

        Assert.Same(success, busy);
    }

    [Fact]
    public async Task Busy_without_a_previous_result_is_a_waiting_reading()
    {
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Busy));

        var reading = await Source().ReadAsync(CancellationToken.None);

        Assert.Equal("Waiting for the next Antigravity check", reading.StatusSummary);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var source = Source();
        using var cancel = new CancellationTokenSource();
        _runner.Enqueue(async (_, token) =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return Success();
        });

        var read = source.ReadAsync(cancel.Token);
        await _runner.WaitForCallsAsync(1);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task A_cancelled_result_is_not_shown_as_a_failure()
    {
        var source = Source();
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Cancelled));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.Equal("Waiting for the next Antigravity check", reading.StatusSummary);
    }

    [Fact]
    public async Task Disabling_cancels_the_running_check()
    {
        var source = Source();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Enqueue(async (_, token) =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
            }

            return new AgyProbeResult(AgyProbeStatus.Cancelled);
        });

        var read = source.ReadAsync(CancellationToken.None);
        await _runner.WaitForCallsAsync(1);
        _settings.AntigravityEnabled = false;

        await cancelled.Task.WaitAsync(Timeout);
        var reading = await read.WaitAsync(Timeout);
        Assert.Equal("Antigravity disabled in Settings", reading.StatusSummary);
    }

    [Fact]
    public async Task A_result_that_arrives_after_disable_is_discarded()
    {
        var source = Source();
        var release = new TaskCompletionSource<AgyProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Enqueue((_, _) => release.Task); // ignores cancellation on purpose

        var read = source.ReadAsync(CancellationToken.None);
        await _runner.WaitForCallsAsync(1);
        var generation = source.Generation;
        _settings.AntigravityEnabled = false;
        await WaitUntil(() => source.Generation > generation);
        release.SetResult(Success());

        var reading = await read.WaitAsync(Timeout);
        Assert.Null(reading.Usage);
        Assert.Equal("Antigravity disabled in Settings", reading.StatusSummary);
    }

    [Fact]
    public async Task A_result_that_arrives_after_disable_and_enable_is_discarded()
    {
        var source = Source();
        var release = new TaskCompletionSource<AgyProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Enqueue((_, _) => release.Task);
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Waiting, NextAllowedAt: T0.AddMinutes(15)));

        var read = source.ReadAsync(CancellationToken.None);
        await _runner.WaitForCallsAsync(1);
        var generation = source.Generation;
        _settings.AntigravityEnabled = false;
        await WaitUntil(() => source.Generation > generation);
        _settings.AntigravityEnabled = true;
        release.SetResult(Success());

        var stale = await read.WaitAsync(Timeout);
        Assert.Null(stale.Usage);

        var next = await source.ReadAsync(CancellationToken.None);
        Assert.Null(next.Usage);
        Assert.Equal("Waiting for the next Antigravity check (08:15)", next.StatusSummary);
    }

    [Fact]
    public async Task A_result_returned_right_after_disable_is_not_cached()
    {
        // The watcher never polls in this test, so only the result handling can notice.
        var source = new AntigravityAgySource(_settings, _runner, null, _time, TimeSpan.FromMinutes(10));
        _runner.Enqueue((_, _) =>
        {
            _settings.AntigravityEnabled = false;
            return Task.FromResult(Success());
        });

        var reading = await source.ReadAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Null(reading.Usage);
        Assert.Equal("Antigravity disabled in Settings", reading.StatusSummary);

        _settings.AntigravityEnabled = true;
        _runner.Enqueue(new AgyProbeResult(AgyProbeStatus.Waiting, NextAllowedAt: T0.AddMinutes(15)));
        var next = await source.ReadAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Null(next.Usage);
        Assert.Equal("Waiting for the next Antigravity check (08:15)", next.StatusSummary);
    }

    [Fact]
    public async Task Consecutive_failures_survive_a_restart_and_start_the_backoff()
    {
        var basePath = AgyFixtures.NewBasePath("source-restart");
        try
        {
            // One known boot throughout: only the app restarts.
            var uptime = new FakeUptime(50_000_000) { BootId = 41 };
            AntigravityAgySource NewSource() => new(
                _settings,
                new AgyProbeRunner(null, new AgyProbeRunnerOptions
                {
                    BasePath = basePath,
                    ResolveExecutable = () => null,
                    Time = _time,
                    Uptime = uptime
                }),
                null,
                _time,
                TimeSpan.FromMilliseconds(20));
            void Advance()
            {
                _time.Advance(TimeSpan.FromMinutes(16));
                uptime.Advance(TimeSpan.FromMinutes(16));
            }

            var first = NewSource();
            await first.ReadAsync(CancellationToken.None);
            Advance();
            await first.ReadAsync(CancellationToken.None);
            Advance();

            // The app restarts (or a second instance takes over): a new source and runner.
            await NewSource().ReadAsync(CancellationToken.None);

            var stored = ReadSchedule(basePath);
            Assert.Equal(3, stored.ConsecutiveFailures);
            Assert.Equal(_time.Now + AgyProbeSchedule.MinimumBackoff, stored.BackoffUntil);
        }
        finally
        {
            AgyFixtures.DeleteQuietly(basePath);
        }
    }

    private static AgyProbeScheduleState ReadSchedule(string basePath)
    {
        var file = AgyProbeSchedule.Parse(File.ReadAllText(
            Path.Combine(basePath, "AIUsageTray", AgyProbeRunner.FolderName, AgyProbeRunner.ScheduleFileName)));
        Assert.Equal(AgyScheduleFileKind.Valid, file.Kind);
        return file.State!;
    }

    [Fact]
    public async Task Concurrent_reads_share_one_check()
    {
        var source = Source();
        var release = new TaskCompletionSource<AgyProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Enqueue((_, _) => release.Task);

        var first = source.ReadAsync(CancellationToken.None);
        await _runner.WaitForCallsAsync(1);
        var second = source.ReadAsync(CancellationToken.None);
        var third = source.ReadAsync(CancellationToken.None);
        release.SetResult(Success());

        var readings = await Task.WhenAll(first, second, third).WaitAsync(Timeout);
        Assert.Equal(1, _runner.Calls);
        Assert.All(readings, reading => Assert.Same(readings[0], reading));
        Assert.NotNull(readings[0].Usage);
    }

    [Fact]
    public async Task A_runner_that_throws_becomes_a_failure_reading()
    {
        _runner.Enqueue((_, _) => throw new InvalidOperationException("boom"));

        var source = Source();
        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.StartsWith("agy could not be started.", reading.StatusSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backoff_through_the_real_schedule_holds_after_three_failures()
    {
        var basePath = AgyFixtures.NewBasePath("source-backoff");
        try
        {
            var uptime = new FakeUptime(50_000_000);
            var runner = new AgyProbeRunner(null, new AgyProbeRunnerOptions
            {
                BasePath = basePath,
                ResolveExecutable = () => null,
                Time = _time,
                Uptime = uptime
            });
            var source = new AntigravityAgySource(_settings, runner, null, _time, TimeSpan.FromMilliseconds(20));

            for (var index = 0; index < 3; index++)
            {
                var failure = await source.ReadAsync(CancellationToken.None);
                Assert.StartsWith("agy (Antigravity CLI) was not found.", failure.StatusSummary, StringComparison.Ordinal);
                _time.Advance(TimeSpan.FromMinutes(16));
                uptime.Advance(TimeSpan.FromMinutes(16));
            }

            // Third failure at +32 min started a 60 minute back-off: +48 min still waits.
            var waiting = await source.ReadAsync(CancellationToken.None);
            Assert.Equal(3, ReadSchedule(basePath).ConsecutiveFailures);
            Assert.StartsWith("agy (Antigravity CLI) was not found.", waiting.StatusSummary, StringComparison.Ordinal);

            _time.Advance(TimeSpan.FromMinutes(45));
            uptime.Advance(TimeSpan.FromMinutes(45));
            await source.ReadAsync(CancellationToken.None);
            Assert.Equal(4, ReadSchedule(basePath).ConsecutiveFailures);
        }
        finally
        {
            AgyFixtures.DeleteQuietly(basePath);
        }
    }

    private AntigravityAgySource Source() =>
        new(_settings, _runner, null, _time, TimeSpan.FromMilliseconds(20));

    private AgyProbeResult Success() =>
        new(AgyProbeStatus.Success, AgyQuotaParser.Parse(AgyFixtures.RealPayload).Reading, CompletedAt: _time.Now);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time");
            await Task.Delay(10);
        }
    }

    private sealed class FakeRunner : IAgyProbeRunner
    {
        private readonly Queue<Func<AgyProbeRequest, CancellationToken, Task<AgyProbeResult>>> _steps = new();
        private readonly List<AgyProbeRequest> _requests = [];
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<AgyProbeRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        public void Enqueue(AgyProbeResult result) => Enqueue((_, _) => Task.FromResult(result));

        public void Enqueue(Func<AgyProbeRequest, CancellationToken, Task<AgyProbeResult>> step)
        {
            lock (_steps)
            {
                _steps.Enqueue(step);
            }
        }

        public Task<AgyProbeResult> RunAsync(AgyProbeRequest request, CancellationToken cancellationToken)
        {
            Func<AgyProbeRequest, CancellationToken, Task<AgyProbeResult>> step;
            lock (_steps)
            {
                step = _steps.Count > 0
                    ? _steps.Dequeue()
                    : (_, _) => Task.FromResult(new AgyProbeResult(AgyProbeStatus.Waiting, NextAllowedAt: T0.AddHours(1)));
            }

            lock (_requests)
            {
                _requests.Add(request);
            }

            Interlocked.Increment(ref _calls);
            return step(request, cancellationToken);
        }

        public async Task WaitForCallsAsync(int count)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (Calls < count)
            {
                Assert.True(DateTime.UtcNow < deadline, "Runner was not called in time");
                await Task.Delay(5);
            }
        }
    }
}
