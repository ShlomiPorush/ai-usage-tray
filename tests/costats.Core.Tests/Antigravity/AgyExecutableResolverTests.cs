using costats.Application.Settings;
using costats.Infrastructure.Antigravity;
using Xunit;

namespace costats.Core.Tests.Antigravity;

public sealed class AgyExecutableResolverTests
{
    private const string LocalAppData = @"C:\Users\Some One\AppData\Local";
    private static readonly string Installed = Path.Combine(LocalAppData, "agy", "bin", "agy.exe");

    [Fact]
    public void The_official_install_location_wins()
    {
        var resolved = AgyExecutableResolver.Resolve(Environment(@"D:\tools", Installed, @"D:\tools\agy.exe"));

        Assert.Equal(Installed, resolved);
    }

    [Fact]
    public void Path_folders_are_searched_in_order_for_agy_exe()
    {
        var resolved = AgyExecutableResolver.Resolve(
            Environment(@"relative\bin;D:\first;""D:\second""", @"D:\second\agy.exe", @"relative\bin\agy.exe"));

        Assert.Equal(@"D:\second\agy.exe", resolved);
    }

    [Fact]
    public void Nothing_found_is_null()
    {
        Assert.Null(AgyExecutableResolver.Resolve(Environment(@"D:\tools")));
    }

    [Fact]
    public void Only_windows_is_supported()
    {
        var environment = Environment(null, Installed) with { IsWindows = false };

        Assert.Null(AgyExecutableResolver.Resolve(environment));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    [InlineData(15, 15)]
    [InlineData(240, 240)]
    [InlineData(1000, 240)]
    public void The_check_interval_setting_is_clamped(int value, int expected)
    {
        Assert.Equal(15, new AppSettings().AntigravityMinIntervalMinutes);
        Assert.Equal(expected, new AppSettings { AntigravityMinIntervalMinutes = value }.AntigravityMinIntervalMinutes);
    }

    [Fact]
    public void The_system_boot_id_is_a_number_or_null_and_never_throws()
    {
        var direct = SystemUptimeClock.ReadBootId();
        var cached = SystemUptimeClock.Instance.BootId;

        Assert.Equal(direct, cached);
        Assert.Equal(cached, SystemUptimeClock.Instance.BootId);
        if (direct is { } value)
        {
            Assert.True(value >= 0);
        }
    }

    private static AgyResolverEnvironment Environment(string? path, params string[] existing) =>
        new(true, LocalAppData, path, candidate => existing.Contains(candidate, StringComparer.OrdinalIgnoreCase));
}
