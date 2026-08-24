using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace costats.Application.SessionActivation;

/// <summary>Checks close to the reset boundary without changing normal usage polling.</summary>
public sealed class SessionAutoStartWorker : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly SessionAutoStartCoordinator _coordinator;
    private readonly ILogger<SessionAutoStartWorker> _logger;

    public SessionAutoStartWorker(
        SessionAutoStartCoordinator coordinator,
        ILogger<SessionAutoStartWorker> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _coordinator.CheckOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Automatic five-hour window check failed");
            }

            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
