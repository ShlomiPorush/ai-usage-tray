using costats.App.Services.Updates;
using Xunit;

namespace costats.Core.Tests.Updates;

/// <summary>
/// A winget copy must not self-update: winget owns its folder and version
/// record. Detection works from the app's own directory.
/// </summary>
public sealed class WingetInstallTests
{
    [Theory]
    [InlineData(@"C:\Users\Someone\AppData\Local\Microsoft\WinGet\Packages\AIUsageTray.AIUsageTray_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    [InlineData(@"C:\Users\Someone\AppData\Local\Microsoft\WinGet\Packages\AIUsageTray.AIUsageTray_Microsoft.Winget.Source_8wekyb3d8bbwe\")]
    [InlineData(@"C:\Program Files\WinGet\Packages\AIUsageTray.AIUsageTray_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    [InlineData(@"C:\Program Files (x86)\WinGet\Packages\AIUsageTray.AIUsageTray_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    [InlineData(@"c:\users\someone\appdata\local\microsoft\winget\packages\aiusagetray.aiusagetray_microsoft.winget.source_8wekyb3d8bbwe")]
    [InlineData(@"C:\Users\Someone\AppData\Local\Microsoft\WinGet\Packages\AIUsageTray.AIUsageTray_Microsoft.Winget.Source_8wekyb3d8bbwe\nested")]
    [InlineData("C:/Program Files/WinGet/Packages/AIUsageTray.AIUsageTray_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    public void IsWingetPackageDirectory_RecognisesUserAndMachineInstalls(string directory)
    {
        Assert.True(WingetInstall.IsWingetPackageDirectory(directory));
    }

    [Theory]
    [InlineData(@"C:\Users\Someone\AppData\Local\AIUsageTray\app")]
    [InlineData(@"C:\Users\Someone\AppData\Local\Microsoft\WinGet\Packages\Other.Package_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    [InlineData(@"C:\Users\Someone\AppData\Local\Microsoft\WinGet\Packages\AIUsageTray.AIUsageTrayBeta_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    [InlineData(@"C:\Users\Someone\AppData\Local\Microsoft\WinGet\Packages")]
    [InlineData(@"C:\Users\Someone\AppData\Local\Microsoft\WinGet\Links")]
    [InlineData(@"D:\Tools\AIUsageTray.AIUsageTray_x")]
    [InlineData(@"D:\Tools\Packages\AIUsageTray.AIUsageTray_x")]
    [InlineData(@"C:\repo\src\costats.App\bin\Release\net10.0-windows")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsWingetPackageDirectory_RejectsOtherFolders(string? directory)
    {
        Assert.False(WingetInstall.IsWingetPackageDirectory(directory));
    }

    [Fact]
    public void UpgradeCommand_UsesThePackageIdentifier()
    {
        Assert.Equal("AIUsageTray.AIUsageTray", WingetInstall.PackageIdentifier);
        Assert.Equal("winget upgrade AIUsageTray.AIUsageTray", WingetInstall.UpgradeCommand);
    }
}
