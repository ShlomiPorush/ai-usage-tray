using System.Text.Json;
using costats.Application.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

public sealed class RemoteViewDefaultsTests
{
    /// <summary>The test vector from remote/worker/README.md.</summary>
    private const string WriteId = "0123456789abcdef0123456789abcdef";
    private const string ReadId = "3eb1bd439947eb762998e566ccc2e099";

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
    public void Share_link_carries_the_derived_read_id_not_the_write_id()
    {
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewId = WriteId,
            DefaultRemoteViewPageUrl = "https://view.example.com/"
        };

        Assert.Equal($"https://view.example.com/?id={ReadId}", settings.RemoteViewShareLink);
        Assert.DoesNotContain(WriteId, settings.RemoteViewShareLink);
        Assert.Equal(ReadId, settings.RemoteViewReadId);
    }

    [Fact]
    public void Share_link_prefers_the_user_page_url()
    {
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewId = WriteId,
            RemoteViewPageUrl = "https://mine-page.example.com",
            DefaultRemoteViewPageUrl = "https://view.example.com"
        };

        Assert.Equal($"https://mine-page.example.com/?id={ReadId}", settings.RemoteViewShareLink);
    }

    [Theory]
    [InlineData(false, WriteId, "https://view.example.com")]
    [InlineData(true, null, "https://view.example.com")]
    [InlineData(true, "  ", "https://view.example.com")]
    [InlineData(true, WriteId, null)]
    // A hand-edited id that is not 32 lowercase hex characters cannot be hashed
    // into an id the worker stores under, so there is no link to show.
    [InlineData(true, "abc", "https://view.example.com")]
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

    [Theory]
    [InlineData("http://relay.example.com")]
    [InlineData("ftp://relay.example.com")]
    [InlineData("relay.example.com")]
    [InlineData("javascript:alert(1)")]
    public void A_non_https_override_is_ignored_and_the_default_is_used(string badUrl)
    {
        var settings = new AppSettings
        {
            RemoteViewUploadUrl = badUrl,
            RemoteViewPageUrl = badUrl,
            DefaultRemoteViewUploadUrl = "https://relay.example.com",
            DefaultRemoteViewPageUrl = "https://view.example.com"
        };

        Assert.Equal("https://relay.example.com", settings.EffectiveRemoteViewUploadUrl);
        Assert.Equal("https://view.example.com", settings.EffectiveRemoteViewPageUrl);
    }

    [Fact]
    public void A_non_https_override_with_no_default_leaves_remote_view_inert()
    {
        var settings = new AppSettings
        {
            RemoteViewUploadUrl = "http://relay.example.com",
            RemoteViewPageUrl = "http://view.example.com"
        };

        Assert.Null(settings.EffectiveRemoteViewUploadUrl);
        Assert.Null(settings.EffectiveRemoteViewPageUrl);
    }

    [Fact]
    public void A_loopback_override_is_accepted_over_plain_http()
    {
        var settings = new AppSettings
        {
            RemoteViewUploadUrl = "http://localhost:8787",
            RemoteViewPageUrl = "http://127.0.0.1:8787",
            DefaultRemoteViewUploadUrl = "https://relay.example.com",
            DefaultRemoteViewPageUrl = "https://view.example.com"
        };

        Assert.Equal("http://localhost:8787", settings.EffectiveRemoteViewUploadUrl);
        Assert.Equal("http://127.0.0.1:8787", settings.EffectiveRemoteViewPageUrl);
    }

    [Fact]
    public void A_non_https_shipped_default_does_not_count_as_a_default()
    {
        var settings = new AppSettings
        {
            DefaultRemoteViewUploadUrl = "http://relay.example.com",
            DefaultRemoteViewPageUrl = "https://view.example.com"
        };

        Assert.False(settings.HasRemoteViewDefaults);
        Assert.Null(settings.EffectiveRemoteViewUploadUrl);
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
        Assert.DoesNotContain("remoteViewReadId", json, StringComparison.OrdinalIgnoreCase);

        // The user's own override still round-trips.
        var restored = JsonSerializer.Deserialize<AppSettings>(json, options)!;
        Assert.Equal("https://mine.example.com", restored.RemoteViewUploadUrl);
        Assert.True(restored.RemoteViewEnabled);
        Assert.Null(restored.DefaultRemoteViewUploadUrl);
    }
}
