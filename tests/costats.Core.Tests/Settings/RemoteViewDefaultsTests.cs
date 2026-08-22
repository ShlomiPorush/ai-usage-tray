using System.Text.Json;
using costats.Application.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

public sealed class RemoteViewDefaultsTests
{
    [Fact]
    public void User_url_wins_over_the_shipped_default()
    {
        var settings = new AppSettings
        {
            RemoteViewUploadUrl = "https://mine.example.com",
            RemoteViewPageUrl = "https://mine-page.example.com",
            DefaultRemoteViewUploadUrl = "https://relay.example.com",
            DefaultRemoteViewPageUrl = "https://view.example.com"
        };

        Assert.Equal("https://mine.example.com", settings.EffectiveRemoteViewUploadUrl);
        Assert.Equal("https://mine-page.example.com", settings.EffectiveRemoteViewPageUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Default_is_used_when_the_user_value_is_missing_or_blank(string? userValue)
    {
        var settings = new AppSettings
        {
            RemoteViewUploadUrl = userValue,
            RemoteViewPageUrl = userValue,
            DefaultRemoteViewUploadUrl = "https://relay.example.com",
            DefaultRemoteViewPageUrl = "https://view.example.com"
        };

        Assert.Equal("https://relay.example.com", settings.EffectiveRemoteViewUploadUrl);
        Assert.Equal("https://view.example.com", settings.EffectiveRemoteViewPageUrl);
    }

    [Fact]
    public void No_user_value_and_no_default_resolves_to_null()
    {
        var settings = new AppSettings();

        Assert.Null(settings.EffectiveRemoteViewUploadUrl);
        Assert.Null(settings.EffectiveRemoteViewPageUrl);
        Assert.False(settings.HasRemoteViewDefaults);
    }

    [Theory]
    [InlineData("https://relay.example.com", "https://view.example.com", true)]
    [InlineData("https://relay.example.com", null, false)]
    [InlineData(null, "https://view.example.com", false)]
    [InlineData("  ", "https://view.example.com", false)]
    [InlineData(null, null, false)]
    public void HasRemoteViewDefaults_requires_both_urls(string? uploadUrl, string? pageUrl, bool expected)
    {
        var settings = new AppSettings
        {
            DefaultRemoteViewUploadUrl = uploadUrl,
            DefaultRemoteViewPageUrl = pageUrl
        };

        Assert.Equal(expected, settings.HasRemoteViewDefaults);
    }

    [Fact]
    public void Share_link_combines_the_effective_page_url_with_the_id()
    {
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewId = "0123456789abcdef0123456789abcdef",
            DefaultRemoteViewPageUrl = "https://view.example.com/"
        };

        Assert.Equal(
            "https://view.example.com/?id=0123456789abcdef0123456789abcdef",
            settings.RemoteViewShareLink);
    }

    [Fact]
    public void Share_link_prefers_the_user_page_url()
    {
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewId = "abc",
            RemoteViewPageUrl = "https://mine-page.example.com",
            DefaultRemoteViewPageUrl = "https://view.example.com"
        };

        Assert.Equal("https://mine-page.example.com/?id=abc", settings.RemoteViewShareLink);
    }

    [Theory]
    [InlineData(false, "abc", "https://view.example.com")]
    [InlineData(true, null, "https://view.example.com")]
    [InlineData(true, "  ", "https://view.example.com")]
    [InlineData(true, "abc", null)]
    public void Share_link_is_null_until_remote_view_is_on_and_configured(
        bool enabled, string? id, string? pageUrl)
    {
        var settings = new AppSettings
        {
            RemoteViewEnabled = enabled,
            RemoteViewId = id,
            DefaultRemoteViewPageUrl = pageUrl
        };

        Assert.Null(settings.RemoteViewShareLink);
    }

    [Fact]
    public void Shipped_defaults_are_never_written_to_the_settings_file()
    {
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewUploadUrl = "https://mine.example.com",
            DefaultRemoteViewUploadUrl = "https://relay.example.com",
            DefaultRemoteViewPageUrl = "https://view.example.com"
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(settings, options);

        Assert.DoesNotContain("defaultRemoteViewUploadUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("defaultRemoteViewPageUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effectiveRemoteView", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasRemoteViewDefaults", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remoteViewShareLink", json, StringComparison.OrdinalIgnoreCase);

        // The user's own override still round-trips.
        var restored = JsonSerializer.Deserialize<AppSettings>(json, options)!;
        Assert.Equal("https://mine.example.com", restored.RemoteViewUploadUrl);
        Assert.True(restored.RemoteViewEnabled);
        Assert.Null(restored.DefaultRemoteViewUploadUrl);
    }
}
