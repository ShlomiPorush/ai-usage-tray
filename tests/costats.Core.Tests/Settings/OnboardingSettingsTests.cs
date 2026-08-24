using costats.Application.Settings;
using costats.Infrastructure.Settings;
using Xunit;

namespace costats.Core.Tests.Settings;

public sealed class OnboardingSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "costats-onboarding-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Missing_settings_file_is_a_first_run_and_shows_guided_setup()
    {
        var settings = await new JsonSettingsStore(basePath: _root)
            .LoadAsync(CancellationToken.None);

        Assert.True(settings.IsFirstRun);
        Assert.True(settings.ShouldShowInitialOnboarding);
        Assert.False(settings.ShouldShowOnboardingFallback);
    }

    [Fact]
    public async Task Legacy_settings_without_onboarding_state_do_not_show_first_run()
    {
        var path = Path.Combine(_root, "costats", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"refreshMinutes\":5}");

        var settings = await new JsonSettingsStore(basePath: _root)
            .LoadAsync(CancellationToken.None);

        Assert.False(settings.IsFirstRun);
        Assert.False(settings.ShouldShowInitialOnboarding);
        Assert.False(settings.ShouldShowOnboardingFallback);
    }

    [Theory]
    [InlineData(OnboardingStates.Started, true, false)]
    [InlineData(OnboardingStates.Dismissed, false, true)]
    [InlineData(OnboardingStates.Completed, false, false)]
    public void Persisted_state_selects_the_correct_surface(
        string state,
        bool showInitial,
        bool showFallback)
    {
        var settings = new AppSettings { OnboardingState = state };

        Assert.Equal(showInitial, settings.ShouldShowInitialOnboarding);
        Assert.Equal(showFallback, settings.ShouldShowOnboardingFallback);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
