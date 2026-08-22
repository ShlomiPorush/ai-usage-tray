using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsageAccountMapTests
{
    private static readonly UsageAccountInfo ClaudeOne =
        new("claude-1", "Work", UsageProviderKind.Claude);

    private static readonly UsageAccountInfo ClaudeTwo =
        new("claude-2", "Personal", UsageProviderKind.Claude);

    private static readonly UsageAccountInfo Codex =
        new(UsageAccounts.MergedCodexId, UsageAccounts.MergedCodexDisplayName, UsageProviderKind.Codex);

    [Fact]
    public void ClaudeAccountBindsToItsOwnBucket()
    {
        var binding = UsageAccountMap.Resolve("claude:claude-2", [ClaudeOne, ClaudeTwo, Codex]);

        Assert.NotNull(binding);
        Assert.Equal("claude-2", binding.AccountId);
        Assert.Equal(UsageProviderKind.Claude, binding.Provider);
        Assert.False(binding.IsMerged);
    }

    [Fact]
    public void ClaudeAccountIdIsMatchedCaseInsensitively()
    {
        var binding = UsageAccountMap.Resolve("claude:CLAUDE-1", [ClaudeOne]);

        Assert.NotNull(binding);
        Assert.Equal("claude-1", binding.AccountId);
    }

    [Fact]
    public void UnscannedClaudeAccountResolvesToNothing()
    {
        Assert.Null(UsageAccountMap.Resolve("claude:claude-9", [ClaudeOne, Codex]));
    }

    [Fact]
    public void BareClaudeIdBindsOnlyWhenOneClaudeAccountExists()
    {
        var single = UsageAccountMap.Resolve("claude", [ClaudeOne, Codex]);
        Assert.NotNull(single);
        Assert.Equal("claude-1", single.AccountId);

        // Two candidates and no suffix: guessing would show the wrong spend.
        Assert.Null(UsageAccountMap.Resolve("claude", [ClaudeOne, ClaudeTwo]));
    }

    [Fact]
    public void EveryCodexAccountBindsToTheMergedBucket()
    {
        var first = UsageAccountMap.Resolve("codex:openai-1", [ClaudeOne, Codex]);
        var second = UsageAccountMap.Resolve("codex:openai-2", [ClaudeOne, Codex]);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(UsageAccounts.MergedCodexId, first.AccountId);
        Assert.Equal(UsageAccounts.MergedCodexId, second.AccountId);
        Assert.True(first.IsMerged);
        Assert.True(second.IsMerged);
        Assert.Equal(UsageProviderKind.Codex, first.Provider);
    }

    [Fact]
    public void CodexResolvesToNothingWhenNoCodexLogsWereScanned()
    {
        Assert.Null(UsageAccountMap.Resolve("codex:openai-1", [ClaudeOne]));
    }

    [Theory]
    [InlineData("zai")]
    [InlineData("copilot")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ProvidersWithoutLocalLogsResolveToNothing(string? providerId)
    {
        Assert.Null(UsageAccountMap.Resolve(providerId, [ClaudeOne, Codex]));
    }

    [Fact]
    public void EmptyAccountListResolvesToNothing()
    {
        Assert.Null(UsageAccountMap.Resolve("claude:claude-1", []));
        Assert.Null(UsageAccountMap.Resolve("claude:claude-1", null));
    }

    [Fact]
    public void MergedScopeNoteNamesTheWholeCodexBucket()
    {
        Assert.Equal("all Codex accounts", UsageAccountMap.MergedScopeNote);
    }
}
