using costats.Core.Tray;
using Xunit;

namespace costats.Core.Tests.Tray;

public sealed class TrayAccountFilterTests
{
    [Theory]
    [InlineData("claude:work")]
    [InlineData("CODEX:personal")]
    [InlineData("claude")]
    public void Account_providers_are_always_visible(string providerId)
    {
        Assert.True(TrayAccountFilter.IsVisible(providerId, hasZaiKey: false, copilotEnabled: false));
    }

    [Fact]
    public void Zai_is_visible_only_with_a_key()
    {
        Assert.False(TrayAccountFilter.IsVisible("zai", hasZaiKey: false, copilotEnabled: true));
        Assert.True(TrayAccountFilter.IsVisible("zai", hasZaiKey: true, copilotEnabled: false));
    }

    [Fact]
    public void Copilot_is_visible_when_enabled()
    {
        Assert.False(TrayAccountFilter.IsVisible("copilot", hasZaiKey: true, copilotEnabled: false));
        Assert.True(TrayAccountFilter.IsVisible("copilot", hasZaiKey: false, copilotEnabled: true));
        Assert.True(TrayAccountFilter.IsVisible("Copilot:personal", hasZaiKey: false, copilotEnabled: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    public void Unknown_providers_are_hidden(string providerId)
    {
        Assert.False(TrayAccountFilter.IsVisible(providerId, hasZaiKey: true, copilotEnabled: true));
    }
}
