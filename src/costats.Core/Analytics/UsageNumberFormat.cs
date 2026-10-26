using System.Globalization;

namespace costats.Core.Analytics;

/// <summary>
/// The one place the usage dashboard turns numbers into text. Every helper is
/// culture-invariant on purpose: the figures are US dollars and raw token
/// counts, and the dashboard must read the same on every machine.
/// </summary>
/// <remarks>
/// Token counts use three significant digits and a unit suffix
/// ("13.3B", "106M", "16.5K"), which keeps a column of numbers the same width
/// while still separating 7.90B from 7.9B worth of traffic. Money is never
/// abbreviated: a cost is either exact to the cent or it is not a cost.
/// </remarks>
public static class UsageNumberFormat
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// A compact token count with three significant digits: <c>0</c>,
    /// <c>348</c>, <c>16.5K</c>, <c>106M</c>, <c>7.90B</c>, <c>13.3B</c>.
    /// Negative inputs are clamped to zero, because no bucket can consume
    /// negative tokens.
    /// </summary>
    public static string Tokens(long tokens)
    {
        if (tokens <= 0)
        {
            return "0";
        }

        if (tokens < 1_000)
        {
            return tokens.ToString(Culture);
        }

        var (scaled, suffix) = Scale(tokens);

        // Rounding can push a value over its own unit (999,600 -> "1000K"),
        // so promote it before formatting.
        if (suffix != "B" && Math.Round(scaled, DecimalsFor(scaled)) >= 1_000d)
        {
            scaled /= 1_000d;
            suffix = suffix == "K" ? "M" : "B";
        }

        return scaled.ToString(FormatFor(scaled), Culture) + suffix;
    }

    /// <summary>
    /// An exact US dollar amount: <c>$0.00</c>, <c>$1,152.15</c>,
    /// <c>$69,668.14</c>.
    /// </summary>
    public static string Money(decimal amount) => amount.ToString("C2", Culture);

    /// <summary>
    /// A bucket's cost, or <c>unpriced</c> when every token in it came from a
    /// model the pricing table does not cover. Nothing that cost money is ever
    /// written as <c>$0.00</c>: a bucket is either priced or it says it is not.
    /// A bucket that mixes priced and unpriced models still shows its priced
    /// cost, which is a floor; whatever surface owns the figure is responsible
    /// for saying so.
    /// </summary>
    public static string CostOrUnpriced(decimal costUsd, long unpricedTokens) =>
        costUsd == 0m && unpricedTokens > 0 ? "unpriced" : Money(costUsd);

    /// <summary>
    /// A chart axis tick for the cost metric. Zero is drawn bare so the
    /// baseline does not shout "$0.00" at the reader.
    /// </summary>
    public static string AxisCost(decimal amount) => amount == 0m ? "0" : Money(amount);

    /// <summary>A chart axis tick for the token metric.</summary>
    public static string AxisTokens(long tokens) => tokens <= 0 ? "0" : Tokens(tokens);

    /// <summary>
    /// A share as a percentage with one decimal: <c>48.8%</c>. A zero or
    /// negative whole means nothing to divide into, so the share is
    /// <c>0.0%</c>.
    /// </summary>
    public static string Percent(decimal part, decimal whole) =>
        whole <= 0m ? "0.0%" : Percent((double)(part / whole));

    /// <inheritdoc cref="Percent(decimal, decimal)"/>
    public static string Percent(double fraction)
    {
        if (double.IsNaN(fraction) || double.IsInfinity(fraction))
        {
            return "0.0%";
        }

        return (fraction * 100d).ToString("0.0", Culture) + "%";
    }

    /// <summary>
    /// A ratio written as a multiplier: <c>6.7x</c>. Used for "cache savings
    /// were 6.7x the raw token cost".
    /// </summary>
    public static string Multiplier(decimal part, decimal whole)
    {
        if (whole <= 0m)
        {
            return "0.0x";
        }

        return (part / whole).ToString("0.0", Culture) + "x";
    }

    /// <summary>
    /// The header's range caption: <c>Jul 25 to Aug 23</c>.
    /// </summary>
    public static string RangeLabel(DateOnly from, DateOnly to) =>
        $"{DayLabel(from)} to {DayLabel(to)}";

    /// <summary>
    /// A range caption that adds the year when it is needed: <c>Jul 25 to
    /// Aug 23</c> inside <paramref name="today"/>'s year, <c>Dec 20, 2025 to
    /// Jan 5, 2026</c> otherwise. A single day reads <c>Sat, Aug 23</c>.
    /// </summary>
    public static string RangeLabel(DateOnly from, DateOnly to, DateOnly today)
    {
        var withYear = from.Year != today.Year || to.Year != today.Year;
        if (from == to)
        {
            return from.ToString(withYear ? "ddd, MMM d, yyyy" : "ddd, MMM d", Culture);
        }

        return withYear
            ? $"{from.ToString("MMM d, yyyy", Culture)} to {to.ToString("MMM d, yyyy", Culture)}"
            : RangeLabel(from, to);
    }

    /// <summary>
    /// A change against a comparison period: <c>▲ 12.4%</c>, <c>▼ 3.0%</c>,
    /// <c>0.0%</c>, with <c> tokens</c> appended when tokens were compared
    /// instead of cost, and <c>no prior data</c> when the baseline is empty.
    /// Empty when there is nothing to compare.
    /// </summary>
    /// <param name="delta">The change to describe.</param>
    /// <param name="showBasis">
    /// False where the figure is tokens anyway, so the suffix would only repeat it.
    /// </param>
    public static string Delta(UsageDelta delta, bool showBasis = true)
    {
        var suffix = showBasis && delta.Basis == UsageDeltaBasis.Tokens ? " tokens" : string.Empty;
        switch (delta.Kind)
        {
            case UsageDeltaKind.NoPriorData:
                return "no prior data";
            case UsageDeltaKind.Change:
                var percent = Math.Abs(delta.Fraction) * 100d;
                var magnitude = percent.ToString(percent >= 100d ? "#,0" : "0.0", Culture) + "%";
                if (magnitude is "0.0%" or "0%")
                {
                    return "0.0%" + suffix;
                }

                return (delta.Fraction > 0d ? "▲ " : "▼ ") + magnitude + suffix;
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// A breakdown row's day label, with the year when it is not
    /// <paramref name="today"/>'s: <c>Sat, Aug 23</c> or <c>Wed, Oct 1, 2025</c>.
    /// </summary>
    public static string LongDayLabel(DateOnly day, DateOnly today) =>
        day.Year == today.Year ? LongDayLabel(day) : day.ToString("ddd, MMM d, yyyy", Culture);

    /// <summary>
    /// A breakdown row's hour label in <paramref name="zone"/>, with the year
    /// when it is not <paramref name="today"/>'s, and the UTC offset on an hour
    /// a daylight-saving change repeats.
    /// </summary>
    public static string LongHourLabel(DateTimeOffset instant, TimeZoneInfo zone, DateOnly today)
    {
        var label = LongHourLabel(instant, zone);
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        if (local.Year == today.Year)
        {
            return label;
        }

        // "Wed, Oct 1, 14:00" becomes "Wed, Oct 1, 2025, 14:00".
        var date = local.ToString("ddd, MMM d", Culture);
        return date + ", " + local.Year.ToString(Culture) + label[date.Length..];
    }

    /// <summary>
    /// A breakdown row's hour label in <paramref name="zone"/>. An hour that a
    /// daylight-saving change repeats carries its UTC offset, so the two
    /// copies can be told apart: <c>Sun, Oct 25, 02:00 (UTC+3)</c>.
    /// </summary>
    public static string LongHourLabel(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var label = LongHourLabel(local);
        if (!zone.IsAmbiguousTime(local))
        {
            return label;
        }

        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var hours = Math.Abs(offset.Hours);
        var minutes = Math.Abs(offset.Minutes);
        return minutes == 0
            ? $"{label} (UTC{sign}{hours})"
            : $"{label} (UTC{sign}{hours}:{minutes:00})";
    }

    /// <summary>
    /// The header's caption for a rolling window, in local time:
    /// <c>Sep 22, 14:37 to Sep 23, 14:37</c>.
    /// </summary>
    public static string RangeLabel(DateTimeOffset localSince, DateTimeOffset localUntil) =>
        $"{localSince.ToString("MMM d, HH:mm", Culture)} to {localUntil.ToString("MMM d, HH:mm", Culture)}";

    /// <summary>A chart axis label for an hour of a multi-day range: <c>OCT 18 14:00</c>.</summary>
    public static string AxisDayHourLabel(DateTimeOffset local) =>
        local.ToString("MMM d HH:mm", Culture).ToUpperInvariant();

    /// <summary>A chart axis hour label in local time: <c>14:37</c>.</summary>
    public static string AxisHourLabel(DateTimeOffset local) =>
        local.ToString("HH:mm", Culture);

    /// <summary>A breakdown row's hour label in local time: <c>Tue, Sep 23, 14:37</c>.</summary>
    public static string LongHourLabel(DateTimeOffset local) =>
        local.ToString("ddd, MMM d, HH:mm", Culture);

    /// <summary>A short day label: <c>Jul 25</c>.</summary>
    public static string DayLabel(DateOnly day) =>
        day.ToString("MMM d", Culture);

    /// <summary>A chart axis day label: <c>JUL 25</c>.</summary>
    public static string AxisDayLabel(DateOnly day) =>
        DayLabel(day).ToUpperInvariant();

    /// <summary>A breakdown row's day label: <c>Sat, Aug 23</c>.</summary>
    public static string LongDayLabel(DateOnly day) =>
        day.ToString("ddd, MMM d", Culture);

    private static (double Scaled, string Suffix) Scale(long tokens) => tokens switch
    {
        >= 1_000_000_000 => (tokens / 1_000_000_000d, "B"),
        >= 1_000_000 => (tokens / 1_000_000d, "M"),
        _ => (tokens / 1_000d, "K")
    };

    private static int DecimalsFor(double scaled) => scaled switch
    {
        >= 100d => 0,
        >= 10d => 1,
        _ => 2
    };

    private static string FormatFor(double scaled) => DecimalsFor(scaled) switch
    {
        0 => "0",
        1 => "0.0",
        _ => "0.00"
    };
}
