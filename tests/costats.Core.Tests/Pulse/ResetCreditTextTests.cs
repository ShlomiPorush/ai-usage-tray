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
    public void A_known_expiry_is_appended_as_a_short_date()
    {
        Assert.Equal(
            "1 usage limit reset available, expires Sep 20",
            UsageFormatter.ResetCreditsLine(1, new DateOnly(2026, 9, 20)));
    }

    [Fact]
    public void Nothing_to_redeem_produces_no_text()
    {
        Assert.Equal(string.Empty, UsageFormatter.ResetCreditsLine(0, new DateOnly(2026, 9, 20)));
        Assert.Equal(string.Empty, UsageFormatter.ResetCreditsChip(0));
    }
}
