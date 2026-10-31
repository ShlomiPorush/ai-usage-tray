using costats.Core.Antigravity;
using costats.Infrastructure.Antigravity;
using Xunit;

namespace costats.Core.Tests.Antigravity;

/// <summary>
/// The pure schedule rules, and the same rules through the runner and its
/// schedule file. Runner tests here resolve no executable, so a permitted
/// check ends as agy-not-found without starting any process.
/// </summary>
public sealed class AgyProbeScheduleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Fifteen = TimeSpan.FromMinutes(15);

    private readonly string _basePath = AgyFixtures.NewBasePath("schedule");
    private readonly ManualTimeProvider _time = new(T0);
    private readonly FakeUptime _uptime = new(10_000_000);
    private int _resolveCalls;

    public void Dispose() => AgyFixtures.DeleteQuietly(_basePath);

    // ---- Pure rules -------------------------------------------------------

    [Fact]
    public void No_previous_attempt_allows_a_check()
    {
        var decision = AgyProbeSchedule.Evaluate(AgyScheduleFile.Missing, T0, 1000, Fifteen);

        Assert.Equal(AgyScheduleVerdict.Launch, decision.Verdict);
    }

    [Fact]
    public void Both_the_wall_clock_and_the_uptime_must_have_moved_a_full_interval()
    {
        var state = new AgyProbeScheduleState(T0, 1_000_000, InProgress: false, BackoffUntil: null);
        var file = AgyScheduleFile.Valid(state);
        var later = T0 + Fifteen;
        var uptimeLater = 1_000_000 + (long)Fifteen.TotalMilliseconds;

        Assert.Equal(AgyScheduleVerdict.Launch, AgyProbeSchedule.Evaluate(file, later, uptimeLater, Fifteen).Verdict);
        Assert.Equal(AgyScheduleVerdict.Wait, AgyProbeSchedule.Evaluate(file, later, uptimeLater - 1, Fifteen).Verdict);
        Assert.Equal(AgyScheduleVerdict.Wait, AgyProbeSchedule.Evaluate(file, later.AddSeconds(-1), uptimeLater, Fifteen).Verdict);
    }

    [Fact]
    public void Serialize_and_parse_round_trip()
    {
        var state = new AgyProbeScheduleState(T0, 123_456, InProgress: true, BackoffUntil: T0.AddHours(1), ConsecutiveFailures: 4, BootId: 4_000_000_000);

        var json = AgyProbeSchedule.Serialize(state);
        var parsed = AgyProbeSchedule.Parse(json);

        Assert.Equal(AgyScheduleFileKind.Valid, parsed.Kind);
        Assert.Equal(state, parsed.State);
        Assert.Contains("\"version\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"backoffUntil\":\"2026-10-30T09:00:00", json, StringComparison.Ordinal);
        Assert.Contains("\"consecutiveFailures\":4", json, StringComparison.Ordinal);
        Assert.Contains("\"bootId\":4000000000", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_boot_id_is_written_as_null_and_read_back()
    {
        var state = new AgyProbeScheduleState(T0, 1, InProgress: false, BackoffUntil: null, ConsecutiveFailures: 0, BootId: null);

        var json = AgyProbeSchedule.Serialize(state);

        Assert.Contains("\"bootId\":null", json, StringComparison.Ordinal);
        Assert.Equal(state, AgyProbeSchedule.Parse(json).State);
    }

    [Theory]
    [InlineData("null", null)]
    [InlineData("0", 0L)]
    [InlineData("12", 12L)]
    public void A_complete_file_parses(string bootId, long? expected)
    {
        var json = """{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null,"consecutiveFailures":0,"bootId":"""
                   + bootId + "}";

        var file = AgyProbeSchedule.Parse(json);

        Assert.Equal(AgyScheduleFileKind.Valid, file.Kind);
        Assert.Equal(expected, file.State!.BootId);
    }

    [Theory]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null,"consecutiveFailures":0}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null,"consecutiveFailures":0,"bootId":-1}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null,"consecutiveFailures":0,"bootId":"7"}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null,"consecutiveFailures":0,"bootId":7.5}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null,"consecutiveFailures":0,"bootId":true}""")]
    public void A_missing_negative_or_non_numeric_boot_id_is_corrupt(string json)
    {
        Assert.Equal(AgyScheduleFileKind.Corrupt, AgyProbeSchedule.Parse(json).Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"version":2,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null}""")]
    [InlineData("""{"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"yesterday","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":-5,"inProgress":false,"backoffUntil":null}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":"no","backoffUntil":null}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":5}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null,"consecutiveFailures":-1,"bootId":null}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null,"consecutiveFailures":"2","bootId":null}""")]
    [InlineData("""{"version":1,"lastAttemptAt":"2026-10-30T08:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null,"consecutiveFailures":1.5,"bootId":null}""")]
    public void Unreadable_unknown_or_incomplete_files_are_corrupt(string json)
    {
        Assert.Equal(AgyScheduleFileKind.Corrupt, AgyProbeSchedule.Parse(json).Kind);
    }

    [Fact]
    public void Third_consecutive_failure_starts_a_backoff_of_at_least_an_hour()
    {
        Assert.Null(AgyProbeSchedule.BackoffAfter(false, 2, null, T0, Fifteen));
        Assert.Equal(T0.AddMinutes(60), AgyProbeSchedule.BackoffAfter(false, 3, null, T0, Fifteen));
        Assert.Equal(T0.AddMinutes(120), AgyProbeSchedule.BackoffAfter(false, 3, null, T0, TimeSpan.FromMinutes(120)));
        Assert.Null(AgyProbeSchedule.BackoffAfter(true, 0, T0.AddHours(1), T0, Fifteen));
    }

    [Fact]
    public void A_new_backoff_never_shortens_a_longer_stored_one()
    {
        var stored = T0.AddMinutes(90);

        Assert.Equal(stored, AgyProbeSchedule.BackoffAfter(false, 3, stored, T0, Fifteen));
        Assert.Equal(stored, AgyProbeSchedule.BackoffAfter(false, 1, stored, T0, Fifteen));
    }

    [Fact]
    public void A_backoff_too_far_in_the_future_is_clamped_to_the_longest_possible_backoff()
    {
        // Same boot: two hours ago at an uptime two hours lower.
        var state = new AgyProbeScheduleState(T0.AddHours(-2), 10_000_000 - 7_200_000, false, BackoffUntil: T0.AddDays(30), ConsecutiveFailures: 5);

        var decision = AgyProbeSchedule.Evaluate(AgyScheduleFile.Valid(state), T0, 10_000_000, Fifteen);

        Assert.Equal(AgyScheduleVerdict.RecordWithoutLaunch, decision.Verdict);
        Assert.Equal(T0 + AgyProbeSchedule.MaximumBackoff, decision.NextAllowedAt);
        Assert.Equal(5, decision.Rewrite!.ConsecutiveFailures);
    }

    [Fact]
    public void Lowering_the_interval_never_shortens_a_backoff_set_by_the_longest_interval()
    {
        // A back-off written while the interval was 240 minutes, then the user lowers it to 5.
        var stored = T0.AddMinutes(239);
        var state = new AgyProbeScheduleState(T0.AddMinutes(-1), 10_000_000 - 60_000, false, BackoffUntil: stored);

        var decision = AgyProbeSchedule.Evaluate(AgyScheduleFile.Valid(state), T0, 10_000_000, TimeSpan.FromMinutes(5));

        Assert.Equal(AgyScheduleVerdict.Wait, decision.Verdict);
        Assert.Equal(stored, decision.NextAllowedAt);
    }

    [Fact]
    public void The_longest_backoff_matches_the_largest_interval_setting()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(costats.Application.Settings.AppSettings.MaxAntigravityIntervalMinutes),
            AgyProbeSchedule.MaximumBackoff);
    }

    [Fact]
    public void A_restart_is_detected_even_when_the_new_uptime_is_larger_than_the_stored_one()
    {
        // Last check at uptime 5 min; the machine restarts and the app starts
        // two hours later at uptime 30 min. Different boot instants.
        var state = new AgyProbeScheduleState(T0, 5 * 60_000, InProgress: false, BackoffUntil: null);
        var now = T0.AddHours(2);

        var decision = AgyProbeSchedule.Evaluate(AgyScheduleFile.Valid(state), now, 30 * 60_000, Fifteen);

        Assert.Equal(AgyScheduleVerdict.RecordWithoutLaunch, decision.Verdict);
        Assert.Equal(now, decision.Rewrite!.LastAttemptAt);
        Assert.Equal(30 * 60_000, decision.Rewrite.LastAttemptUptimeMs);
        Assert.Equal(now + Fifteen, decision.NextAllowedAt);
    }

    [Fact]
    public void The_same_boot_launches_after_the_interval_despite_small_clock_drift()
    {
        const long uptime = 7_200_000;
        var state = new AgyProbeScheduleState(T0, uptime, InProgress: false, BackoffUntil: null);

        // 90 seconds of drift between the wall clock and the uptime is within tolerance.
        var decision = AgyProbeSchedule.Evaluate(
            AgyScheduleFile.Valid(state),
            T0.AddMinutes(20).AddSeconds(90),
            uptime + 20 * 60_000,
            Fifteen);

        Assert.Equal(AgyScheduleVerdict.Launch, decision.Verdict);
    }

    [Fact]
    public void An_out_of_range_backoff_is_saved_clamped_so_the_wait_ends()
    {
        const long uptime = 50_000_000;
        var state = new AgyProbeScheduleState(T0.AddHours(-2), uptime - 7_200_000, false, BackoffUntil: T0.AddDays(30));

        var first = AgyProbeSchedule.Evaluate(AgyScheduleFile.Valid(state), T0, uptime, Fifteen);

        Assert.Equal(AgyScheduleVerdict.RecordWithoutLaunch, first.Verdict);
        Assert.Equal(T0 + AgyProbeSchedule.MaximumBackoff, first.Rewrite!.BackoffUntil);
        Assert.Equal(T0, first.Rewrite.LastAttemptAt);

        var later = AgyProbeSchedule.Evaluate(
            AgyScheduleFile.Valid(first.Rewrite),
            T0 + AgyProbeSchedule.MaximumBackoff,
            uptime + (long)AgyProbeSchedule.MaximumBackoff.TotalMilliseconds,
            Fifteen);

        Assert.Equal(AgyScheduleVerdict.Launch, later.Verdict);
    }

    // Reviewer scenario: first boot 08:00, check at 08:00:30 (uptime 30 s),
    // reboot at 08:01:30, tray starts at 08:21:30 (uptime 20 min). The boot
    // instants differ by only 90 seconds, inside the tolerance.
    private static readonly DateTimeOffset QuickRebootAttempt = new(2026, 10, 30, 8, 0, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset QuickRebootStart = new(2026, 10, 30, 8, 21, 30, TimeSpan.Zero);
    private const long QuickRebootAttemptUptime = 30_000;
    private const long QuickRebootStartUptime = 20 * 60_000;

    [Fact]
    public void A_reboot_within_two_minutes_of_the_previous_boot_is_caught_by_the_boot_id()
    {
        var state = new AgyProbeScheduleState(QuickRebootAttempt, QuickRebootAttemptUptime, false, null, 0, BootId: 41);

        var decision = AgyProbeSchedule.Evaluate(
            AgyScheduleFile.Valid(state), QuickRebootStart, QuickRebootStartUptime, Fifteen, bootId: 42);

        Assert.Equal(AgyScheduleVerdict.RecordWithoutLaunch, decision.Verdict);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 36, 30, TimeSpan.Zero), decision.NextAllowedAt);
        Assert.Equal(42, decision.Rewrite!.BootId);
        Assert.Equal(QuickRebootStart, decision.Rewrite.LastAttemptAt);
    }

    [Fact]
    public void The_same_boot_id_and_a_normal_gap_still_launch()
    {
        var state = new AgyProbeScheduleState(T0, 7_200_000, false, null, 0, BootId: 41);

        var decision = AgyProbeSchedule.Evaluate(
            AgyScheduleFile.Valid(state), T0.AddMinutes(20), 7_200_000 + 20 * 60_000, Fifteen, bootId: 41);

        Assert.Equal(AgyScheduleVerdict.Launch, decision.Verdict);
    }

    [Theory]
    [InlineData(null, 42L)]
    [InlineData(41L, null)]
    [InlineData(null, null)]
    public void An_unknown_boot_id_waits_on_the_first_check_and_then_follows_the_normal_rules(long? storedBootId, long? currentBootId)
    {
        // The quick reboot with a boot id unknown on either side: this process
        // cannot tell the record's boot, so its first check waits a full interval.
        var quick = new AgyProbeScheduleState(QuickRebootAttempt, QuickRebootAttemptUptime, false, null, 0, storedBootId);

        var first = AgyProbeSchedule.Evaluate(
            AgyScheduleFile.Valid(quick), QuickRebootStart, QuickRebootStartUptime, Fifteen, currentBootId, firstCheckInThisProcess: true);

        Assert.Equal(AgyScheduleVerdict.RecordWithoutLaunch, first.Verdict);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 36, 30, TimeSpan.Zero), first.NextAllowedAt);
        Assert.Equal(currentBootId, first.Rewrite!.BootId);

        // A later check in the same process, after a normal interval, launches.
        var second = AgyProbeSchedule.Evaluate(
            AgyScheduleFile.Valid(first.Rewrite),
            QuickRebootStart + Fifteen,
            QuickRebootStartUptime + (long)Fifteen.TotalMilliseconds,
            Fifteen,
            currentBootId,
            firstCheckInThisProcess: false);

        Assert.Equal(AgyScheduleVerdict.Launch, second.Verdict);
    }

    [Theory]
    [InlineData(null, 42L)]
    [InlineData(41L, null)]
    [InlineData(null, null)]
    public void After_the_first_check_an_unknown_boot_id_falls_back_to_the_uptime_rules(long? storedBootId, long? currentBootId)
    {
        // A restart those rules can see is still caught: the boot instants are two hours apart.
        var earlier = new AgyProbeScheduleState(T0, 5 * 60_000, false, null, 0, storedBootId);

        Assert.Equal(
            AgyScheduleVerdict.RecordWithoutLaunch,
            AgyProbeSchedule.Evaluate(AgyScheduleFile.Valid(earlier), T0.AddHours(2), 30 * 60_000, Fifteen, currentBootId).Verdict);
    }

    [Fact]
    public void Known_matching_boot_ids_need_no_extra_wait_on_the_first_check()
    {
        var state = new AgyProbeScheduleState(T0, 7_200_000, false, null, 0, BootId: 41);

        var decision = AgyProbeSchedule.Evaluate(
            AgyScheduleFile.Valid(state), T0.AddMinutes(20), 7_200_000 + 20 * 60_000, Fifteen, bootId: 41, firstCheckInThisProcess: true);

        Assert.Equal(AgyScheduleVerdict.Launch, decision.Verdict);
    }

    [Fact]
    public void A_missing_file_launches_even_on_the_first_check_without_a_boot_id()
    {
        var decision = AgyProbeSchedule.Evaluate(
            AgyScheduleFile.Missing, T0, 1000, Fifteen, bootId: null, firstCheckInThisProcess: true);

        Assert.Equal(AgyScheduleVerdict.Launch, decision.Verdict);
    }

    [Fact]
    public async Task A_fresh_runner_without_a_boot_id_waits_once_for_an_earlier_record_then_launches()
    {
        _uptime.BootId = null;
        // A valid record from an earlier check, one hour ago, that looks like this boot.
        WriteRaw(AgyProbeSchedule.Serialize(new AgyProbeScheduleState(
            T0.AddHours(-1), _uptime.Value - 3_600_000, InProgress: false, BackoffUntil: null, ConsecutiveFailures: 0, BootId: null)));
        var runner = Runner();

        var first = await Run(runner);
        Assert.Equal(AgyProbeStatus.Waiting, first.Status);
        Assert.Equal(T0 + Fifteen, first.NextAllowedAt);
        Assert.Equal(0, _resolveCalls);

        Advance(Fifteen);
        Assert.Equal(AgyProbeStatus.AgyNotFound, (await Run(runner)).Status);
        Assert.Equal(1, _resolveCalls);
    }

    [Fact]
    public async Task A_stored_record_without_a_boot_id_is_rewritten_with_the_known_one_then_follows_the_normal_rules()
    {
        _uptime.BootId = 7;
        WriteRaw(AgyProbeSchedule.Serialize(new AgyProbeScheduleState(
            T0.AddHours(-1), _uptime.Value - 3_600_000, InProgress: false, BackoffUntil: null, ConsecutiveFailures: 0, BootId: null)));
        var runner = Runner();

        Assert.Equal(AgyProbeStatus.Waiting, (await Run(runner)).Status);
        Assert.Equal(7, ReadStored().BootId);
        Assert.Equal(T0, ReadStored().LastAttemptAt);
        Assert.Equal(0, _resolveCalls);

        Advance(Fifteen);
        Assert.Equal(AgyProbeStatus.AgyNotFound, (await Run(runner)).Status);

        // A tray restart in the same boot: the ids now match, so no extra wait.
        Advance(Fifteen);
        Assert.Equal(AgyProbeStatus.AgyNotFound, (await Run(Runner())).Status);
        Assert.Equal(2, _resolveCalls);
    }

    // ---- Through the runner and its file -----------------------------------

    [Fact]
    public async Task The_reviewer_quick_reboot_waits_a_full_interval_through_the_runner()
    {
        _time.Now = QuickRebootAttempt;
        _uptime.Value = QuickRebootAttemptUptime;
        _uptime.BootId = 41;
        await Run(Runner());
        Assert.Equal(41, ReadStored().BootId);

        _time.Now = QuickRebootStart;
        _uptime.Value = QuickRebootStartUptime;
        _uptime.BootId = 42;
        var result = await Run(Runner());

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 36, 30, TimeSpan.Zero), result.NextAllowedAt);
        Assert.Equal(1, _resolveCalls);
        Assert.Equal(42, ReadStored().BootId);
        Assert.Equal(QuickRebootStart, ReadStored().LastAttemptAt);
    }

    [Fact]
    public async Task Every_schedule_write_records_the_current_boot_id()
    {
        _uptime.BootId = 7;
        var runner = Runner();

        await Run(runner); // pre-launch and completion writes
        Assert.Equal(7, ReadStored().BootId);

        WriteRaw("garbage");
        await Run(runner); // rewrite without a launch
        Assert.Equal(7, ReadStored().BootId);
    }

    [Fact]
    public async Task An_unavailable_boot_id_is_stored_as_null()
    {
        _uptime.BootId = null;

        await Run(Runner());

        Assert.Null(ReadStored().BootId);
        Assert.Contains("\"bootId\":null", File.ReadAllText(SchedulePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backoff_from_a_wrong_clock_does_not_keep_moving_forward()
    {
        Write(new AgyProbeScheduleState(T0.AddHours(-1), _uptime.Value - 3_600_000, InProgress: false, BackoffUntil: T0.AddDays(30)));
        var runner = Runner();

        Assert.Equal(AgyProbeStatus.Waiting, (await Run(runner)).Status);
        Advance(TimeSpan.FromHours(5));

        Assert.Equal(AgyProbeStatus.AgyNotFound, (await Run(runner)).Status);
    }

    [Fact]
    public async Task A_restart_with_a_larger_uptime_waits_one_interval()
    {
        // The last check ran five minutes after a boot.
        _uptime.Value = 5 * 60_000;
        var runner = Runner();
        await Run(runner);

        // Reboot: the wall clock moves on two hours, the uptime starts again at 30 minutes.
        _time.Advance(TimeSpan.FromHours(2));
        _uptime.Value = 30 * 60_000;
        var result = await Run(Runner());

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(1, _resolveCalls);
    }

    [Fact]
    public async Task First_check_runs_and_the_next_waits_a_full_interval()
    {
        var runner = Runner();

        var first = await Run(runner);
        Assert.Equal(AgyProbeStatus.AgyNotFound, first.Status);
        var stored = ReadStored();
        Assert.False(stored.InProgress);
        Assert.Equal(T0, stored.LastAttemptAt);

        Advance(TimeSpan.FromMinutes(14));
        var second = await Run(runner);
        Assert.Equal(AgyProbeStatus.Waiting, second.Status);
        Assert.Equal(T0 + Fifteen, second.NextAllowedAt);
        Assert.Equal(1, _resolveCalls);
    }

    [Fact]
    public async Task Restarting_the_tray_keeps_the_shared_schedule()
    {
        await Run(Runner());
        Advance(TimeSpan.FromMinutes(5));

        var afterRestart = await Run(Runner());

        Assert.Equal(AgyProbeStatus.Waiting, afterRestart.Status);
        Assert.Equal(1, _resolveCalls);
    }

    [Fact]
    public async Task Raising_the_interval_during_the_wait_extends_it()
    {
        var runner = Runner();
        await Run(runner);

        Advance(TimeSpan.FromMinutes(20));
        var raised = await Run(runner, TimeSpan.FromMinutes(30));
        Assert.Equal(AgyProbeStatus.Waiting, raised.Status);
        Assert.Equal(T0.AddMinutes(30), raised.NextAllowedAt);

        Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(AgyProbeStatus.AgyNotFound, (await Run(runner, TimeSpan.FromMinutes(30))).Status);
    }

    [Fact]
    public async Task A_corrupted_file_is_recorded_as_an_attempt_now_without_a_launch()
    {
        WriteRaw("{ this is not json");
        var runner = Runner();

        var result = await Run(runner);

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(T0 + Fifteen, result.NextAllowedAt);
        Assert.Equal(0, _resolveCalls);
        Assert.Equal(T0, ReadStored().LastAttemptAt);

        Advance(Fifteen);
        Assert.Equal(AgyProbeStatus.AgyNotFound, (await Run(runner)).Status);
    }

    [Fact]
    public async Task An_unknown_version_is_treated_like_a_corrupted_file()
    {
        WriteRaw("""{"version":9,"lastAttemptAt":"2020-01-01T00:00:00Z","lastAttemptUptimeMs":1,"inProgress":false,"backoffUntil":null}""");

        var result = await Run(Runner());

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(0, _resolveCalls);
        Assert.Equal(AgyScheduleFileKind.Valid, AgyProbeSchedule.Parse(File.ReadAllText(SchedulePath)).Kind);
    }

    [Fact]
    public async Task An_attempt_left_in_progress_by_a_crash_waits_a_full_interval()
    {
        Write(new AgyProbeScheduleState(T0.AddDays(-1), 1, InProgress: true, BackoffUntil: null));

        var result = await Run(Runner());

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(T0 + Fifteen, result.NextAllowedAt);
        Assert.Equal(0, _resolveCalls);
        var stored = ReadStored();
        Assert.False(stored.InProgress);
        Assert.Equal(T0, stored.LastAttemptAt);
        Assert.Equal(_uptime.Value, stored.LastAttemptUptimeMs);
    }

    [Fact]
    public async Task After_a_machine_restart_the_first_check_waits_one_interval()
    {
        // Stored uptime larger than the current one: the machine restarted.
        Write(new AgyProbeScheduleState(T0.AddDays(-1), _uptime.Value + 5_000_000, InProgress: false, BackoffUntil: null));

        var result = await Run(Runner());

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(0, _resolveCalls);
        Assert.Equal(_uptime.Value, ReadStored().LastAttemptUptimeMs);
    }

    [Fact]
    public async Task A_clock_jumping_forward_does_not_shorten_the_wait()
    {
        var runner = Runner();
        await Run(runner);

        _time.Advance(TimeSpan.FromDays(2));
        _uptime.Advance(TimeSpan.FromMinutes(1));
        var result = await Run(runner);

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(1, _resolveCalls);
        // The boot instants no longer match, so the record restarts from now.
        Assert.Equal(_time.Now, ReadStored().LastAttemptAt);
    }

    [Fact]
    public async Task A_clock_jumping_backward_records_an_attempt_now_and_waits()
    {
        var runner = Runner();
        await Run(runner);

        _time.Advance(TimeSpan.FromDays(-1));
        _uptime.Advance(TimeSpan.FromHours(1));
        var result = await Run(runner);

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(1, _resolveCalls);
        Assert.Equal(_time.Now, ReadStored().LastAttemptAt);

        Advance(Fifteen);
        Assert.Equal(AgyProbeStatus.AgyNotFound, (await Run(runner)).Status);
    }

    [Fact]
    public async Task A_stamp_slightly_in_the_future_just_waits()
    {
        // Same, known boot: only the stamp is slightly ahead.
        _uptime.BootId = 9;
        Write(new AgyProbeScheduleState(T0.AddMinutes(1), _uptime.Value - 1, InProgress: false, BackoffUntil: null, BootId: 9));

        var result = await Run(Runner());

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(T0.AddMinutes(1), ReadStored().LastAttemptAt);
    }

    [Fact]
    public async Task A_stamp_far_in_the_future_is_replaced_by_now()
    {
        Write(new AgyProbeScheduleState(T0.AddMinutes(3), _uptime.Value - 1, InProgress: false, BackoffUntil: null));

        var result = await Run(Runner());

        Assert.Equal(AgyProbeStatus.Waiting, result.Status);
        Assert.Equal(T0, ReadStored().LastAttemptAt);
        Assert.Equal(0, _resolveCalls);
    }

    [Fact]
    public async Task A_stored_backoff_is_never_shortened_by_a_shorter_interval()
    {
        Write(new AgyProbeScheduleState(T0.AddHours(-1), _uptime.Value - 3_600_000, InProgress: false, BackoffUntil: T0.AddMinutes(50)));
        var runner = Runner();

        Assert.Equal(AgyProbeStatus.Waiting, (await Run(runner, TimeSpan.FromMinutes(5))).Status);
        Advance(TimeSpan.FromMinutes(49));
        var stillWaiting = await Run(runner, TimeSpan.FromMinutes(5));
        Assert.Equal(AgyProbeStatus.Waiting, stillWaiting.Status);
        Assert.Equal(T0.AddMinutes(50), stillWaiting.NextAllowedAt);

        Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AgyProbeStatus.AgyNotFound, (await Run(runner, TimeSpan.FromMinutes(5))).Status);
    }

    [Fact]
    public async Task Three_failures_in_a_row_store_a_backoff()
    {
        var runner = Runner();

        await Run(runner);
        Assert.Equal(1, ReadStored().ConsecutiveFailures);
        Assert.Null(ReadStored().BackoffUntil);
        Advance(TimeSpan.FromMinutes(16));
        await Run(runner);
        Assert.Equal(2, ReadStored().ConsecutiveFailures);
        Assert.Null(ReadStored().BackoffUntil);
        Advance(TimeSpan.FromMinutes(16));
        await Run(runner);

        var stored = ReadStored();
        Assert.Equal(3, stored.ConsecutiveFailures);
        Assert.Equal(_time.Now.AddMinutes(60), stored.BackoffUntil);
        Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(AgyProbeStatus.Waiting, (await Run(runner)).Status);
    }

    [Fact]
    public async Task The_stored_failure_count_is_carried_through_a_rewrite_and_the_next_failure()
    {
        // Two failures stored by another instance, then a restart: the record is
        // rewritten without a launch, and the count must survive it.
        Write(new AgyProbeScheduleState(T0.AddDays(-1), _uptime.Value + 5_000_000, InProgress: false, BackoffUntil: null, ConsecutiveFailures: 2));
        var runner = Runner();

        Assert.Equal(AgyProbeStatus.Waiting, (await Run(runner)).Status);
        Assert.Equal(2, ReadStored().ConsecutiveFailures);

        Advance(Fifteen);
        Assert.Equal(AgyProbeStatus.AgyNotFound, (await Run(runner)).Status);
        Assert.Equal(3, ReadStored().ConsecutiveFailures);
        Assert.Equal(_time.Now + AgyProbeSchedule.MinimumBackoff, ReadStored().BackoffUntil);
    }

    [Fact]
    public async Task A_failed_rewrite_blocks_the_launch()
    {
        // The first run does the start-up sweep, which would remove the blocker below.
        var runner = Runner();
        await Run(runner);
        WriteRaw("garbage");
        // A folder where the temporary file must go makes every write fail.
        Directory.CreateDirectory(Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleTempFileName));
        Advance(TimeSpan.FromHours(1));

        var result = await Run(runner);

        Assert.Equal(AgyProbeStatus.StorageFailed, result.Status);
        Assert.Equal(1, _resolveCalls);
    }

    [Fact]
    public async Task A_failed_start_stamp_blocks_the_launch()
    {
        var runner = Runner();
        await Run(runner);
        File.Delete(SchedulePath);
        Directory.CreateDirectory(Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleTempFileName));

        var result = await Run(runner);

        Assert.Equal(AgyProbeStatus.StorageFailed, result.Status);
        Assert.Equal(1, _resolveCalls);
    }

    [Fact]
    public async Task A_leftover_temp_file_is_never_read()
    {
        Directory.CreateDirectory(ProbeDirectory);
        File.WriteAllText(
            Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleTempFileName),
            AgyProbeSchedule.Serialize(new AgyProbeScheduleState(T0, _uptime.Value, false, null)));

        var result = await Run(Runner());

        Assert.Equal(AgyProbeStatus.AgyNotFound, result.Status);
    }

    private string ProbeDirectory => Path.Combine(_basePath, "AIUsageTray", AgyProbeRunner.FolderName);

    private string SchedulePath => Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleFileName);

    private AgyProbeRunner Runner() =>
        new(null, new AgyProbeRunnerOptions
        {
            BasePath = _basePath,
            ResolveExecutable = () =>
            {
                Interlocked.Increment(ref _resolveCalls);
                return null;
            },
            Time = _time,
            Uptime = _uptime
        });

    private static Task<AgyProbeResult> Run(AgyProbeRunner runner, TimeSpan? interval = null) =>
        runner.RunAsync(new AgyProbeRequest(interval ?? Fifteen), CancellationToken.None);

    private void Advance(TimeSpan by)
    {
        _time.Advance(by);
        _uptime.Advance(by);
    }

    private AgyProbeScheduleState ReadStored()
    {
        var file = AgyProbeSchedule.Parse(File.ReadAllText(SchedulePath));
        Assert.Equal(AgyScheduleFileKind.Valid, file.Kind);
        return file.State!;
    }

    private void Write(AgyProbeScheduleState state) => WriteRaw(AgyProbeSchedule.Serialize(state));

    private void WriteRaw(string text)
    {
        Directory.CreateDirectory(ProbeDirectory);
        File.WriteAllText(SchedulePath, text);
    }
}
