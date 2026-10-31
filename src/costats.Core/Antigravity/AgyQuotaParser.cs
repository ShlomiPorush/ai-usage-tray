using System.Globalization;
using System.Text.Json;
using costats.Core.Pulse;

namespace costats.Core.Antigravity;

/// <summary>What one statusline payload from the Antigravity CLI (<c>agy</c>) turned out to be.</summary>
public enum AgyPayloadKind
{
    /// <summary>Not a JSON object, for example a file caught half written. Read it again later.</summary>
    Invalid,

    /// <summary>A valid payload without quota yet (agy still starting or signing in).</summary>
    NoQuota,

    /// <summary>The payload has quota, but no Gemini bucket this parser understands.</summary>
    Unsupported,

    /// <summary>At least one Gemini bucket was read.</summary>
    Accepted
}

/// <summary>One accepted quota bucket, already converted to a used percentage.</summary>
public sealed record AgyQuotaBucket(string Family, string Window, long UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>
/// The quota read from an accepted payload. Gemini buckets are the account's
/// main windows; the shared "Claude &amp; GPT" buckets are scoped rows only, so
/// the headline colour reflects Gemini alone. The payload's email is never
/// carried here.
/// </summary>
public sealed record AgyQuotaReading(
    string? PlanTier,
    AgyQuotaBucket? GeminiWeekly,
    AgyQuotaBucket? GeminiFiveHour,
    IReadOnlyList<ScopedQuota> OtherQuotas)
{
    /// <summary>Builds the pulse shown by the tray. Never marks the account blocked.</summary>
    public UsagePulse ToUsagePulse(string providerId, DateTimeOffset capturedAt) =>
        new(
            ProviderId: providerId,
            CapturedAt: capturedAt,
            SessionUsed: GeminiFiveHour?.UsedPercent,
            SessionLimit: GeminiFiveHour is null ? null : 100,
            WeekUsed: GeminiWeekly?.UsedPercent,
            WeekLimit: GeminiWeekly is null ? null : 100,
            SessionWindow: GeminiFiveHour is null ? null : new QuotaWindow(AgyQuotaParser.FiveHourWindow, GeminiFiveHour.ResetsAt),
            WeekWindow: GeminiWeekly is null ? null : new QuotaWindow(AgyQuotaParser.WeeklyWindow, GeminiWeekly.ResetsAt))
        {
            ScopedQuotas = OtherQuotas
        };
}

public sealed record AgyParseResult(AgyPayloadKind Kind, AgyQuotaReading? Reading)
{
    public static AgyParseResult Invalid { get; } = new(AgyPayloadKind.Invalid, null);
    public static AgyParseResult NoQuota { get; } = new(AgyPayloadKind.NoQuota, null);
    public static AgyParseResult Unsupported { get; } = new(AgyPayloadKind.Unsupported, null);
}

/// <summary>
/// Reads the JSON payload that <c>agy</c> pipes to its statusline command.
/// Only bucket ids of the form <c>&lt;family&gt;-&lt;window&gt;</c> with a known
/// family (<c>gemini</c>, <c>3p</c>) and window (<c>weekly</c>, <c>5h</c>) are
/// used, and only with a finite <c>remaining_fraction</c> in [0, 1]. Anything
/// else is ignored rather than guessed at; an unknown window is never shown as
/// a session.
/// </summary>
public static class AgyQuotaParser
{
    public const string GeminiFamily = "gemini";
    public const string ThirdPartyFamily = "3p";
    public const string WeeklyWindowId = "weekly";
    public const string FiveHourWindowId = "5h";

    public const string GeminiLabel = "Gemini";
    public const string ThirdPartyLabel = "Claude & GPT";

    /// <summary>Group of a weekly scoped row; consumers treat any group containing "week" as weekly.</summary>
    public const string WeeklyGroup = "Weekly";

    /// <summary>Group of a five-hour scoped row.</summary>
    public const string SessionGroup = "Session";

    public static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);

    /// <summary>
    /// How close <c>reset_in_seconds</c> must be to the full window length for a
    /// full bucket to count as untouched. Such a bucket's <c>reset_time</c> is
    /// just "now plus the window" and moves on every payload, so it is not shown.
    /// </summary>
    public static readonly TimeSpan UntouchedTolerance = TimeSpan.FromSeconds(120);

    private const int MaxPlanLength = 60;

    public static AgyParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return AgyParseResult.Invalid;
        }

        try
        {
            using var document = JsonDocument.Parse(json.TrimStart('﻿'));
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return AgyParseResult.Invalid;
        }
    }

    private static AgyParseResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return AgyParseResult.Invalid;
        }

        if (!root.TryGetProperty("quota", out var quota) || quota.ValueKind == JsonValueKind.Null)
        {
            return AgyParseResult.NoQuota;
        }

        if (quota.ValueKind != JsonValueKind.Object)
        {
            return AgyParseResult.Unsupported;
        }

        AgyQuotaBucket? geminiWeekly = null;
        AgyQuotaBucket? geminiFiveHour = null;
        AgyQuotaBucket? otherFiveHour = null;
        AgyQuotaBucket? otherWeekly = null;
        var bucketCount = 0;

        foreach (var property in quota.EnumerateObject())
        {
            bucketCount++;
            if (!TryReadBucket(property.Name, property.Value, out var bucket))
            {
                continue;
            }

            // Each bucket appears once: a repeated id keeps its first value.
            switch (bucket.Family, bucket.Window)
            {
                case (GeminiFamily, WeeklyWindowId):
                    geminiWeekly ??= bucket;
                    break;
                case (GeminiFamily, FiveHourWindowId):
                    geminiFiveHour ??= bucket;
                    break;
                case (ThirdPartyFamily, FiveHourWindowId):
                    otherFiveHour ??= bucket;
                    break;
                case (ThirdPartyFamily, WeeklyWindowId):
                    otherWeekly ??= bucket;
                    break;
            }
        }

        if (bucketCount == 0)
        {
            return AgyParseResult.NoQuota;
        }

        if (geminiWeekly is null && geminiFiveHour is null)
        {
            return AgyParseResult.Unsupported;
        }

        var scoped = new List<ScopedQuota>();
        if (otherFiveHour is not null)
        {
            scoped.Add(ToScoped(otherFiveHour));
        }

        if (otherWeekly is not null)
        {
            scoped.Add(ToScoped(otherWeekly));
        }

        return new AgyParseResult(
            AgyPayloadKind.Accepted,
            new AgyQuotaReading(ReadPlan(root), geminiWeekly, geminiFiveHour, scoped));
    }

    /// <summary>Label of a bucket: the family name, plus "5-hour" for the five-hour window.</summary>
    public static string LabelFor(string family, string window)
    {
        var name = family == GeminiFamily ? GeminiLabel : ThirdPartyLabel;
        return window == FiveHourWindowId ? name + " 5-hour" : name;
    }

    private static ScopedQuota ToScoped(AgyQuotaBucket bucket) =>
        new(
            LabelFor(bucket.Family, bucket.Window),
            bucket.Window == WeeklyWindowId ? WeeklyGroup : SessionGroup,
            bucket.UsedPercent,
            bucket.ResetsAt,
            IsActive: false);

    private static bool TryReadBucket(string id, JsonElement value, out AgyQuotaBucket bucket)
    {
        bucket = null!;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var separator = id.IndexOf('-');
        if (separator <= 0 || separator == id.Length - 1)
        {
            return false;
        }

        var family = id[..separator];
        var window = id[(separator + 1)..];
        if (family is not (GeminiFamily or ThirdPartyFamily) || window is not (WeeklyWindowId or FiveHourWindowId))
        {
            return false;
        }

        if (value.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        // remaining_amount alone has no known scale, so it is not used.
        if (!value.TryGetProperty("remaining_fraction", out var fractionElement) ||
            fractionElement.ValueKind != JsonValueKind.Number ||
            !fractionElement.TryGetDouble(out var fraction) ||
            !double.IsFinite(fraction) ||
            fraction < 0 ||
            fraction > 1)
        {
            return false;
        }

        if (!value.TryGetProperty("reset_time", out var resetElement) ||
            resetElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                resetElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var resetTime))
        {
            return false;
        }

        var windowLength = window == WeeklyWindowId ? WeeklyWindow : FiveHourWindow;
        DateTimeOffset? resetsAt = IsUntouched(value, fraction, windowLength) ? null : resetTime;
        var used = (long)Math.Round((1 - fraction) * 100, MidpointRounding.AwayFromZero);

        bucket = new AgyQuotaBucket(family, window, Math.Clamp(used, 0, 100), resetsAt);
        return true;
    }

    private static bool IsUntouched(JsonElement bucket, double fraction, TimeSpan windowLength)
    {
        if (fraction != 1 ||
            !bucket.TryGetProperty("reset_in_seconds", out var seconds) ||
            seconds.ValueKind != JsonValueKind.Number ||
            !seconds.TryGetDouble(out var value) ||
            !double.IsFinite(value))
        {
            return false;
        }

        return Math.Abs(value - windowLength.TotalSeconds) <= UntouchedTolerance.TotalSeconds;
    }

    private static string? ReadPlan(JsonElement root)
    {
        if (!root.TryGetProperty("plan_tier", out var plan) || plan.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = plan.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length > MaxPlanLength ? text[..MaxPlanLength] : text;
    }
}
