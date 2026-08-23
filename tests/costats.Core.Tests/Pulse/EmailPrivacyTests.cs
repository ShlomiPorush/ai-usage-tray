using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Pulse;

public sealed class EmailPrivacyTests
{
    [Theory]
    [InlineData("shlomi@example.com", "s\u2022\u2022\u2022\u2022\u2022@\u2022\u2022\u2022\u2022\u2022.com")]
    [InlineData(" a@b.co ", "a\u2022\u2022\u2022\u2022\u2022@\u2022\u2022\u2022\u2022\u2022.co")]
    public void Mask_preserves_only_the_first_character_and_top_level_domain(string email, string expected)
    {
        Assert.Equal(expected, EmailPrivacy.Mask(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mask_returns_empty_for_missing_email(string? email)
    {
        Assert.Equal(string.Empty, EmailPrivacy.Mask(email));
    }

    [Fact]
    public void Mask_hides_an_unrecognized_address_completely()
    {
        var masked = EmailPrivacy.Mask("not-an-email");

        Assert.DoesNotContain("not", masked);
        Assert.Equal(12, masked.Length);
    }
}
