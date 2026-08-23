namespace costats.Core.Analytics;

/// <summary>
/// Which provider produced an analytics bucket. Claude and Codex come from
/// local logs; Z.AI comes from its official remote model-usage endpoint.
/// </summary>
public enum UsageProviderKind
{
    /// <summary>Claude Code, read from each account's <c>projects/**/*.jsonl</c>.</summary>
    Claude,

    /// <summary>OpenAI Codex, read from the shared <c>sessions/**/rollout-*.jsonl</c>.</summary>
    Codex,

    /// <summary>Z.AI / GLM, read from the official remote model-usage endpoint.</summary>
    Zai
}

/// <summary>
/// The token counts of a single billable request, split the way both providers
/// bill them. Every field is a raw count; nothing here is priced or scaled.
/// </summary>
/// <remarks>
/// The four input buckets are disjoint, so
/// <see cref="InputTokens"/> = <see cref="UncachedInputTokens"/> +
/// <see cref="CacheReadInputTokens"/> + <see cref="CacheWrite5mInputTokens"/> +
/// <see cref="CacheWrite1hInputTokens"/>. Provider quirks are normalised by the
/// parsers before a sample is built:
/// <list type="bullet">
/// <item>Claude reports uncached input in <c>input_tokens</c> and the cache
/// buckets separately, so its fields map across one to one.</item>
/// <item>Codex reports <c>input_tokens</c> as the full input including the
/// cached part, so the parser subtracts <c>cached_input_tokens</c> to get
/// <see cref="UncachedInputTokens"/>.</item>
/// </list>
/// <see cref="ReasoningOutputTokens"/> is a subset of <see cref="OutputTokens"/>
/// (Claude's <c>output_tokens_details.thinking_tokens</c>, Codex's
/// <c>reasoning_output_tokens</c>), carried for reporting only. It is never
/// added to a total, because both providers already bill it inside
/// <c>output_tokens</c>.
/// </remarks>
public readonly record struct UsageTokens
{
    /// <summary>A sample that consumed nothing.</summary>
    public static readonly UsageTokens Empty = default;

    /// <summary>Input tokens billed at the full input rate (no cache involved).</summary>
    public long UncachedInputTokens { get; init; }

    /// <summary>Input tokens served from a cache hit, billed at the cheap cache-read rate.</summary>
    public long CacheReadInputTokens { get; init; }

    /// <summary>Input tokens written into the 5-minute ephemeral cache.</summary>
    public long CacheWrite5mInputTokens { get; init; }

    /// <summary>Input tokens written into the 1-hour ephemeral cache.</summary>
    public long CacheWrite1hInputTokens { get; init; }

    /// <summary>Generated output tokens, reasoning and thinking included.</summary>
    public long OutputTokens { get; init; }

    /// <summary>
    /// The reasoning or thinking part of <see cref="OutputTokens"/>. Reported,
    /// never summed: it is already inside <see cref="OutputTokens"/>.
    /// </summary>
    public long ReasoningOutputTokens { get; init; }

    /// <summary>All cache writes, both TTLs.</summary>
    public long CacheWriteInputTokens => CacheWrite5mInputTokens + CacheWrite1hInputTokens;

    /// <summary>Every input token the request paid for, at whatever rate.</summary>
    public long InputTokens =>
        UncachedInputTokens + CacheReadInputTokens + CacheWrite5mInputTokens + CacheWrite1hInputTokens;

    /// <summary>Everything the model moved: all input buckets plus output.</summary>
    public long ProcessedTokens => InputTokens + OutputTokens;

    /// <summary>Component-wise sum, used to fold samples into a bucket.</summary>
    public UsageTokens Add(UsageTokens other) => new()
    {
        UncachedInputTokens = UncachedInputTokens + other.UncachedInputTokens,
        CacheReadInputTokens = CacheReadInputTokens + other.CacheReadInputTokens,
        CacheWrite5mInputTokens = CacheWrite5mInputTokens + other.CacheWrite5mInputTokens,
        CacheWrite1hInputTokens = CacheWrite1hInputTokens + other.CacheWrite1hInputTokens,
        OutputTokens = OutputTokens + other.OutputTokens,
        ReasoningOutputTokens = ReasoningOutputTokens + other.ReasoningOutputTokens
    };

    /// <inheritdoc cref="Add"/>
    public static UsageTokens operator +(UsageTokens left, UsageTokens right) => left.Add(right);
}

/// <summary>
/// One deduplicated request read out of a local agent log. Only usage numbers,
/// the model id and the timestamp are ever materialised; message content is
/// never parsed into a sample.
/// </summary>
/// <param name="Timestamp">When the provider logged the request, in UTC.</param>
/// <param name="Provider">Which agent produced it.</param>
/// <param name="AccountId">
/// The monitored account it belongs to. Claude samples carry the real per-account
/// id; every Codex sample carries <see cref="UsageAccounts.MergedCodexId"/>,
/// because all Codex profiles share one session directory and cannot be told apart.
/// </param>
/// <param name="Model">The model id exactly as the log reported it.</param>
/// <param name="Tokens">The request's token counts.</param>
public sealed record UsageSample(
    DateTimeOffset Timestamp,
    UsageProviderKind Provider,
    string AccountId,
    string Model,
    UsageTokens Tokens);

/// <summary>Well-known account identifiers used by the usage engine.</summary>
public static class UsageAccounts
{
    /// <summary>
    /// The single bucket every Codex account's usage lands in. Codex symlinks
    /// each profile's <c>sessions</c> folder to one shared directory, so a
    /// rollout file cannot be attributed to the account that wrote it.
    /// </summary>
    public const string MergedCodexId = "codex";

    /// <summary>Display name for <see cref="MergedCodexId"/>.</summary>
    public const string MergedCodexDisplayName = "Codex (all accounts)";
}

/// <summary>
/// An account the usage engine can filter by, for a picker in the UI.
/// </summary>
/// <param name="AccountId">Value to pass in the account filter.</param>
/// <param name="DisplayName">User-facing nickname.</param>
/// <param name="Provider">Which agent it belongs to.</param>
public sealed record UsageAccountInfo(
    string AccountId,
    string DisplayName,
    UsageProviderKind Provider);
