using costats.Core.Pulse;
using Microsoft.Extensions.Logging;

namespace costats.Application.Pulse;

public sealed class SourceSelector : ISourceSelector
{
    private readonly ILogger<SourceSelector> _logger;

    public SourceSelector(ILogger<SourceSelector> logger)
    {
        _logger = logger;
    }

    public async Task<ProviderReading> SelectAsync(
        string providerId,
        IReadOnlyList<ISignalSource> sources,
        CancellationToken cancellationToken)
    {
        // Fetch all sources in parallel for performance
        var readTasks = sources.Select(async source =>
        {
            try
            {
                return await source.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A cancelled refresh is not a source failure. Swallowing it here
                // would turn every source into null and publish an empty "No data"
                // reading over the last good state.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source {Source} failed for {ProviderId}", source.GetType().Name, providerId);
                return null;
            }
        });

        var readings = await Task.WhenAll(readTasks);

        // Select the best reading from all successful results
        ProviderReading? best = null;
        foreach (var reading in readings)
        {
            if (reading is not null && (best is null || IsBetter(reading, best)))
            {
                best = reading;
            }
        }

        return best ?? new ProviderReading(
            Usage: null,
            Identity: null,
            StatusSummary: "No data",
            CapturedAt: DateTimeOffset.UtcNow,
            Confidence: ReadingConfidence.Unknown,
            Source: ReadingSource.Unknown);
    }

    private static bool IsBetter(ProviderReading candidate, ProviderReading current)
    {
        if (candidate.Confidence != current.Confidence)
        {
            return candidate.Confidence > current.Confidence;
        }

        return candidate.CapturedAt > current.CapturedAt;
    }
}
