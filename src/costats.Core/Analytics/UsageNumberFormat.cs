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
