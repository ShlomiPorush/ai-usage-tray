using System.IO;
using System.Text.Json;
using costats.App.Services.Updates;
using Xunit;

namespace costats.Core.Tests.Updates;

/// <summary>
/// The install marker is what stops the updater, the installer and the
/// uninstaller from replacing or deleting a folder they do not own.
/// </summary>
public class InstallMarkerTests : IDisposable
{
    private readonly string _directory;

    public InstallMarkerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ai-usage-tray-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best effort.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void FileName_MatchesTheNameUsedByTheScripts()
    {
        Assert.Equal("install-manifest.json", InstallMarker.FileName);
        Assert.Equal("AIUsageTray", InstallMarker.AppIdentifier);
        Assert.Equal(1, InstallMarker.SchemaVersion);
    }

    [Fact]
    public void CreateContent_WritesTheAgreedFields()
    {
        var created = new DateTimeOffset(2026, 8, 23, 10, 30, 0, TimeSpan.Zero);

        using var document = JsonDocument.Parse(InstallMarker.CreateContent("install.ps1", created));
        var root = document.RootElement;

        Assert.Equal("AIUsageTray", root.GetProperty("app").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("install.ps1", root.GetProperty("installedBy").GetString());
        Assert.StartsWith("2026-08-23T10:30:00", root.GetProperty("installedUtc").GetString());
    }

    [Fact]
    public void IsValidContent_AcceptsWhatTheScriptsWrite()
    {
        // Exactly what ConvertTo-Json produces in install.ps1 and apply-update.ps1.
        const string json = """
        {
            "app":  "AIUsageTray",
            "schemaVersion":  1,
            "installedUtc":  "2026-08-23T10:30:00.0000000Z",
            "installedBy":  "install.ps1"
        }
        """;

        Assert.True(InstallMarker.IsValidContent(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"app":"SomethingElse"}""")]
    [InlineData("""{"app":123}""")]
    public void IsValidContent_RejectsAnythingElse(string? json)
    {
        Assert.False(InstallMarker.IsValidContent(json));
    }

    [Fact]
    public void Exists_IsFalseForAPlainDirectory()
    {
        Assert.False(InstallMarker.Exists(_directory));
    }

    [Fact]
    public void Exists_IsTrueAfterTryWrite()
    {
        Assert.True(InstallMarker.TryWrite(_directory, "unit-test"));
        Assert.True(File.Exists(Path.Combine(_directory, InstallMarker.FileName)));
        Assert.True(InstallMarker.Exists(_directory));
    }

    [Fact]
    public void Exists_IsFalseWhenTheMarkerBelongsToSomethingElse()
    {
        File.WriteAllText(InstallMarker.PathFor(_directory), """{"app":"OtherApp"}""");

        Assert.False(InstallMarker.Exists(_directory));
    }

    [Fact]
    public void IsDefaultManagedDirectory_MatchesTheInstallerLayout()
    {
        const string localAppData = @"C:\Users\Someone\AppData\Local";

        Assert.True(InstallMarker.IsDefaultManagedDirectory(@"C:\Users\Someone\AppData\Local\AIUsageTray\app", localAppData));
        Assert.True(InstallMarker.IsDefaultManagedDirectory(@"C:\Users\Someone\AppData\Local\aiusagetray\APP\", localAppData));
        Assert.False(InstallMarker.IsDefaultManagedDirectory(@"C:\Users\Someone\AppData\Local\AIUsageTray", localAppData));
        Assert.False(InstallMarker.IsDefaultManagedDirectory(@"D:\Tools", localAppData));
        Assert.False(InstallMarker.IsDefaultManagedDirectory(null, localAppData));
        Assert.False(InstallMarker.IsDefaultManagedDirectory(@"D:\Tools", null));
    }

    [Fact]
    public void IsDevelopmentDirectory_SpotsARepositoryBuild()
    {
        Assert.True(InstallMarker.IsDevelopmentDirectory(@"C:\repo\src\costats.App\bin\Debug\net10.0-windows"));
        Assert.False(InstallMarker.IsDevelopmentDirectory(@"D:\Tools\AIUsageTray"));
        Assert.False(InstallMarker.IsDevelopmentDirectory(null));
    }

    [Fact]
    public void IsManagedInstallDirectory_RefusesAnUnmarkedFolder()
    {
        Assert.False(InstallMarker.IsManagedInstallDirectory(_directory, "unit-test"));
        Assert.False(File.Exists(Path.Combine(_directory, InstallMarker.FileName)));
    }

    [Fact]
    public void IsManagedInstallDirectory_AcceptsAMarkedFolder()
    {
        InstallMarker.TryWrite(_directory, "unit-test");

        Assert.True(InstallMarker.IsManagedInstallDirectory(_directory, "unit-test"));
    }

    [Fact]
    public void IsManagedInstallDirectory_RefusesADevelopmentBuildEvenWithAMarker()
    {
        var devDirectory = Path.Combine(_directory, "src", "costats.App", "bin", "Debug");
        Directory.CreateDirectory(devDirectory);
        InstallMarker.TryWrite(devDirectory, "unit-test");

        Assert.False(InstallMarker.IsManagedInstallDirectory(devDirectory, "unit-test"));
    }

    [Fact]
    public void IsManagedInstallDirectory_RefusesEmptyInput()
    {
        Assert.False(InstallMarker.IsManagedInstallDirectory(null, "unit-test"));
        Assert.False(InstallMarker.IsManagedInstallDirectory("   ", "unit-test"));
    }
}
