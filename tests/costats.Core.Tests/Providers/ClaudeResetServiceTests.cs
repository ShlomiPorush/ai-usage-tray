using costats.Core.Pulse;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ClaudeResetServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "costats-tests",
        Guid.NewGuid().ToString("N"));

    private sealed class ClientFake : IClaudeResetCreditClient
    {
        public ClaudeResetSnapshot? Snapshot { get; set; } = FullBank();
        public ResetCreditOutcome ClaimOutcome { get; set; } = ResetCreditOutcome.Reset;
        public List<(string ConfigDir, string GrantId, string RequestId)> Claims { get; } = [];

        public Task<ClaudeResetSnapshot?> FetchAsync(string configDir, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task<ResetCreditOutcome> ClaimAsync(
            string configDir, string grantId, string requestId, CancellationToken cancellationToken)
        {
            Claims.Add((configDir, grantId, requestId));
            return Task.FromResult(ClaimOutcome);
        }

        public static ClaudeResetSnapshot FullBank() => new(
            false,
            new ResetCreditBank(1, [Grant("grant-a")]),
            null);

        public static ResetCredit Grant(string id, long usesLeft = 1) => new(
            id,
            ResetCredit.ClaudeType,
            "Usage limit reset",
            null,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(20),
            usesLeft);
    }

    [Fact]
    public async Task A_settled_claim_discards_its_idempotency_key_so_the_next_use_is_a_new_request()
    {
        var fake = new ClientFake
        {
            Snapshot = new ClaudeResetSnapshot(false, new ResetCreditBank(2, [ClientFake.Grant("grant-a", 2)]), null)
        };
        var service = new ClaudeResetService(fake, _root);

        Assert.Equal(ResetCreditOutcome.Reset, await service.RedeemAsync("cfg", "grant-a", CancellationToken.None));
        Assert.Equal(ResetCreditOutcome.Reset, await service.RedeemAsync("cfg", "grant-a", CancellationToken.None));

        Assert.Equal(2, fake.Claims.Count);
        Assert.NotEqual(fake.Claims[0].RequestId, fake.Claims[1].RequestId);
    }

    [Fact]
    public async Task An_uncertain_claim_keeps_its_key_so_the_retry_cannot_redeem_twice()
    {
        var fake = new ClientFake { ClaimOutcome = ResetCreditOutcome.Unknown };
        var service = new ClaudeResetService(fake, _root);

        Assert.Equal(ResetCreditOutcome.Unknown, await service.RedeemAsync("cfg", "grant-a", CancellationToken.None));
        Assert.Equal(ResetCreditOutcome.Unknown, await service.RedeemAsync("cfg", "grant-a", CancellationToken.None));

        Assert.Equal(2, fake.Claims.Count);
        Assert.Equal(fake.Claims[0].RequestId, fake.Claims[1].RequestId);
    }

    [Fact]
    public async Task An_uncertain_key_survives_a_restart_through_the_state_file()
    {
        var fake = new ClientFake { ClaimOutcome = ResetCreditOutcome.Unknown };
        Assert.Equal(
            ResetCreditOutcome.Unknown,
            await new ClaudeResetService(fake, _root).RedeemAsync("cfg", "grant-a", CancellationToken.None));
        Assert.Equal(
            ResetCreditOutcome.Unknown,
            await new ClaudeResetService(fake, _root).RedeemAsync("cfg", "grant-a", CancellationToken.None));

        Assert.Equal(fake.Claims[0].RequestId, fake.Claims[1].RequestId);
    }

    [Fact]
    public async Task Preflight_refuses_before_any_claim_is_sent()
    {
        var fake = new ClientFake { Snapshot = null };
        var service = new ClaudeResetService(fake, _root);
        Assert.Equal(ResetCreditOutcome.Unavailable, await service.RedeemAsync("cfg", "grant-a", CancellationToken.None));

        fake.Snapshot = new ClaudeResetSnapshot(true, ResetCreditBank.Unknown, null);
        Assert.Equal(ResetCreditOutcome.SignInRequired, await service.RedeemAsync("cfg", "grant-a", CancellationToken.None));

        fake.Snapshot = new ClaudeResetSnapshot(false, new ResetCreditBank(2, [ClientFake.Grant("grant-a")]), null);
        Assert.Equal(ResetCreditOutcome.IncompleteList, await service.RedeemAsync("cfg", "grant-a", CancellationToken.None));

        fake.Snapshot = ClientFake.FullBank();
        Assert.Equal(ResetCreditOutcome.CreditUnavailable, await service.RedeemAsync("cfg", "grant-b", CancellationToken.None));

        Assert.Empty(fake.Claims);
    }

    [Theory]
    [InlineData("""{ "result": "reset", "resets_left": 0, "cleared": ["five_hour"] }""", ResetCreditOutcome.Reset)]
    [InlineData("""{ "result": "not_limited" }""", ResetCreditOutcome.NothingToReset)]
    [InlineData("""{ "result": "already_used" }""", ResetCreditOutcome.AlreadyRedeemed)]
    [InlineData("""{ "result": "ineligible" }""", ResetCreditOutcome.CreditUnavailable)]
    [InlineData("""{ "result": "cooldown" }""", ResetCreditOutcome.Cooldown)]
    [InlineData("""{ "result": "rate_limited" }""", ResetCreditOutcome.Cooldown)]
    [InlineData("""{ "result": "auth_error" }""", ResetCreditOutcome.SignInRequired)]
    [InlineData("""{ "result": "something_new" }""", ResetCreditOutcome.Unknown)]
    [InlineData("""{ "unexpected": true }""", ResetCreditOutcome.Unknown)]
    [InlineData("not json", ResetCreditOutcome.Unknown)]
    public void Claim_results_map_to_outcomes_and_new_values_stay_uncertain(string json, ResetCreditOutcome expected)
    {
        Assert.Equal(expected, ClaudeResetCreditClient.ParseClaimResult(json));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
