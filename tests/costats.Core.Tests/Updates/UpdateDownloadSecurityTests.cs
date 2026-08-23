using System.IO;
using costats.App.Services.Updates;
using Xunit;

namespace costats.Core.Tests.Updates;

/// <summary>
/// Guards the update download path: only GitHub over HTTPS, and never an
/// unverified archive.
/// </summary>
public class UpdateDownloadSecurityTests
{
    [Theory]
    [InlineData("https://github.com/ShlomiPorush/ai-usage-tray/releases/download/v2.0.2/ai-usage-tray-win-x64-v2.0.2.zip")]
    [InlineData("https://api.github.com/repos/ShlomiPorush/ai-usage-tray/releases/latest")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2")]
    [InlineData("https://release-assets.githubusercontent.com/releases/assets/1")]
    [InlineData("https://GITHUB.COM/ShlomiPorush/ai-usage-tray/releases/download/v1/x.zip")]
    public void IsTrustedUrl_AcceptsGitHubOverHttps(string url)
    {
        Assert.True(StartupUpdateCoordinator.IsTrustedUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://github.com/ShlomiPorush/ai-usage-tray/releases/download/v1/x.zip")]
    [InlineData("ftp://github.com/x.zip")]
    [InlineData("file:///C:/x.zip")]
    [InlineData("https://evil.example/x.zip")]
    [InlineData("https://github.com.evil.example/x.zip")]
    [InlineData("https://githubusercontent.com.evil.example/x.zip")]
    [InlineData("https://notgithubusercontent.com/x.zip")]
    [InlineData("/relative/path.zip")]
    public void IsTrustedUrl_RejectsEverythingElse(string? url)
    {
        Assert.False(StartupUpdateCoordinator.IsTrustedUrl(url));
    }

    [Theory]
    [InlineData("ShlomiPorush/ai-usage-tray")]
    [InlineData("owner.name/repo_name-1")]
    public void IsValidRepositoryName_AcceptsOwnerSlashName(string repository)
    {
        Assert.True(StartupUpdateCoordinator.IsValidRepositoryName(repository));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/repo?x=1")]
    [InlineData("../../evil")]
    [InlineData("evil.example/repo/../..")]
    public void IsValidRepositoryName_RejectsAnythingElse(string? repository)
    {
        Assert.False(StartupUpdateCoordinator.IsValidRepositoryName(repository));
    }

    [Fact]
    public void EnsureChecksumMatches_ThrowsWhenNoChecksumWasPublished()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => StartupUpdateCoordinator.EnsureChecksumMatches(null, new string('a', 64), "package.zip"));

        Assert.Contains("package.zip", error.Message);
    }

    [Fact]
    public void EnsureChecksumMatches_ThrowsWhenChecksumIsBlank()
    {
        Assert.Throws<InvalidDataException>(
            () => StartupUpdateCoordinator.EnsureChecksumMatches("   ", new string('a', 64), "package.zip"));
    }

    [Fact]
    public void EnsureChecksumMatches_ThrowsOnMismatch()
    {
        Assert.Throws<InvalidDataException>(
            () => StartupUpdateCoordinator.EnsureChecksumMatches(new string('a', 64), new string('b', 64), "package.zip"));
    }

    [Fact]
    public void EnsureChecksumMatches_AcceptsCaseAndWhitespaceDifferences()
    {
        var expected = new string('A', 64);
        var actual = new string('a', 64);

        StartupUpdateCoordinator.EnsureChecksumMatches($" {expected} ", actual, "package.zip");
    }

    [Fact]
    public void ExtractChecksum_ReadsTheReleaseFormat()
    {
        var hash = new string('c', 64);
        var text = $"{hash}  ai-usage-tray-win-x64-v2.0.2.zip";

        Assert.Equal(hash, StartupUpdateCoordinator.ExtractChecksum(text, "ai-usage-tray-win-x64-v2.0.2.zip"));
    }

    [Fact]
    public void ExtractChecksum_ReadsABareHash()
    {
        var hash = new string('d', 64);

        Assert.Equal(hash, StartupUpdateCoordinator.ExtractChecksum($"{hash}\n", "anything.zip"));
    }

    [Fact]
    public void ExtractChecksum_PicksTheMatchingNameFromAChecksumList()
    {
        var other = new string('e', 64);
        var wanted = new string('f', 64);
        var text = $"{other} *ai-usage-tray-win-arm64-v2.0.2.zip\n{wanted} *ai-usage-tray-win-x64-v2.0.2.zip\n";

        Assert.Equal(wanted, StartupUpdateCoordinator.ExtractChecksum(text, "ai-usage-tray-win-x64-v2.0.2.zip"));
    }

    [Fact]
    public void ExtractChecksum_ReturnsNullWhenTheNameDoesNotMatch()
    {
        var text = $"{new string('a', 64)}  some-other-package.zip";

        Assert.Null(StartupUpdateCoordinator.ExtractChecksum(text, "ai-usage-tray-win-x64-v2.0.2.zip"));
    }

    [Fact]
    public void ExtractChecksum_ReturnsNullForEmptyText()
    {
        Assert.Null(StartupUpdateCoordinator.ExtractChecksum(string.Empty, "package.zip"));
    }
}
