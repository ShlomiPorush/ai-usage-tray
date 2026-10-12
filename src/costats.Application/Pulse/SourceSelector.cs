using System.Collections.Concurrent;
using System.Diagnostics;
using costats.Application.Diagnostics;
using costats.Core.Pulse;
using Microsoft.Extensions.Logging;

namespace costats.Application.Pulse;

public sealed class SourceSelector : ISourceSelector
{
    private readonly ILogger<SourceSelector> _logger;

    /// <summary>Last state logged per provider id, so only changes are logged.</summary>
    private readonly ConcurrentDictionary<string, AccountReadState> _lastStates = new(StringComparer.OrdinalIgnoreCase);

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
            var sourceName = source.GetType().Name;
            var stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("Reading {ProviderId} through {Source}", providerId, sourceName);
            try
            {
                var reading = await source.ReadAsync(cancellationToken);
                _logger.LogDebug(
                    "Read {ProviderId} through {Source} in {ElapsedMs} ms: {Outcome} ({Status})",
                    providerId,
                    sourceName,
                    stopwatch.ElapsedMilliseconds,
                    Classify(reading),
                    reading?.StatusSummary);
                return reading;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A cancelled refresh is not a source failure. Swallowing it here
                // would turn every source into null and publish an empty "No data"
                // reading over the last good state.
                _logger.LogDebug(
                    "Read {ProviderId} through {Source} cancelled after {ElapsedMs} ms",
                    providerId,
                    sourceName,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Read {ProviderId} through {Source} failed after {ElapsedMs} ms: {ErrorKind}: {Error}",
                    providerId,
                    sourceName,
                    stopwatch.ElapsedMilliseconds,
                    ex is OperationCanceledException or TimeoutException ? "timeout" : ex.GetType().Name,
                    LogRedaction.Redact(ex.Message));
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

        var selected = best ?? new ProviderReading(
            Usage: null,
            Identity: null,
            StatusSummary: "No data",
            CapturedAt: DateTimeOffset.UtcNow,
            Confidence: ReadingConfidence.Unknown,
            Source: ReadingSource.Unknown);

        LogStateTransition(providerId, Classify(selected));
        return selected;
    }

    /// <summary>
    /// How a reading looks to the user: connected with usage, sign-in
    /// required, or no data. Never includes identity details.
    /// </summary>
    internal static AccountReadState Classify(ProviderReading? reading) => reading switch
    {
        null => AccountReadState.Unavailable,
        { AuthenticationState: ProviderAuthenticationState.SignInRequired } => AccountReadState.SignInRequired,
        { Usage: not null } => AccountReadState.Connected,
        _ => AccountReadState.NoData
    };

    private void LogStateTransition(string providerId, AccountReadState state)
    {
        var previous = _lastStates.TryGetValue(providerId, out var last) ? last : (AccountReadState?)null;
        if (previous == state)
        {
            return;
        }

        _lastStates[providerId] = state;
        _logger.LogDebug(
            "Account {ProviderId} state: {Previous} -> {Current}",
            providerId,
            previous?.ToString() ?? "(first read)",
            state);
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

/// <summary>Coarse account state used for diagnostics.</summary>
public enum AccountReadState
{
    Connected,
    SignInRequired,
    NoData,
    Unavailable
}
