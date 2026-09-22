using costats.Core.Shell;
using Xunit;

namespace costats.Core.Tests.Shell;

public sealed class SystemExecutablesTests
{
    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("where.exe")]
    public void On_Windows_a_System32_tool_resolves_to_an_absolute_System32_path(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var resolved = SystemExecutables.ResolveSystem32(name);

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith(name, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".exe", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(resolved));

        var expectedDirectory = Path.TrimEndingDirectorySeparator(Environment.SystemDirectory);
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(resolved), ignoreCase: true);
    }

    [Fact]
    public void On_Windows_PowerShell_resolves_to_the_WindowsPowerShell_v1_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var resolved = SystemExecutables.PowerShell;

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith("powershell.exe", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(resolved));

        var expectedDirectory = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(expectedDirectory),
            Path.GetDirectoryName(resolved),
            ignoreCase: true);
    }

    [Fact]
    public void On_Windows_a_missing_exe_extension_is_appended()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var resolved = SystemExecutables.ResolveSystem32("where");

        Assert.EndsWith("where.exe", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Off_Windows_the_bare_name_is_returned_unchanged()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("where", SystemExecutables.ResolveSystem32("where"));
        Assert.Equal("powershell.exe", SystemExecutables.ResolveSystem32("powershell.exe"));
    }

    [Fact]
    public void The_System32_convenience_properties_match_ResolveSystem32()
    {
        Assert.Equal(SystemExecutables.ResolveSystem32("cmd.exe"), SystemExecutables.Cmd);
        Assert.Equal(SystemExecutables.ResolveSystem32("where.exe"), SystemExecutables.Where);
    }

    [Fact]
    public void The_trusted_working_directory_is_rooted()
    {
        Assert.False(string.IsNullOrWhiteSpace(SystemExecutables.TrustedWorkingDirectory));
        Assert.True(Path.IsPathRooted(SystemExecutables.TrustedWorkingDirectory));
        Assert.Equal(AppContext.BaseDirectory, SystemExecutables.TrustedWorkingDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_rejected(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => SystemExecutables.ResolveSystem32(name!));
    }
}
