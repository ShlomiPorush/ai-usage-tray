using costats.Core.Shell;
using Xunit;

namespace costats.Core.Tests.Shell;

public sealed class NotifyIconPathMatcherTests
{
    private const string ProcessPath = @"C:\Users\shlomi\AppData\Local\AIUsageTray\app\AIUsageTray.exe";
    private static readonly Guid LocalAppData = Guid.Parse("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private static readonly Guid ProgramFiles = Guid.Parse("6D809377-6AF0-444B-8957-A3773F02200E");

    private static string? Resolve(Guid id) =>
        id == LocalAppData ? @"C:\Users\shlomi\AppData\Local"
        : id == ProgramFiles ? @"C:\Program Files"
        : null;

    private static string? ResolveNothing(Guid id) => null;

    [Theory]
    [InlineData(@"C:\Users\shlomi\AppData\Local\AIUsageTray\app\AIUsageTray.exe")]
    [InlineData(@"c:\users\SHLOMI\appdata\local\aiusagetray\app\aiusagetray.EXE")]
    [InlineData(@"C:/Users/shlomi/AppData/Local/AIUsageTray/app/AIUsageTray.exe")]
    [InlineData(@"""C:\Users\shlomi\AppData\Local\AIUsageTray\app\AIUsageTray.exe""")]
    public void Plain_path_matches_case_insensitively(string stored)
    {
        Assert.True(NotifyIconPathMatcher.Matches(stored, ProcessPath, Resolve));
    }

    [Fact]
    public void Known_folder_prefix_is_resolved()
    {
        Assert.True(NotifyIconPathMatcher.Matches(
            @"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\AIUsageTray\app\AIUsageTray.exe",
            ProcessPath,
            Resolve));
        Assert.True(NotifyIconPathMatcher.Matches(
            @"{6d809377-6af0-444b-8957-a3773f02200e}\AIUsageTray\AIUsageTray.exe",
            @"C:\Program Files\AIUsageTray\AIUsageTray.exe",
            Resolve));
    }

    [Fact]
    public void Resolved_known_folder_must_match_the_whole_path()
    {
        Assert.False(NotifyIconPathMatcher.Matches(
            @"{6D809377-6AF0-444B-8957-A3773F02200E}\AIUsageTray\app\AIUsageTray.exe",
            ProcessPath,
            Resolve));
    }

    [Fact]
    public void Unresolvable_known_folder_falls_back_to_the_path_tail()
    {
        Assert.True(NotifyIconPathMatcher.Matches(
            @"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\AIUsageTray\app\AIUsageTray.exe",
            ProcessPath,
            ResolveNothing));
    }

    [Fact]
    public void Tail_fallback_requires_a_folder_boundary()
    {
        Assert.False(NotifyIconPathMatcher.Matches(
            @"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\AIUsageTray.exe",
            @"C:\Tools\xAIUsageTray.exe",
            ResolveNothing));
    }

    [Fact]
    public void Throwing_resolver_falls_back_to_the_path_tail()
    {
        Assert.True(NotifyIconPathMatcher.Matches(
            @"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\AIUsageTray\app\AIUsageTray.exe",
            ProcessPath,
            _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Other\Other.exe")]
    [InlineData(@"C:\Users\shlomi\AppData\Local\AIUsageTray\app-old\AIUsageTray.exe")]
    [InlineData(@"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\Other\app\Other.exe")]
    [InlineData(@"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}")]
    [InlineData(@"{not-a-guid}\AIUsageTray\app\AIUsageTray.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_paths_do_not_match(string? stored)
    {
        Assert.False(NotifyIconPathMatcher.Matches(stored, ProcessPath, Resolve));
    }

    [Fact]
    public void Missing_process_path_never_matches()
    {
        Assert.False(NotifyIconPathMatcher.Matches(ProcessPath, null, Resolve));
    }
}
