using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Pulse;

public sealed class ResetCreditTextTests
{
    [Fact]
    public void One_reset_reads_in_the_singular()
    {
        Assert.Equal("1 usage limit reset available", UsageFormatter.ResetCreditsLine(1, null));
        Assert.Equal("reset", UsageFormatter.ResetCreditsChip(1));
    }

    [Fact]
    public void More_than_one_reset_reads_in_the_plural_with_the_count()
    {
        Assert.Equal("2 usage limit resets available", UsageFormatter.ResetCreditsLine(2, null));
        Assert.Equal("2 resets", UsageFormatter.ResetCreditsChip(2));
    }

    [Fact]
    public void A_known_expiry_is_appended_as_a_day_countdown()
    {
        var today = new DateOnly(2026, 9, 15);

        Assert.Equal(
            "1 usage limit reset available, expires in 5 days",
            UsageFormatter.ResetCreditsLine(1, new DateOnly(2026, 9, 20), today));
        Assert.Equal(
            "2 usage limit resets available, expires in 30 days",
            UsageFormatter.ResetCreditsLine(2, new DateOnly(2026, 10, 15), today));
    }

    [Fact]
    public void Today_and_tomorrow_read_in_words_and_the_singular()
    {
        var today = new DateOnly(2026, 9, 15);

        Assert.Equal(
            "1 usage limit reset available, expires today",
            UsageFormatter.ResetCreditsLine(1, today, today));
        Assert.Equal(
            "1 usage limit reset available, expires in 1 day",
            UsageFormatter.ResetCreditsLine(1, new DateOnly(2026, 9, 16), today));
    }

    [Fact]
    public void An_expiry_already_in_the_past_is_left_out()
    {
        Assert.Equal(
            "1 usage limit reset available",
            UsageFormatter.ResetCreditsLine(1, new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15)));
    }

    [Fact]
    public void Nothing_to_redeem_produces_no_text()
    {
        Assert.Equal(string.Empty, UsageFormatter.ResetCreditsLine(0, new DateOnly(2026, 9, 20)));
        Assert.Equal(string.Empty, UsageFormatter.ResetCreditsChip(0));
    }
}
