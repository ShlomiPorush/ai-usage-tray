using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using Serilog;

namespace costats.App.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    /// <summary>
    /// Upper bound on how long a connected client may take to deliver a full
    /// activation line before the listener drops it and accepts the next one.
    /// Prevents a hung (or hostile) same-user client from holding the single
    /// pipe instance open and starving genuine activations.
    /// </summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;

    public SingleInstanceCoordinator(string appId)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "default";
        PipeName = $"{appId}.pipe.{sid}";
        var mutexName = $"Global\\{appId}.mutex.{sid}";
        _mutex = new Mutex(true, mutexName, out var createdNew);
        IsPrimary = createdNew;
    }

    /// <summary>
    /// True when this process created the single-instance mutex. False means the
    /// name was already held: usually a genuine primary is running, but it can
    /// also be a squatter, so a false result alone must not decide shutdown.
    /// Use <see cref="TryHandoffToPrimaryAsync"/> to confirm a real primary.
    /// </summary>
    public bool IsPrimary { get; }

    public string PipeName { get; }

    public Task StartListenerAsync(Func<ActivationMessage, Task> onActivation, CancellationToken cancellationToken)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        _listenerTask = Task.Run(() => RunListenerAsync(PipeName, onActivation, ReadTimeout, linkedCts.Token), linkedCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Serves activation messages on <paramref name="pipeName"/> until cancelled.
    /// The server pipe is created with <see cref="PipeOptions.CurrentUserOnly"/>
    /// so only the current user can connect, and every accepted connection is
    /// read under <paramref name="readTimeout"/> so a stalled client cannot block
    /// the accept loop.
    /// </summary>
    internal static async Task RunListenerAsync(
        string pipeName,
        Func<ActivationMessage, Task> onActivation,
        TimeSpan readTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    string? line;
                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        readCts.CancelAfter(readTimeout);
                        using var reader = new StreamReader(server);
                        try
                        {
                            line = await reader.ReadLineAsync().WaitAsync(readCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // The connected client did not deliver a full line in time.
                            // Drop it and go back to accepting so it cannot wedge the loop.
                            Log.Warning(
                                "Named pipe read timed out after {Seconds}s; dropping the connection",
                                readTimeout.TotalSeconds);
                            continue;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(line) &&
                        Enum.TryParse(line, ignoreCase: true, out ActivationMessage message))
                    {
                        await onActivation(message).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Named pipe listener error");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Named pipe listener crashed");
            throw;
        }
    }

    /// <summary>
    /// Tries to hand <paramref name="message"/> to a primary instance listening on
    /// <paramref name="pipeName"/>. Returns true only when a listener accepted the
    /// activation. A false result means no real primary answered (for example the
    /// mutex name was squatted by a process that is not this app, or a former
    /// primary died without releasing it), so the caller should keep running
    /// instead of exiting silently.
    /// </summary>
    public static async Task<bool> TryHandoffToPrimaryAsync(string pipeName, ActivationMessage message, TimeSpan timeout)
    {
        try
        {
            await SignalPrimaryAsync(pipeName, message, timeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Hand-off to primary instance failed on pipe {PipeName}", pipeName);
            return false;
        }
    }

    public static async Task SignalPrimaryAsync(string pipeName, ActivationMessage message, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        using var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync(message.ToString()).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown failures.
        }

        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore release failures.
            }
        }

        _mutex.Dispose();
        _cts.Dispose();
    }
}
