using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ResetCreditExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "Expired")]
    [InlineData(-1, "Expired")]
    [InlineData(1, "1 day left")]
    [InlineData(7, "7 days left")]
    [InlineData(18, "18 days left")]
    public void Remaining_days_are_explicit(int days, string expected) =>
        Assert.Equal(expected, ResetCreditExpiry.RemainingText(Now.AddDays(days), Now));

    [Fact]
    public void Notice_counts_only_unexpired_credits_through_the_seventh_day()
    {
        ResetCredit Credit(int day) => new(day.ToString(), "codexRateLimits", null, null, null, Now.AddDays(day));
        Assert.Equal("2 resets expire within 7 days.", ResetCreditExpiry.Notice([Credit(-1), Credit(0), Credit(1), Credit(7), Credit(8)], null, Now));
        Assert.Equal(string.Empty, ResetCreditExpiry.Notice([], Now.AddDays(1), Now));
        Assert.Equal("1 reset expires within 7 days.", ResetCreditExpiry.Notice(null, Now.AddDays(7), Now));
        Assert.Equal(string.Empty, ResetCreditExpiry.Notice(null, null, Now));
    }
}
