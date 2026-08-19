using costats.Core.Pulse;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ZaiUsageSourceTests
{
    [Fact]
    public async Task ReadAsync_reports_not_configured_when_no_keys_and_no_data()
    {
        var source = new ZaiUsageSource(new StubClient(null), () => null, () => null, () => "GLM");

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.Equal(ReadingConfidence.Low, reading.Confidence);
        Assert.Contains("not configured", reading.StatusSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("GLM", reading.Identity?.DisplayName);
        Assert.Equal("zai", reading.Identity?.ProviderId);
    }

    [Fact]
    public async Task ReadAsync_converts_session_and_weekly_remaining_into_used_percentages()
    {
        var source = new ZaiUsageSource(
            new StubClient(new ZaiUsageSnapshot(
                SessionRemainingPercent: 73,
                SessionResetsAt: DateTimeOffset.Parse("2026-08-09T18:00:00Z"),
                SessionWindow: TimeSpan.FromHours(5),
                WeeklyRemainingPercent: 41,
                WeeklyResetsAt: DateTimeOffset.Parse("2026-08-15T18:00:00Z"),
                WeeklyWindow: TimeSpan.FromDays(7),
                PlanName: "GLM Coding Plan",
                FetchedAt: DateTimeOffset.UtcNow)),
            () => "test-key",
            () => null,
            () => "GLM");

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.NotNull(reading.Usage);
        Assert.Equal(27, reading.Usage!.SessionUsed); // 100 - 73
        Assert.Equal(59, reading.Usage.WeekUsed);     // 100 - 41
        Assert.Equal(100, reading.Usage.SessionLimit);
        Assert.Equal(100, reading.Usage.WeekLimit);
        Assert.Equal(DateTimeOffset.Parse("2026-08-09T18:00:00Z").ToUniversalTime(),
            reading.Usage.SessionWindow?.ResetsAt?.ToUniversalTime());
        Assert.Equal(DateTimeOffset.Parse("2026-08-15T18:00:00Z").ToUniversalTime(),
            reading.Usage.WeekWindow?.ResetsAt?.ToUniversalTime());
        Assert.Equal(ReadingConfidence.High, reading.Confidence);
        Assert.Contains("GLM Coding Plan", reading.StatusSummary);
    }

    [Fact]
    public async Task ReadAsync_falls_back_to_five_hour_window_when_response_omits_duration()
    {
        var source = new ZaiUsageSource(
            new StubClient(new ZaiUsageSnapshot(
                SessionRemainingPercent: 50,
                SessionResetsAt: null,
                SessionWindow: null,
                WeeklyRemainingPercent: null,
                WeeklyResetsAt: null,
                WeeklyWindow: null,
                PlanName: null,
                FetchedAt: DateTimeOffset.UtcNow)),
            () => "test-key",
            () => null,
            () => "GLM");

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.NotNull(reading.Usage);
        Assert.NotNull(reading.Usage!.SessionWindow);
        Assert.Equal(TimeSpan.FromHours(5), reading.Usage.SessionWindow!.Duration);
    }

    private sealed class StubClient(ZaiUsageSnapshot? snapshot) : IZaiUsageClient
    {
        public Task<ZaiUsageSnapshot?> FetchAsync(
            string? codingApiKey,
            string? standardApiKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }
}