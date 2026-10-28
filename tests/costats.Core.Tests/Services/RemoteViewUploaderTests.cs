using System.Net;
using System.Net.Http;
using costats.App.Services;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Core.RemoteView;
using Serilog;
using Xunit;

namespace costats.Core.Tests.Services;

/// <summary>
/// A quota snapshot built before remote view was turned off, or before its
/// link changed, must never reach the relay after the snapshot was deleted.
/// </summary>
public sealed class RemoteViewUploaderTests
{
    private static readonly string WriteId = RemoteViewIds.MintWriteId();

    [Fact]
    public async Task A_snapshot_built_before_remote_view_was_turned_off_is_dropped_after_the_delete()
    {
        var (uploader, settings, requests) = Uploader();
        using var _ = uploader;

        // Built, then stalled before reaching the queue.
        var upload = uploader.PrepareSnapshotUpload(State(), DateTimeOffset.UtcNow);
        settings.RemoteViewEnabled = false;
        Assert.True(await uploader.DeleteAsync(WriteId));
        await uploader.EnqueueSnapshotUpload(upload);
        await uploader.Queue.WhenIdleAsync();

        Assert.Equal([$"DELETE /u/{WriteId}"], requests);
    }

    [Fact]
    public async Task A_snapshot_built_for_a_retired_link_is_dropped()
    {
        var (uploader, settings, requests) = Uploader();
        using var _ = uploader;

        var upload = uploader.PrepareSnapshotUpload(State(), DateTimeOffset.UtcNow);
        settings.RemoteViewId = RemoteViewIds.MintWriteId();
        await uploader.EnqueueSnapshotUpload(upload);

        Assert.Empty(requests);
    }

    [Fact]
    public async Task A_snapshot_whose_remote_view_is_unchanged_is_sent()
    {
        var (uploader, _, requests) = Uploader();
        using var _ = uploader;

        await uploader.EnqueueSnapshotUpload(uploader.PrepareSnapshotUpload(State(), DateTimeOffset.UtcNow));

        Assert.Equal([$"PUT /u/{WriteId}"], requests);
    }

    private static (RemoteViewUploader Uploader, AppSettings Settings, List<string> Requests) Uploader()
    {
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewId = WriteId,
            DefaultRemoteViewUploadUrl = "https://relay.example.test",
            DefaultRemoteViewPageUrl = "https://viewer.example.test"
        };
        var requests = new List<string>();
        var handler = new Handler(request =>
        {
            lock (requests)
            {
                requests.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var uploader = new RemoteViewUploader(
            new Orchestrator(),
            [],
            new NoAccountSources(),
            settings,
            handler,
            new LoggerConfiguration().CreateLogger());
        return (uploader, settings, requests);
    }

    private static PulseState State()
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new UsagePulse("claude:claude-1", now, 40, 100, 20, 100, null, null);
        var reading = new ProviderReading(
            usage,
            new IdentityCard("claude:claude-1", "Claude", null, null, "Max", "test"),
            "ok",
            now,
            ReadingConfidence.High,
            ReadingSource.Api,
            ProviderAuthenticationState.Authenticated);
        return new PulseState(
            new Dictionary<string, ProviderReading> { ["claude:claude-1"] = reading },
            now,
            [],
            false,
            RefreshTrigger.Scheduled);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class NoAccountSources : IAccountSourceRegistry
    {
        public IReadOnlyList<ISignalSource> Current => [];

        public void Reload()
        {
        }
    }

    private sealed class Orchestrator : IPulseOrchestrator
    {
        public IObservable<PulseState> PulseStream { get; } = new PulseBroadcaster();
        public PulseState? CurrentState => null;
        public Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshIfStaleAsync(TimeSpan maxAge, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ProviderRefreshResult> RefreshProviderForVerificationAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderRefreshResult.Failure());
        public bool IsFullRefreshStale(TimeSpan maxAge) => false;
        public void RepublishLastState()
        {
        }
        public void UpdateRefreshInterval(TimeSpan interval)
        {
        }
    }
}
