using System.Text.Json;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class CodexResetCreditTests
{
    [Fact]
    public void Parser_preserves_all_available_details_and_unknown_types()
    {
        var snapshot = ParseBank("""
            {"availableCount":2,"credits":[
              {"id":"second","status":"available","resetType":"codexRateLimits","title":"Full reset",
               "description":"Both windows","grantedAt":1781654400,"expiresAt":1784246400},
              {"id":"first","status":"available","resetType":"futureType","expiresAt":null},
              {"id":"used","status":"redeemed","resetType":"codexRateLimits","expiresAt":null}
            ]}
            """);
        Assert.True(snapshot.ResetCreditBank.IsComplete);
        Assert.NotNull(snapshot.ResetCredits);
        Assert.Equal(new[] { "second", "first" }, snapshot.ResetCredits!.Select(credit => credit.Id));
        Assert.Equal("Full reset", snapshot.ResetCredits[0].Title);
        Assert.Equal("Both windows", snapshot.ResetCredits[0].Description);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1781654400), snapshot.ResetCredits[0].GrantedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784246400), snapshot.ResetCredits[0].ExpiresAt);
        Assert.False(snapshot.ResetCredits[1].CanUseAt(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"availableCount\":2,\"credits\":null}")]
    [InlineData("{\"availableCount\":2,\"credits\":[]}")]
    [InlineData("{\"availableCount\":\"2\",\"credits\":[]}")]
    [InlineData("{\"availableCount\":-1,\"credits\":[]}")]
    [InlineData("{\"availableCount\":1,\"credits\":[{\"id\":\"a\",\"status\":\"available\"}]}")]
    [InlineData("{\"availableCount\":1,\"credits\":[{\"id\":\"a\",\"status\":\"available\",\"expiresAt\":9223372036854775807}]}")]
    [InlineData("{\"availableCount\":1,\"credits\":[{\"id\":\"a\",\"status\":\"available\",\"expiresAt\":\"tomorrow\"}]}")]
    [InlineData("{\"availableCount\":2,\"credits\":[{\"id\":\"a\",\"status\":\"available\",\"expiresAt\":null},{\"id\":\"a\",\"status\":\"available\",\"expiresAt\":null}]}")]
    public void Invalid_or_incomplete_banks_do_not_enable_redemption_or_break_quota(string bank)
    {
        var snapshot = ParseBank(bank);
        Assert.Equal(75, snapshot.SessionRemainingPercent);
        Assert.False(snapshot.ResetCreditBank.IsComplete);
    }

    [Fact]
    public void Empty_bank_is_complete_when_details_were_loaded() =>
        Assert.True(ParseBank("{\"availableCount\":0,\"credits\":[]}").ResetCreditBank.IsComplete);

    [Fact]
    public async Task Source_carries_the_entire_bank_into_the_pulse()
    {
        var fake = new ResetClientFake();
        var source = new CodexAppServerSource(new CodexAccountProfile("test", "Test", "fake-home"), fake);
        var reading = await source.ReadAsync(CancellationToken.None);
        Assert.Equal(fake.Snapshot!.ResetCredits, reading.Usage!.ResetCredits);
        Assert.Equal(2, reading.Usage.ResetCreditsAvailable);
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("reset", ResetCreditOutcome.Reset)]
    [InlineData("alreadyRedeemed", ResetCreditOutcome.AlreadyRedeemed)]
    [InlineData("nothingToReset", ResetCreditOutcome.NothingToReset)]
    [InlineData("noCredit", ResetCreditOutcome.NoCredit)]
    [InlineData("future", ResetCreditOutcome.Unknown)]
    public async Task Protocol_sends_exact_selection_and_key_after_initialization(string result, ResetCreditOutcome expected)
    {
        var output = new StringReader(Initialize + "\n{\"method\":\"account/rateLimits/updated\",\"params\":{}}\n" +
            "{\"id\":4,\"result\":{\"outcome\":\"" + result + "\"}}");
        var input = new StringWriter();
        var outcome = await CodexAppServerClient.ExchangeResetCreditAsync(output, input, "selected-\"id", "retry-key", CancellationToken.None);
        Assert.Equal(expected, outcome);
        var requests = input.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(3, requests.Length);
            Assert.Equal("initialize", requests[0].RootElement.GetProperty("method").GetString());
            Assert.Equal("initialized", requests[1].RootElement.GetProperty("method").GetString());
            var consume = requests[2].RootElement;
            Assert.Equal("account/rateLimitResetCredit/consume", consume.GetProperty("method").GetString());
            Assert.Equal("selected-\"id", consume.GetProperty("params").GetProperty("creditId").GetString());
            Assert.Equal("retry-key", consume.GetProperty("params").GetProperty("idempotencyKey").GetString());
        }
        finally { foreach (var request in requests) request.Dispose(); }
    }

    [Theory]
    [InlineData("{\"id\":1,\"error\":{\"code\":-32601}}")]
    [InlineData("{\"id\":1,\"result\":{}}")]
    [InlineData("{\"id\":1,\"result\":{\"userAgent\":\"ai_usage_tray/0.140.0 (Windows)\"}}")]
    public async Task Unsupported_handshake_never_sends_a_redemption(string response)
    {
        var input = new StringWriter();
        var outcome = await CodexAppServerClient.ExchangeResetCreditAsync(new StringReader(response), input,
            "selected", "key", CancellationToken.None);
        Assert.Equal(ResetCreditOutcome.Unsupported, outcome);
        Assert.DoesNotContain("consume", input.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"id\":4,\"error\":{\"code\":-32000,\"message\":\"failed\"}}")]
    [InlineData("{\"id\":4,\"result\":null}")]
    [InlineData("{\"id\":4,\"result\":{\"outcome\":null}}")]
    public async Task Missing_or_uncertain_result_never_claims_success(string response)
    {
        var outcome = await CodexAppServerClient.ExchangeResetCreditAsync(new StringReader(Initialize + "\n" + response),
            new StringWriter(), "selected", "key", CancellationToken.None);
        Assert.Equal(ResetCreditOutcome.Unknown, outcome);
    }

    [Fact]
    public async Task Service_rechecks_bank_and_uses_only_selected_credit_and_account()
    {
        var fake = new ResetClientFake();
        var service = new CodexResetCreditService(fake, fake);
        await service.LoadAsync("account-a", CancellationToken.None);
        fake.Snapshot = fake.Snapshot! with { ResetCreditsAvailable = 3 };
        Assert.Equal(ResetCreditOutcome.IncompleteList, await service.RedeemAsync("account-a", "second", CancellationToken.None));
        Assert.Empty(fake.Calls);
        fake.Snapshot = ResetClientFake.FullBank();
        Assert.Equal(ResetCreditOutcome.Reset, await service.RedeemAsync("account-b", "second", CancellationToken.None));
        var call = Assert.Single(fake.Calls);
        Assert.Equal("account-b", call.Home);
        Assert.Equal("second", call.Credit);
        Assert.True(Guid.TryParse(call.Key, out _));
        Assert.Equal(3, fake.Reads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("unknown-type")]
    public async Task Service_never_substitutes_another_credit(string selection)
    {
        var fake = new ResetClientFake();
        fake.Snapshot = ResetClientFake.FullBank() with
        {
            ResetCredits = [ResetClientFake.Credit("first"), ResetClientFake.Credit(selection) with
            {
                Id = selection == "missing" ? "different" : selection,
                ExpiresAt = selection == "expired" ? DateTimeOffset.UtcNow.AddDays(-1) : null,
                ResetType = selection == "unknown-type" ? "future" : "codexRateLimits"
            }]
        };
        Assert.Equal(ResetCreditOutcome.CreditUnavailable,
            await new CodexResetCreditService(fake, fake).RedeemAsync("account", selection, CancellationToken.None));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Failed_authentication_or_read_never_consumes()
    {
        var fake = new ResetClientFake { Snapshot = null };
        var service = new CodexResetCreditService(fake, fake);
        Assert.Equal(ResetCreditOutcome.Unavailable, await service.RedeemAsync("account", "first", CancellationToken.None));
        fake.Snapshot = ResetClientFake.FullBank() with { RequiresSignIn = true };
        Assert.Equal(ResetCreditOutcome.SignInRequired, await service.RedeemAsync("account", "first", CancellationToken.None));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Retries_reuse_key_after_uncertainty_but_new_attempts_follow_definite_refusal()
    {
        var fake = new ResetClientFake { Outcome = ResetCreditOutcome.Unknown };
        var service = new CodexResetCreditService(fake, fake);
        await service.RedeemAsync("account", "first", CancellationToken.None);
        fake.Outcome = ResetCreditOutcome.NothingToReset;
        await service.RedeemAsync("account", "first", CancellationToken.None);
        await service.RedeemAsync("account", "first", CancellationToken.None);
        Assert.Equal(fake.Calls[0].Key, fake.Calls[1].Key);
        Assert.NotEqual(fake.Calls[1].Key, fake.Calls[2].Key);
    }

    [Fact]
    public async Task Concurrent_redemptions_are_blocked_until_first_finishes()
    {
        var fake = new ResetClientFake { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new CodexResetCreditService(fake, fake);
        var first = service.RedeemAsync("account", "first", CancellationToken.None);
        Assert.Equal(ResetCreditOutcome.Busy, await service.RedeemAsync("account", "second", CancellationToken.None));
        fake.Pending.SetResult(ResetCreditOutcome.Reset);
        Assert.Equal(ResetCreditOutcome.Reset, await first);
        Assert.Single(fake.Calls);
    }

    private const string Initialize = "{\"id\":1,\"result\":{\"userAgent\":\"ai_usage_tray/0.154.0 (Windows)\"}}";
    private static CodexAppServerRateLimitSnapshot ParseBank(string bank) =>
        Assert.IsType<CodexAppServerRateLimitSnapshot>(CodexAppServerRateLimitParser.Parse(
            "{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300}},\"rateLimitResetCredits\":" + bank + "}}", 2));
}

internal sealed class ResetClientFake : ICodexAppServerClient, ICodexResetCreditClient
{
    public CodexAppServerRateLimitSnapshot? Snapshot { get; set; } = FullBank();
    public ResetCreditOutcome Outcome { get; set; } = ResetCreditOutcome.Reset;
    public TaskCompletionSource<ResetCreditOutcome>? Pending { get; set; }
    public List<(string Home, string Credit, string Key)> Calls { get; } = [];
    public int Reads { get; private set; }
    public Task<CodexAppServerRateLimitSnapshot?> FetchAsync(string codexHome, bool refreshToken, CancellationToken cancellationToken)
    {
        Assert.False(refreshToken);
        Reads++;
        return Task.FromResult(Snapshot);
    }
    public Task<ResetCreditOutcome> ConsumeResetCreditAsync(string codexHome, string creditId, string idempotencyKey, CancellationToken cancellationToken)
    {
        Calls.Add((codexHome, creditId, idempotencyKey));
        return Pending?.Task ?? Task.FromResult(Outcome);
    }
    public static ResetCredit Credit(string id) => new(id, "codexRateLimits", "Reset " + id,
        "Both usage windows", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(20));
    public static CodexAppServerRateLimitSnapshot FullBank() => new(25, TimeSpan.FromHours(5), null, 50, TimeSpan.FromDays(7), null)
    {
        ResetCreditsAvailable = 2,
        ResetCredits = [Credit("first"), Credit("second")]
    };
}
