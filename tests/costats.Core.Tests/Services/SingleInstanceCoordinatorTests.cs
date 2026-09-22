using System.IO;
using System.IO.Pipes;
using costats.App.Services;
using Xunit;

namespace costats.Core.Tests.Services;

/// <summary>
/// Covers the hardened single-instance hand-off. These tests drive the pipe
/// server and client directly with unique names, so they never touch the real
/// process-wide mutex and can run in parallel with a live app.
/// </summary>
public class SingleInstanceCoordinatorTests
{
    private static string UniquePipeName() => $"costats-test-pipe-{Guid.NewGuid():N}";

    /// <summary>
    /// The normal case: a genuine second launch hands off to a listening primary,
    /// which then receives the activation. Because both ends are built with
    /// CurrentUserOnly, a successful same-user round-trip also proves that option
    /// does not block the legitimate path.
    /// </summary>
    [Fact]
    public async Task Handoff_ToListeningPrimary_Succeeds_AndPrimaryReceivesActivation()
    {
        var pipeName = UniquePipeName();
        using var listenerCts = new CancellationTokenSource();
        var received = new TaskCompletionSource<ActivationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var listener = SingleInstanceCoordinator.RunListenerAsync(
            pipeName,
            message =>
            {
                received.TrySetResult(message);
                return Task.CompletedTask;
            },
            SingleInstanceCoordinator.ReadTimeout,
            listenerCts.Token);

        try
        {
            var handedOff = await SingleInstanceCoordinator.TryHandoffToPrimaryAsync(
                pipeName,
                ActivationMessage.ShowWidget,
                TimeSpan.FromSeconds(5));

            Assert.True(handedOff);

            var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ActivationMessage.ShowWidget, delivered);
        }
        finally
        {
            listenerCts.Cancel();
            await AwaitListenerAsync(listener);
        }
    }

    /// <summary>
    /// Fix 1: when the mutex name is held but no real primary is listening on the
    /// pipe (a squatter, or a primary that died without releasing the mutex), the
    /// hand-off must fail so the caller keeps running instead of exiting silently.
    /// </summary>
    [Fact]
    public async Task Handoff_WithNoListener_Fails_SoCallerCanRun()
    {
        var handedOff = await SingleInstanceCoordinator.TryHandoffToPrimaryAsync(
            UniquePipeName(),
            ActivationMessage.ShowWidget,
            TimeSpan.FromMilliseconds(500));

        Assert.False(handedOff);
    }

    /// <summary>
    /// A client that connects with CurrentUserOnly reaches a server that also
    /// sets CurrentUserOnly. Cross-user rejection needs two OS users and cannot be
    /// asserted here; this proves the same-user contract both ends share is met.
    /// </summary>
    [Fact]
    public async Task CurrentUserOnlyClient_ConnectsToCurrentUserOnlyServer()
    {
        var pipeName = UniquePipeName();
        using var listenerCts = new CancellationTokenSource();
        var received = new TaskCompletionSource<ActivationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var listener = SingleInstanceCoordinator.RunListenerAsync(
            pipeName,
            message =>
            {
                received.TrySetResult(message);
                return Task.CompletedTask;
            },
            SingleInstanceCoordinator.ReadTimeout,
            listenerCts.Token);

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await client.ConnectAsync(5000);
            Assert.True(client.IsConnected);

            using (var writer = new StreamWriter(client) { AutoFlush = true })
            {
                await writer.WriteLineAsync(ActivationMessage.ShowWidget.ToString());
            }

            var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ActivationMessage.ShowWidget, delivered);
        }
        finally
        {
            listenerCts.Cancel();
            await AwaitListenerAsync(listener);
        }
    }

    /// <summary>
    /// Fix 2: a client that connects but never sends a complete line must not wedge
    /// the single-instance listener. After the bounded read elapses the loop drops
    /// the stalled client and accepts the next activation.
    /// </summary>
    [Fact]
    public async Task StalledClient_DoesNotBlock_SubsequentActivation()
    {
        var pipeName = UniquePipeName();
        using var listenerCts = new CancellationTokenSource();
        var received = new TaskCompletionSource<ActivationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var listener = SingleInstanceCoordinator.RunListenerAsync(
            pipeName,
            message =>
            {
                received.TrySetResult(message);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(300),
            listenerCts.Token);

        try
        {
            // Connect but never send a full line: the read must time out.
            using (var stalled = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await stalled.ConnectAsync(5000);
                Assert.True(stalled.IsConnected);

                // The next activation succeeds only because the listener recovered
                // from the stalled connection within the bounded read window.
                var handedOff = await SingleInstanceCoordinator.TryHandoffToPrimaryAsync(
                    pipeName,
                    ActivationMessage.ShowWidget,
                    TimeSpan.FromSeconds(5));

                Assert.True(handedOff);
            }

            var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ActivationMessage.ShowWidget, delivered);
        }
        finally
        {
            listenerCts.Cancel();
            await AwaitListenerAsync(listener);
        }
    }

    private static async Task AwaitListenerAsync(Task listener)
    {
        try
        {
            await listener;
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation.
        }
    }
}
