using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using costats.App.Services;
using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Core.Remote;
using costats.Core.RemoteView;
using Serilog;
using Xunit;

namespace costats.Core.Tests.Services;

/// <summary>
/// Drives usage sharing with a fake relay and a fake clock: write order,
/// sequences, generations, removals and the upload schedule.
/// </summary>
public sealed class RemoteUsageSharingTests
{
    private const string Relay = "https://relay.example.test";
    private static readonly string WriteIdA = RemoteViewIds.MintWriteId();
    private static readonly string WriteIdB = RemoteViewIds.MintWriteId();
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Turning_sharing_off_during_an_upload_removes_after_it_with_a_higher_seq()
    {
        var harness = new Harness();
        var gate = new TaskCompletionSource();
        harness.Relay.Respond = async request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                await gate.Task;
            }

            return Status(HttpStatusCode.NoContent);
        };

        var cycle = harness.Sharing.RunCycleAsync();
        await harness.Relay.WaitForAsync(1);

        harness.Settings.RemoteViewShareUsage = false;
        var removal = harness.Sharing.Reconcile();
        Assert.NotNull(removal);
        gate.SetResult();
        await cycle;
        Assert.True(await removal);

        Assert.Collection(
            harness.Relay.Requests,
            put => Assert.Equal(("PUT", $"/u/{WriteIdA}/usage"), (put.Method, put.Path)),
            delete => Assert.Equal(("DELETE", $"/u/{WriteIdA}/usage"), (delete.Method, delete.Path)));
        Assert.True(harness.Relay.Requests[1].Seq > harness.Relay.Requests[0].Seq);
        Assert.Empty(harness.Settings.RemoteUsagePendingDeletes);
    }

    [Fact]
    public async Task Off_then_on_quickly_removes_first_and_uploads_after()
    {
        var harness = new Harness();
        await harness.Sharing.RunCycleAsync();

        harness.Settings.RemoteViewShareUsage = false;
        _ = harness.Sharing.Reconcile();
        harness.Settings.RemoteViewShareUsage = true;
        _ = harness.Sharing.Reconcile();
        await harness.Sharing.RunCycleAsync();
        await harness.Queue.WhenIdleAsync();

        Assert.Equal(["PUT", "DELETE", "PUT"], harness.Relay.Requests.Select(request => request.Method));
        Assert.True(harness.Relay.Requests[2].Seq > harness.Relay.Requests[1].Seq);
        Assert.True(harness.Relay.Requests[1].Seq > harness.Relay.Requests[0].Seq);
        // Re-enabling the same target drops its pending removal.
        Assert.Empty(harness.Settings.RemoteUsagePendingDeletes);
    }

    [Fact]
    public async Task A_retried_old_removal_keeps_its_old_lower_seq_after_sharing_resumes()
    {
        var harness = new Harness();
        var deletes = 0;
        harness.Relay.Respond = request => Task.FromResult(
            request.Method == HttpMethod.Delete && Interlocked.Increment(ref deletes) == 1
                ? Status(HttpStatusCode.InternalServerError)
                : Status(HttpStatusCode.NoContent));

        await harness.Sharing.RunCycleAsync();
        harness.Settings.RemoteViewShareUsage = false;
        Assert.False(await harness.Sharing.Reconcile()!);
        var pending = Assert.Single(harness.Settings.RemoteUsagePendingDeletes);
        Assert.Equal(WriteIdA, pending.WriteId);

        // Sharing resumes under a new link while the old removal is still owed.
        harness.Settings.RemoteViewId = WriteIdB;
        harness.Settings.RemoteViewShareUsage = true;
        Assert.Null(harness.Sharing.Reconcile());
        await harness.Sharing.RunCycleAsync();

        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        await harness.Sharing.RetryPendingDeletesAsync();
        await harness.Queue.WhenIdleAsync();

        var newPut = harness.Relay.Requests.Single(request => request.Method == "PUT" && request.Path.Contains(WriteIdB, StringComparison.Ordinal));
        var retried = harness.Relay.Requests.Last();
        Assert.Equal(("DELETE", $"/u/{WriteIdA}/usage"), (retried.Method, retried.Path));
        Assert.Equal(pending.Seq, retried.Seq);
        Assert.True(retried.Seq < newPut.Seq);
        Assert.Empty(harness.Settings.RemoteUsagePendingDeletes);
    }

    [Fact]
    public async Task A_new_link_or_relay_removes_the_report_from_the_old_target()
    {
        var harness = new Harness();
        await harness.Sharing.RunCycleAsync();

        harness.Settings.RemoteViewId = WriteIdB;
        var removal = harness.Sharing.Reconcile();
        Assert.Equal(WriteIdA, Assert.Single(harness.Settings.RemoteUsagePendingDeletes).WriteId);
        Assert.True(await removal!);
        Assert.Equal(("DELETE", "relay.example.test", $"/u/{WriteIdA}/usage"), Last(harness));

        await harness.Sharing.RunCycleAsync();
        Assert.Equal(("PUT", "relay.example.test", $"/u/{WriteIdB}/usage"), Last(harness));

        harness.Settings.RemoteViewUploadUrl = "https://other-relay.example.test";
        Assert.True(await harness.Sharing.Reconcile()!);
        Assert.Equal(("DELETE", "relay.example.test", $"/u/{WriteIdB}/usage"), Last(harness));

        static (string, string, string) Last(Harness harness)
        {
            var request = harness.Relay.Requests.Last();
            return (request.Method, request.Host, request.Path);
        }
    }

    [Fact]
    public async Task A_restart_retries_saved_removals_with_their_saved_seq()
    {
        var settings = Harness.SharedSettings();
        settings.RemoteViewShareUsage = false;
        var seq = Start.AddMinutes(-30).ToUnixTimeMilliseconds();
        settings.RemoteUsageLastSeq = seq;
        settings.RemoteUsagePendingDeletes = [new RemoteUsagePendingDelete { UploadUrl = Relay, WriteId = WriteIdB, Seq = seq }];

        var harness = new Harness(settings);
        await harness.Sharing.TickAsync();
        await harness.Queue.WhenIdleAsync();

        var delete = Assert.Single(harness.Relay.Requests);
        Assert.Equal(("DELETE", $"/u/{WriteIdB}/usage", seq), (delete.Method, delete.Path, delete.Seq));
        Assert.Empty(settings.RemoteUsagePendingDeletes);
        Assert.True(harness.Saves > 0);
    }

    [Fact]
    public async Task Removals_retry_with_backoff_and_are_never_given_up_by_age()
    {
        var harness = new Harness();
        harness.Relay.Respond = _ => Task.FromResult(Status(HttpStatusCode.ServiceUnavailable));
        harness.Settings.RemoteViewShareUsage = false;
        Assert.False(await harness.Sharing.Reconcile()!);

        harness.Clock.Advance(TimeSpan.FromMinutes(9));
        await harness.Sharing.RetryPendingDeletesAsync();
        await harness.Queue.WhenIdleAsync();
        Assert.Single(harness.Relay.Requests);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Sharing.RetryPendingDeletesAsync();
        await harness.Queue.WhenIdleAsync();
        Assert.Equal(2, harness.Relay.Requests.Count);

        // The second failure waits twice as long.
        harness.Clock.Advance(TimeSpan.FromMinutes(19));
        await harness.Sharing.RetryPendingDeletesAsync();
        await harness.Queue.WhenIdleAsync();
        Assert.Equal(2, harness.Relay.Requests.Count);

        // A clock jump weeks ahead does not drop a removal the relay may still need.
        harness.Clock.Advance(TimeSpan.FromDays(30));
        await harness.Sharing.RetryPendingDeletesAsync();
        await harness.Queue.WhenIdleAsync();
        Assert.Equal(3, harness.Relay.Requests.Count);
        Assert.Single(harness.Settings.RemoteUsagePendingDeletes);
    }

    [Fact]
    public async Task Saved_removals_with_an_unusable_address_or_id_are_dropped()
    {
        var settings = Harness.SharedSettings();
        settings.RemoteViewShareUsage = false;
        settings.RemoteUsagePendingDeletes =
        [
            new RemoteUsagePendingDelete { UploadUrl = "http://not-https.example.test", WriteId = WriteIdA, Seq = 5 },
            new RemoteUsagePendingDelete { UploadUrl = Relay, WriteId = "not-an-id", Seq = 6 }
        ];
        var harness = new Harness(settings);

        await harness.Sharing.RetryPendingDeletesAsync();
        await harness.Queue.WhenIdleAsync();

        Assert.Empty(harness.Relay.Requests);
        Assert.Empty(settings.RemoteUsagePendingDeletes);
    }

    [Fact]
    public async Task Off_and_on_during_an_upload_sends_a_new_upload_after_the_removal_and_never_publishes_the_removed_seq()
    {
        var harness = new Harness();
        var gate = new TaskCompletionSource();
        long? publishedWhenRemoved = -1;
        harness.Relay.Respond = async request =>
        {
            if (request.Method == HttpMethod.Put && harness.Relay.Requests.Count == 1)
            {
                await gate.Task;
            }

            if (request.Method == HttpMethod.Delete)
            {
                // The first upload's answer has been handled by now: the queue is serial.
                publishedWhenRemoved = harness.Sharing.SnapshotStatus()?.Seq;
            }

            return Status(HttpStatusCode.NoContent);
        };

        var first = harness.Sharing.RunCycleAsync();
        await harness.Relay.WaitForAsync(1);

        harness.Settings.RemoteViewShareUsage = false;
        _ = harness.Sharing.Reconcile();
        harness.Settings.RemoteViewShareUsage = true;
        _ = harness.Sharing.Reconcile();
        // The cycle asked for while the first one still runs.
        await harness.Sharing.RunCycleAsync();

        gate.SetResult();
        await first;
        await harness.Queue.WhenIdleAsync();

        Assert.Equal(["PUT", "DELETE", "PUT"], harness.Relay.Requests.Select(request => request.Method));
        var (stale, removal, fresh) = (harness.Relay.Requests[0], harness.Relay.Requests[1], harness.Relay.Requests[2]);
        Assert.True(fresh.Seq > removal.Seq);
        Assert.Null(publishedWhenRemoved);
        Assert.Equal(fresh.Seq, harness.Sharing.SnapshotStatus()!.Seq);
        Assert.NotEqual(stale.Seq, harness.Sharing.SnapshotStatus()!.Seq);
    }

    [Fact]
    public async Task An_upload_composed_for_a_gone_generation_is_never_sent()
    {
        var harness = new Harness();
        var gate = new TaskCompletionSource<bool>();
        _ = harness.Queue.Enqueue(() => gate.Task);

        var cycle = harness.Sharing.RunCycleAsync();
        await WaitUntilAsync(() => harness.Composes > 0);
        harness.Settings.RemoteViewId = WriteIdB;
        gate.SetResult(true);
        await cycle;
        await harness.Queue.WhenIdleAsync();

        Assert.Empty(harness.Relay.Requests);
        Assert.Equal(0, harness.Settings.RemoteUsageLastSeq);
    }

    [Fact]
    public async Task A_stale_cycle_that_fails_leaves_the_new_cycle_scheduled_at_once()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var harness = new Harness(compose: number => number == 1 ? FailAfter(started, release) : null);

        var old = harness.Sharing.RunCycleAsync();
        await started.Task;
        harness.Settings.RemoteViewShareUsage = false;
        _ = harness.Sharing.Reconcile();
        harness.Settings.RemoteViewShareUsage = true;
        _ = harness.Sharing.Reconcile();
        // Asked for while the old cycle still runs, as Reconcile does in the app.
        await harness.Sharing.RunCycleAsync();

        release.SetResult();
        await old;
        await harness.Queue.WhenIdleAsync();

        // No backoff from the stale failure: the new upload goes out at once.
        Assert.Equal(2, harness.Composes);
        Assert.Equal(["DELETE", "PUT"], harness.Relay.Requests.Select(request => request.Method));

        static async Task<RemoteUsageSource> FailAfter(TaskCompletionSource started, TaskCompletionSource release)
        {
            started.SetResult();
            await release.Task;
            throw new IOException("The scan failed after sharing changed.");
        }
    }

    [Fact]
    public async Task A_stale_cycle_that_skips_leaves_the_new_cycle_scheduled_at_once()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        // Every composition is over the model limit, so every cycle skips.
        var harness = new Harness(
            compose: number => number == 1 ? HoldThenAnswer(started, release) : null,
            limits: new RemoteUsageLimits { MaxModels = 0 });

        var old = harness.Sharing.RunCycleAsync();
        await started.Task;
        harness.Settings.RemoteViewShareUsage = false;
        _ = harness.Sharing.Reconcile();
        harness.Settings.RemoteViewShareUsage = true;
        _ = harness.Sharing.Reconcile();
        await harness.Sharing.RunCycleAsync();

        release.SetResult();
        await old;

        // The requested cycle composed right away instead of waiting out a backoff.
        Assert.Equal(2, harness.Composes);

        static async Task<RemoteUsageSource> HoldThenAnswer(TaskCompletionSource started, TaskCompletionSource release)
        {
            started.SetResult();
            await release.Task;
            return new RemoteUsageSource([Harness.Sample(Start.AddDays(-3))], ModelPricingTable.Default, []);
        }
    }

    [Fact]
    public async Task A_cycle_never_pairs_the_old_target_with_the_new_generation()
    {
        var harness = new Harness();
        // The link changes right as the cycle starts: after any target read
        // that comes before the cycle's lock, and before its generation.
        harness.Clock.OnNextRead = () =>
        {
            harness.Settings.RemoteViewId = WriteIdB;
            _ = harness.Sharing.Reconcile();
        };

        await harness.Sharing.RunCycleAsync();
        await harness.Queue.WhenIdleAsync();

        // The new link is uploaded at once, not after a pushed-out schedule.
        Assert.Equal(
            [("DELETE", $"/u/{WriteIdA}/usage"), ("PUT", $"/u/{WriteIdB}/usage")],
            harness.Relay.Requests.Select(request => (request.Method, request.Path)));
    }

    [Fact]
    public async Task An_older_removal_never_clears_a_newer_upload_to_the_same_target()
    {
        var harness = new Harness();
        await harness.Sharing.RunCycleAsync();
        var uploaded = Assert.Single(harness.Relay.Requests).Seq!.Value;

        // A removal from before the upload, for the same link, runs late.
        harness.Settings.RemoteUsagePendingDeletes =
            [new RemoteUsagePendingDelete { UploadUrl = Relay, WriteId = WriteIdA, Seq = uploaded - 100 }];
        await harness.Sharing.RetryPendingDeletesAsync();
        await harness.Queue.WhenIdleAsync();

        Assert.Equal("DELETE", harness.Relay.Requests[^1].Method);
        Assert.Equal(uploaded, harness.Sharing.SnapshotStatus()!.Seq);
        Assert.NotNull(harness.Sharing.SnapshotStatus()!.CheckedAt);
    }

    [Fact]
    public async Task Seq_is_the_larger_of_now_and_last_plus_one_and_is_saved_before_sending()
    {
        var harness = new Harness();
        harness.Settings.RemoteUsageLastSeq = Start.ToUnixTimeMilliseconds() + 5000;

        await harness.Sharing.RunCycleAsync();

        var put = Assert.Single(harness.Relay.Requests);
        Assert.Equal(Start.ToUnixTimeMilliseconds() + 5001, put.Seq);
        Assert.Equal(put.Seq, put.SavedSeqWhenSent);

        var fresh = new Harness();
        await fresh.Sharing.RunCycleAsync();
        Assert.Equal(Start.ToUnixTimeMilliseconds(), Assert.Single(fresh.Relay.Requests).Seq);
        Assert.Equal(Start.ToUnixTimeMilliseconds(), fresh.Settings.RemoteUsageLastSeq);
    }

    [Fact]
    public async Task An_unchanged_report_is_confirmed_not_resent_until_the_heartbeat()
    {
        var harness = new Harness();
        await harness.Sharing.RunCycleAsync();
        Assert.Single(harness.Relay.Requests);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Sharing.RunCycleAsync();
        Assert.Equal(1, harness.Composes);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Sharing.RunCycleAsync();
        Assert.Equal(2, harness.Composes);
        Assert.Single(harness.Relay.Requests);
        var status = harness.Sharing.SnapshotStatus()!;
        Assert.Equal(harness.Relay.Requests[0].Seq, status.Seq);
        Assert.Equal(RemoteUsageComposer.FormatInstant(harness.Clock.GetUtcNow()), status.CheckedAt);

        // Changed data goes out at the next cycle.
        harness.Samples.Add(Harness.Sample(Start.AddDays(-4)));
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        await harness.Sharing.RunCycleAsync();
        Assert.Equal(2, harness.Relay.Requests.Count);

        harness.Clock.Advance(RemoteUsageSharing.HeartbeatInterval);
        await harness.Sharing.RunCycleAsync();
        Assert.Equal(3, harness.Relay.Requests.Count);
    }

    [Fact]
    public async Task Failures_back_off_from_ten_minutes_doubling_to_two_hours()
    {
        Assert.Equal(
            [10, 20, 40, 80, 120, 120],
            Enumerable.Range(1, 6).Select(failures => RemoteUsageSharing.Backoff(failures).TotalMinutes));

        var harness = new Harness();
        harness.Relay.Respond = _ => Task.FromResult(Status(HttpStatusCode.BadGateway));
        await harness.Sharing.RunCycleAsync();

        harness.Clock.Advance(TimeSpan.FromMinutes(9));
        await harness.Sharing.RunCycleAsync();
        Assert.Single(harness.Relay.Requests);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Sharing.RunCycleAsync();
        Assert.Equal(2, harness.Relay.Requests.Count);

        harness.Clock.Advance(TimeSpan.FromMinutes(19));
        await harness.Sharing.RunCycleAsync();
        Assert.Equal(2, harness.Relay.Requests.Count);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Sharing.RunCycleAsync();
        Assert.Equal(3, harness.Relay.Requests.Count);
    }

    [Fact]
    public async Task A_relay_without_the_usage_route_is_retried_hourly()
    {
        var harness = new Harness();
        harness.Relay.Respond = _ => Task.FromResult(Status(HttpStatusCode.NotFound));
        await harness.Sharing.RunCycleAsync();

        harness.Clock.Advance(TimeSpan.FromMinutes(59));
        await harness.Sharing.RunCycleAsync();
        Assert.Single(harness.Relay.Requests);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Sharing.RunCycleAsync();
        Assert.Equal(2, harness.Relay.Requests.Count);
        Assert.Null(harness.Sharing.SnapshotStatus()!.Seq);
    }

    [Fact]
    public async Task A_missing_snapshot_waits_for_the_next_snapshot_upload()
    {
        var harness = new Harness();
        var calls = 0;
        harness.Relay.Respond = _ => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? Status(HttpStatusCode.Conflict, """{"error":"snapshot_missing"}""")
            : Status(HttpStatusCode.NoContent));

        await harness.Sharing.RunCycleAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Sharing.RunCycleAsync();
        Assert.Single(harness.Relay.Requests);

        harness.Sharing.NoteSnapshotPublished(harness.Sharing.SnapshotStatus());
        await harness.Relay.WaitForAsync(2);
        await harness.Queue.WhenIdleAsync();
        Assert.Equal(harness.Relay.Requests[1].Seq, harness.Sharing.SnapshotStatus()!.Seq);
    }

    [Fact]
    public async Task A_stale_write_raises_the_sequence_and_retries_next_cycle()
    {
        var harness = new Harness();
        var calls = 0;
        harness.Relay.Respond = _ => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? Status(HttpStatusCode.Conflict, """{"error":"stale_write"}""")
            : Status(HttpStatusCode.NoContent));

        await harness.Sharing.RunCycleAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        await harness.Sharing.RunCycleAsync();

        Assert.Equal(2, harness.Relay.Requests.Count);
        Assert.True(harness.Relay.Requests[1].Seq >= harness.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        Assert.True(harness.Relay.Requests[1].Seq > harness.Relay.Requests[0].Seq);
    }

    [Fact]
    public async Task A_successful_upload_asks_for_the_snapshot_to_publish_its_seq()
    {
        var harness = new Harness();
        Assert.Null(new Harness(SettingsWith(share: false)).Sharing.SnapshotStatus());
        Assert.Equal(new RemoteUsageShareStatus(true, null, null), harness.Sharing.SnapshotStatus());

        var refreshes = 0;
        harness.Sharing.SnapshotRefreshRequested += (_, _) => refreshes++;
        await harness.Sharing.RunCycleAsync();

        Assert.Equal(1, refreshes);
        Assert.Equal(harness.Relay.Requests[0].Seq, harness.Sharing.SnapshotStatus()!.Seq);
    }

    [Fact]
    public async Task Sharing_pauses_when_the_time_zone_has_no_iana_name()
    {
        var harness = new Harness(zone: TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3"));
        var changes = 0;
        harness.Sharing.StatusChanged += (_, _) => changes++;

        await harness.Sharing.RunCycleAsync();

        Assert.Empty(harness.Relay.Requests);
        Assert.Equal(RemoteUsageSharing.TimeZonePausedStatus, harness.Sharing.Status);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task The_write_queue_runs_writes_one_at_a_time_in_order()
    {
        var queue = new RelayWriteQueue();
        var order = new ConcurrentQueue<int>();
        var gate = new TaskCompletionSource();
        var first = queue.Enqueue(async () =>
        {
            await gate.Task;
            order.Enqueue(1);
            throw new InvalidOperationException("a failed write does not block the next");
        });
        var second = queue.Enqueue(() =>
        {
            order.Enqueue(2);
            return Task.FromResult(2);
        });

        await Task.Delay(50);
        Assert.Empty(order);
        gate.SetResult();
        Assert.Equal(2, await second);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.Equal([1, 2], order);
    }

    private static AppSettings SettingsWith(bool share) => new()
    {
        RemoteViewEnabled = true,
        RemoteViewShareUsage = share,
        RemoteViewId = WriteIdA,
        DefaultRemoteViewUploadUrl = Relay,
        DefaultRemoteViewPageUrl = "https://viewer.example.test"
    };

    private static HttpResponseMessage Status(HttpStatusCode status, string? body = null) =>
        new(status) { Content = body is null ? null : new StringContent(body) };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out.");
    }

    private sealed class Harness
    {
        public Harness(
            AppSettings? settings = null,
            TimeZoneInfo? zone = null,
            Func<int, Task<RemoteUsageSource>?>? compose = null,
            RemoteUsageLimits? limits = null)
        {
            Settings = settings ?? SharedSettings();
            Samples = [Sample(Start.AddDays(-3)), Sample(Start.AddDays(-2).AddHours(-3))];
            Relay = new FakeRelay(this);
            Queue = new RelayWriteQueue();
            var logger = new LoggerConfiguration().CreateLogger();
            var client = new RemoteRelayClient(new HttpClient(Relay), () => "signing-key", () => Clock.GetUtcNow(), () => logger);
            Sharing = new RemoteUsageSharing(
                Settings,
                _ =>
                {
                    Interlocked.Increment(ref _saves);
                    Interlocked.Exchange(ref _savedSeq, Settings.RemoteUsageLastSeq);
                    return Task.CompletedTask;
                },
                Queue,
                client,
                _ =>
                {
                    // A test may take over any compose by its number, from 1.
                    var number = Interlocked.Increment(ref _composes);
                    return compose?.Invoke(number) ??
                        Task.FromResult(new RemoteUsageSource([.. Samples], ModelPricingTable.Default, []));
                },
                () => zone ?? TimeZoneInfo.FindSystemTimeZoneById("Europe/London"),
                Clock,
                () => logger,
                limits,
                startCyclesOnChange: false);
        }

        private int _saves;
        private long _savedSeq;
        private int _composes;

        public AppSettings Settings { get; }

        public List<UsageSample> Samples { get; }

        public FakeClock Clock { get; } = new(Start);

        public FakeRelay Relay { get; }

        public RelayWriteQueue Queue { get; }

        public RemoteUsageSharing Sharing { get; }

        public int Saves => Volatile.Read(ref _saves);

        public long SavedSeq => Interlocked.Read(ref _savedSeq);

        public int Composes => Volatile.Read(ref _composes);

        public static AppSettings SharedSettings() => SettingsWith(share: true);

        public static UsageSample Sample(DateTimeOffset at) =>
            new(at, UsageProviderKind.Claude, "claude-1", "claude-sonnet-4-5", new UsageTokens { UncachedInputTokens = 1000, OutputTokens = 200 });
    }

    private sealed record SeenRequest(string Method, string Host, string Path, long? Seq, long SavedSeqWhenSent);

    private sealed class FakeRelay(Harness harness) : HttpMessageHandler
    {
        private readonly List<SeenRequest> _requests = [];

        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
            _ => Task.FromResult(Status(HttpStatusCode.NoContent));

        public IReadOnlyList<SeenRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public Task WaitForAsync(int count) => WaitUntilAsync(() => Requests.Count >= count);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            long? seq = null;
            if (request.Headers.TryGetValues(RemoteRelayClient.UsageSeqHeader, out var values))
            {
                seq = long.Parse(values.Single(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                seq = body.RootElement.GetProperty("seq").GetInt64();
            }

            lock (_requests)
            {
                _requests.Add(new SeenRequest(request.Method.Method, request.RequestUri!.Host, request.RequestUri.AbsolutePath, seq, harness.SavedSeq));
            }

            return await Respond(request);
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        /// <summary>Runs once, on the next clock read, to land a change at that exact point.</summary>
        public Action? OnNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var onRead = OnNextRead;
            OnNextRead = null;
            onRead?.Invoke();
            return _now;
        }
    }
}
