using costats.Core.Antigravity;
using Xunit;

namespace costats.Core.Tests.Antigravity;

public sealed class AgyQuotaParserTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Real_payload_maps_gemini_to_the_main_window_and_claude_gpt_to_a_scoped_row()
    {
        var result = AgyQuotaParser.Parse(AgyFixtures.RealPayload);

        Assert.Equal(AgyPayloadKind.Accepted, result.Kind);
        var reading = result.Reading!;
        Assert.Equal("Google AI Plus", reading.PlanTier);
        Assert.Null(reading.GeminiFiveHour);
        Assert.Equal(100, reading.GeminiWeekly!.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 58, 7, TimeSpan.Zero), reading.GeminiWeekly.ResetsAt);

        var scoped = Assert.Single(reading.OtherQuotas);
        Assert.Equal("Claude & GPT", scoped.Label);
        Assert.Equal("Weekly", scoped.Group);
        Assert.Equal(0, scoped.UsedPercent);
        Assert.False(scoped.IsActive);
        // Untouched: a full bucket whose reset is a whole window away moves on
        // every payload, so it gets no reset time.
        Assert.Null(scoped.ResetsAt);

        var pulse = reading.ToUsagePulse("antigravity", CapturedAt);
        Assert.Equal(100, pulse.WeekUsed);
        Assert.Equal(100, pulse.WeekLimit);
        Assert.Equal(TimeSpan.FromDays(7), pulse.WeekWindow!.Duration);
        Assert.Null(pulse.SessionUsed);
        Assert.Null(pulse.SessionWindow);
        Assert.False(pulse.IsBlocked);
        Assert.Single(pulse.ScopedQuotas);
    }

    [Fact]
    public void The_email_is_never_carried_into_the_reading()
    {
        Assert.Contains(AgyFixtures.Email, AgyFixtures.RealPayloadWithPlaceholderEmail, StringComparison.Ordinal);
        var reading = AgyQuotaParser.Parse(AgyFixtures.RealPayloadWithPlaceholderEmail).Reading!;

        Assert.DoesNotContain(AgyFixtures.Email, reading.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AgyFixtures.Email, reading.ToUsagePulse("antigravity", CapturedAt).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Five_hour_buckets_become_the_session_window_and_a_labelled_scoped_row()
    {
        const string json = """
            {"quota":{
              "gemini-5h":{"remaining_fraction":0.375,"reset_time":"2026-10-30T12:00:00Z","reset_in_seconds":10800},
              "gemini-weekly":{"remaining_fraction":0.8,"reset_time":"2026-11-04T12:00:00Z","reset_in_seconds":400000},
              "3p-5h":{"remaining_fraction":0.25,"reset_time":"2026-10-30T11:00:00Z","reset_in_seconds":7200},
              "3p-weekly":{"remaining_fraction":0.9,"reset_time":"2026-11-05T12:00:00Z","reset_in_seconds":500000}}}
            """;

        var reading = AgyQuotaParser.Parse(json).Reading!;
        var pulse = reading.ToUsagePulse("antigravity", CapturedAt);

        Assert.Equal(63, pulse.SessionUsed); // (1 - 0.375) * 100 = 62.5, rounded half away from zero
        Assert.Equal(100, pulse.SessionLimit);
        Assert.Equal(TimeSpan.FromHours(5), pulse.SessionWindow!.Duration);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), pulse.SessionWindow.ResetsAt);
        Assert.Equal(20, pulse.WeekUsed);

        Assert.Collection(
            pulse.ScopedQuotas,
            session =>
            {
                Assert.Equal("Claude & GPT 5-hour", session.Label);
                Assert.Equal("Session", session.Group);
                Assert.Equal(75, session.UsedPercent);
                Assert.NotNull(session.ResetsAt);
            },
            weekly =>
            {
                Assert.Equal("Claude & GPT", weekly.Label);
                Assert.Equal("Weekly", weekly.Group);
                Assert.Equal(10, weekly.UsedPercent);
            });
    }

    [Fact]
    public void A_disabled_bucket_is_skipped()
    {
        const string json = """
            {"quota":{
              "gemini-5h":{"remaining_fraction":0.5,"reset_time":"2026-10-30T12:00:00Z","disabled":true},
              "gemini-weekly":{"remaining_fraction":0.5,"reset_time":"2026-11-04T12:00:00Z"}}}
            """;

        var pulse = AgyQuotaParser.Parse(json).Reading!.ToUsagePulse("antigravity", CapturedAt);

        Assert.Null(pulse.SessionUsed);
        Assert.Equal(50, pulse.WeekUsed);
    }

    [Fact]
    public void A_bucket_with_only_remaining_amount_is_ignored()
    {
        const string json = """
            {"quota":{"gemini-weekly":{"remaining_amount":120,"reset_time":"2026-11-04T12:00:00Z"}}}
            """;

        Assert.Equal(AgyPayloadKind.Unsupported, AgyQuotaParser.Parse(json).Kind);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("\"0.5\"")]
    [InlineData("null")]
    public void A_fraction_outside_zero_to_one_or_not_a_number_is_ignored(string fraction)
    {
        var json = "{\"quota\":{\"gemini-weekly\":{\"remaining_fraction\":" + fraction + ",\"reset_time\":\"2026-11-04T12:00:00Z\"}}}";

        Assert.Equal(AgyPayloadKind.Unsupported, AgyQuotaParser.Parse(json).Kind);
    }

    [Theory]
    [InlineData("gemini-daily")]
    [InlineData("gemini-pro-weekly")]
    [InlineData("vertex-weekly")]
    [InlineData("gemini")]
    [InlineData("-weekly")]
    [InlineData("gemini-")]
    public void Unknown_families_and_windows_are_ignored_and_never_become_a_session(string bucketId)
    {
        var json = "{\"quota\":{\"" + bucketId + "\":{\"remaining_fraction\":0.5,\"reset_time\":\"2026-11-04T12:00:00Z\"}}}";

        Assert.Equal(AgyPayloadKind.Unsupported, AgyQuotaParser.Parse(json).Kind);
    }

    [Fact]
    public void Unknown_buckets_next_to_a_gemini_bucket_are_dropped()
    {
        const string json = """
            {"quota":{
              "gemini-weekly":{"remaining_fraction":0.5,"reset_time":"2026-11-04T12:00:00Z"},
              "gemini-daily":{"remaining_fraction":0.1,"reset_time":"2026-10-31T12:00:00Z"},
              "other-5h":{"remaining_fraction":0.1,"reset_time":"2026-10-30T12:00:00Z"}}}
            """;

        var pulse = AgyQuotaParser.Parse(json).Reading!.ToUsagePulse("antigravity", CapturedAt);

        Assert.Null(pulse.SessionUsed);
        Assert.Equal(50, pulse.WeekUsed);
        Assert.Empty(pulse.ScopedQuotas);
    }

    [Fact]
    public void A_bucket_missing_its_reset_time_is_ignored()
    {
        const string json = """{"quota":{"gemini-weekly":{"remaining_fraction":0.5}}}""";

        Assert.Equal(AgyPayloadKind.Unsupported, AgyQuotaParser.Parse(json).Kind);
    }

    [Fact]
    public void Quota_without_any_gemini_bucket_is_unsupported()
    {
        Assert.Equal(AgyPayloadKind.Unsupported, AgyQuotaParser.Parse(AgyFixtures.ThirdPartyOnlyPayload).Kind);
    }

    [Theory]
    [InlineData("""{"agent_state":"initializing"}""")]
    [InlineData("""{"agent_state":"authenticating","quota":null}""")]
    [InlineData("""{"agent_state":"idle","quota":{}}""")]
    public void A_payload_without_quota_yet_is_no_quota(string json)
    {
        Assert.Equal(AgyPayloadKind.NoQuota, AgyQuotaParser.Parse(json).Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"quota\":{\"gemini-weekly\":")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void A_partial_or_foreign_file_is_invalid_so_it_is_read_again(string json)
    {
        Assert.Equal(AgyPayloadKind.Invalid, AgyQuotaParser.Parse(json).Kind);
    }

    [Fact]
    public void A_full_bucket_with_a_reset_well_inside_the_window_keeps_its_reset_time()
    {
        const string json = """
            {"quota":{"gemini-weekly":{"remaining_fraction":1,"reset_time":"2026-11-04T12:00:00Z","reset_in_seconds":300000}}}
            """;

        var bucket = AgyQuotaParser.Parse(json).Reading!.GeminiWeekly!;

        Assert.Equal(0, bucket.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), bucket.ResetsAt);
    }

    [Theory]
    [InlineData(18000)]
    [InlineData(17900)]
    [InlineData(18100)]
    public void An_untouched_five_hour_bucket_has_no_reset_time(int resetInSeconds)
    {
        var json = "{\"quota\":{\"gemini-5h\":{\"remaining_fraction\":1,\"reset_time\":\"2026-10-30T14:00:00Z\",\"reset_in_seconds\":" + resetInSeconds + "}}}";

        Assert.Null(AgyQuotaParser.Parse(json).Reading!.GeminiFiveHour!.ResetsAt);
    }

    [Fact]
    public void A_partly_used_bucket_keeps_its_reset_time_even_near_the_window_length()
    {
        const string json = """
            {"quota":{"gemini-5h":{"remaining_fraction":0.99,"reset_time":"2026-10-30T14:00:00Z","reset_in_seconds":18000}}}
            """;

        Assert.NotNull(AgyQuotaParser.Parse(json).Reading!.GeminiFiveHour!.ResetsAt);
    }
}
