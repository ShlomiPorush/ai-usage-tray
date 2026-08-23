using costats.Application.Pulse;
using costats.Core.Pulse;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace costats.Core.Tests.Pulse;

public sealed class SourceSelectorTests
{
    private static readonly ProviderProfile Profile = new("claude:work", "Claude Work", "#FF7A00");

    [Fact]
    public async Task SelectAsync_returns_the_reading_with_the_highest_confidence()
    {
        var selector = new SourceSelector(NullLogger<SourceSelector>.Instance);

        var weak = Reading(ReadingConfidence.Low, "weak");
        var strong = Reading(ReadingConfidence.High, "strong");

        var result = await selector.SelectAsync(
            "claude:work",
            [new StubSource(_ => Task.FromResult(weak)), new StubSource(_ => Task.FromResult(strong))],
            CancellationToken.None);

        Assert.Equal("strong", result.StatusSummary);
    }

    [Fact]
    public async Task SelectAsync_converts_a_failing_source_into_a_no_data_reading()
    {
        var selector = new SourceSelector(NullLogger<SourceSelector>.Instance);

        var result = await selector.SelectAsync(
            "claude:work",
            [new StubSource(_ => throw new InvalidOperationException("boom"))],
            CancellationToken.None);

        Assert.Equal("No data", result.StatusSummary);
        Assert.Equal(ReadingConfidence.Unknown, result.Confidence);
    }

    [Fact]
    public async Task SelectAsync_still_reports_no_data_when_a_source_fails_while_another_token_is_cancelled()
    {
        // An unrelated cancelled token must not change how real failures are handled.
        var selector = new SourceSelector(NullLogger<SourceSelector>.Instance);

        var result = await selector.SelectAsync(
            "claude:work",
            [
                new StubSource(_ => throw new InvalidOperationException("boom")),
                new StubSource(_ => throw new OperationCanceledException(new CancellationToken(canceled: true)))
            ],
            CancellationToken.None);

        Assert.Equal("No data", result.StatusSummary);
    }

    [Fact]
    public async Task SelectAsync_rethrows_cancellation_of_the_refresh_token()
    {
        var selector = new SourceSelector(NullLogger<SourceSelector>.Instance);
        using var cts = new CancellationTokenSource();

        var sources = new ISignalSource[]
        {
            new StubSource(_ => Task.FromResult(Reading(ReadingConfidence.High, "good"))),
            new StubSource(token =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Reading(ReadingConfidence.High, "never"));
            })
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => selector.SelectAsync("claude:work", sources, cts.Token));
    }

    private static ProviderReading Reading(ReadingConfidence confidence, string summary) => new(
        Usage: null,
        Identity: null,
        StatusSummary: summary,
        CapturedAt: DateTimeOffset.UnixEpoch,
        Confidence: confidence,
        Source: ReadingSource.Unknown);

    private sealed class StubSource : ISignalSource
    {
        private readonly Func<CancellationToken, Task<ProviderReading>> _read;

        public StubSource(Func<CancellationToken, Task<ProviderReading>> read)
        {
            _read = read;
        }

        public ProviderProfile Profile => SourceSelectorTests.Profile;

        public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken) => _read(cancellationToken);
    }
}
