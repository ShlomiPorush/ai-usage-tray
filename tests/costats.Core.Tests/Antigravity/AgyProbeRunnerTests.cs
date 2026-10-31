using System.Diagnostics;
using costats.Application.Settings;
using costats.Core.Antigravity;
using costats.Infrastructure.Antigravity;
using Xunit;

namespace costats.Core.Tests.Antigravity;

/// <summary>
/// The runner on real Windows processes. agy is never started: every test
/// uses cmd.exe as a stand-in, and the long-running stand-ins start a child
/// (ping) so the job's hold on the whole process tree is checked.
/// </summary>
public sealed class AgyProbeRunnerTests : IDisposable
{
    private static readonly TimeSpan Fifteen = TimeSpan.FromMinutes(15);

    private readonly string _basePath = AgyFixtures.NewBasePath("runner");
    private readonly string _fixtureDirectory;

    public AgyProbeRunnerTests()
    {
        // A space in the path, like the real profile folder.
        _fixtureDirectory = Path.Combine(_basePath, "fix ture");
        Directory.CreateDirectory(_fixtureDirectory);
    }

    public void Dispose() => AgyFixtures.DeleteQuietly(_basePath);

    private string ProbeDirectory => Path.Combine(_basePath, "AIUsageTray", AgyProbeRunner.FolderName);

    [Fact]
    public void A_started_process_is_in_its_job_with_only_kill_on_close_and_terminate_ends_the_child()
    {
        var workDirectory = Directory.CreateDirectory(Path.Combine(_basePath, "direct")).FullName;
        using var job = JobObjectProcess.Start(
            AgyFixtures.CmdExe,
            LongRunningCommand(),
            workDirectory,
            AgyProbeRunner.BuildEnvironment());

        Assert.True(job.IsInJob());
        Assert.Equal(JobObjectProcess.JobObjectLimitKillOnJobClose, job.QueryLimitFlags());

        WaitUntil(() => job.ActiveProcessCount() >= 2, "the child (ping) never started");
        var ids = job.ProcessIds();
        Assert.Contains(job.ProcessId, ids);
        Assert.True(ids.Count >= 2);

        job.Terminate(TimeSpan.FromSeconds(5));

        AssertAllExited(ids);
        WaitUntil(() => job.ActiveProcessCount() == 0, "processes survived TerminateJobObject");
    }

    [Fact]
    public void Closing_the_job_without_terminate_still_ends_the_whole_tree()
    {
        var workDirectory = Directory.CreateDirectory(Path.Combine(_basePath, "close")).FullName;
        IReadOnlyList<int> ids;
        using (var job = JobObjectProcess.Start(
                   AgyFixtures.CmdExe,
                   LongRunningCommand(),
                   workDirectory,
                   AgyProbeRunner.BuildEnvironment()))
        {
            WaitUntil(() => job.ActiveProcessCount() >= 2, "the child (ping) never started");
            ids = job.ProcessIds();
        }

        AssertAllExited(ids);
    }

    [Fact]
    public async Task Success_reads_the_payload_ends_the_tree_and_deletes_the_probe_folder()
    {
        var fixture = WriteFixture("payload.json", AgyFixtures.RealPayload);
        var launch = new LaunchRecorder(waitForChild: true);
        var runner = Runner(CopyThenWaitCommand(fixture), launch);

        var result = await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.Success, result.Status);
        Assert.Equal(100, result.Quota!.GeminiWeekly!.UsedPercent);
        Assert.Equal(1, launch.Count);
        Assert.True(launch.InJob);
        AssertAllExited(launch.ProcessIds);
        AssertNoProbeFolders();

        var stored = AgyProbeSchedule.Parse(File.ReadAllText(Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleFileName)));
        Assert.False(stored.State!.InProgress);
        Assert.Null(stored.State.BackoffUntil);
    }

    [Fact]
    public async Task The_plan_reported_after_the_quota_is_waited_for()
    {
        var withoutPlan = WriteFixture("early.json", AgyFixtures.RealPayloadWithoutPlan);
        var withPlan = WriteFixture("payload.json", AgyFixtures.RealPayload);
        var command = Quote(AgyFixtures.CmdExe) +
            $" /d /c copy /y {Quote(withoutPlan)} payload.json >nul & ping -n 2 127.0.0.1 >nul & copy /y {Quote(withPlan)} payload.json >nul & ping -n 60 127.0.0.1 >nul";
        var runner = Runner(command, planGrace: TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.Success, result.Status);
        Assert.Equal("Google AI Plus", result.Quota!.PlanTier);
        AssertNoProbeFolders();
    }

    [Fact]
    public async Task Quota_without_a_plan_succeeds_when_the_plan_grace_ends()
    {
        var fixture = WriteFixture("payload.json", AgyFixtures.RealPayloadWithoutPlan);
        var launch = new LaunchRecorder(waitForChild: true);
        var runner = Runner(CopyThenWaitCommand(fixture), launch, planGrace: TimeSpan.FromMilliseconds(500));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.Success, result.Status);
        Assert.Null(result.Quota!.PlanTier);
        Assert.Equal(100, result.Quota.GeminiWeekly!.UsedPercent);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "the grace, not the deadline, ended the wait");
        AssertAllExited(launch.ProcessIds);
        AssertNoProbeFolders();
    }

    [Fact]
    public async Task A_half_written_payload_is_read_again_on_the_next_poll()
    {
        var partial = WriteFixture("partial.json", "{\"quota\":{\"gemini-weekly\":");
        var full = WriteFixture("payload.json", AgyFixtures.RealPayload);
        var command = Quote(AgyFixtures.CmdExe) +
            $" /d /c copy /y {Quote(partial)} payload.json >nul & ping -n 2 127.0.0.1 >nul & copy /y {Quote(full)} payload.json >nul & ping -n 60 127.0.0.1 >nul";
        var runner = Runner(command);

        var result = await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.Success, result.Status);
        AssertNoProbeFolders();
    }

    [Fact]
    public async Task Early_exit_without_a_payload_is_no_quota_before_the_deadline()
    {
        var runner = Runner(Quote(AgyFixtures.CmdExe) + " /d /c exit 3", deadline: TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.NoQuota, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
        AssertNoProbeFolders();
    }

    [Fact]
    public async Task The_deadline_ends_a_process_that_never_reports_quota()
    {
        var launch = new LaunchRecorder(waitForChild: true);
        var runner = Runner(LongRunningCommand(), launch, TimeSpan.FromSeconds(1));

        var result = await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.NoQuota, result.Status);
        AssertAllExited(launch.ProcessIds);
        AssertNoProbeFolders();
    }

    [Fact]
    public async Task Quota_without_gemini_is_unsupported_format()
    {
        var fixture = WriteFixture("payload.json", AgyFixtures.ThirdPartyOnlyPayload);
        var runner = Runner(CopyThenWaitCommand(fixture), deadline: TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.UnsupportedFormat, result.Status);
        AssertNoProbeFolders();
    }

    [Fact]
    public async Task A_missing_executable_fails_to_launch_and_leaves_nothing_behind()
    {
        var runner = new AgyProbeRunner(null, new AgyProbeRunnerOptions
        {
            BasePath = _basePath,
            ResolveExecutable = () => Path.Combine(_basePath, "missing", "agy.exe")
        });

        var result = await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.LaunchFailed, result.Status);
        AssertNoProbeFolders();
    }

    [Fact]
    public async Task Caller_cancellation_ends_the_process_and_is_reported_as_cancelled()
    {
        var launch = new LaunchRecorder(waitForChild: true);
        var runner = Runner(LongRunningCommand(), launch, TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource();

        var run = runner.RunAsync(new AgyProbeRequest(Fifteen), cancel.Token);
        await launch.Launched.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgyProbeStatus.Cancelled, result.Status);
        AssertAllExited(launch.ProcessIds);
        AssertNoProbeFolders();
        var stored = AgyProbeSchedule.Parse(File.ReadAllText(Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleFileName)));
        Assert.False(stored.State!.InProgress);
    }

    [Fact]
    public async Task Cancellation_keeps_the_stored_failure_count_and_backoff()
    {
        var start = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var uptime = new FakeUptime(50_000_000) { BootId = 7 };
        var expiredBackoff = start.AddMinutes(-5);
        Directory.CreateDirectory(ProbeDirectory);
        File.WriteAllText(
            Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleFileName),
            AgyProbeSchedule.Serialize(new AgyProbeScheduleState(
                start.AddHours(-2), uptime.Value - 7_200_000, InProgress: false, expiredBackoff, ConsecutiveFailures: 3, BootId: 7)));
        var launch = new LaunchRecorder(waitForChild: false);
        var runner = new AgyProbeRunner(null, new AgyProbeRunnerOptions
        {
            BasePath = _basePath,
            ResolveExecutable = () => AgyFixtures.CmdExe,
            CommandLine = (_, _) => LongRunningCommand(),
            Deadline = TimeSpan.FromSeconds(30),
            PlanGrace = TimeSpan.Zero,
            Time = time,
            Uptime = uptime,
            OnLaunched = launch.Record
        });
        using var cancel = new CancellationTokenSource();

        var run = runner.RunAsync(new AgyProbeRequest(Fifteen), cancel.Token);
        await launch.Launched.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        Assert.Equal(AgyProbeStatus.Cancelled, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Status);

        var stored = AgyProbeSchedule.Parse(File.ReadAllText(Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleFileName))).State!;
        Assert.Equal(3, stored.ConsecutiveFailures);
        Assert.Equal(expiredBackoff, stored.BackoffUntil);
        Assert.False(stored.InProgress);
    }

    [Fact]
    public async Task A_success_resets_the_stored_failure_count_and_backoff()
    {
        var start = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var uptime = new FakeUptime(50_000_000) { BootId = 7 };
        Directory.CreateDirectory(ProbeDirectory);
        File.WriteAllText(
            Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleFileName),
            AgyProbeSchedule.Serialize(new AgyProbeScheduleState(
                start.AddHours(-2), uptime.Value - 7_200_000, InProgress: false, start.AddMinutes(-5), ConsecutiveFailures: 4, BootId: 7)));
        var fixture = WriteFixture("payload.json", AgyFixtures.RealPayload);
        var runner = new AgyProbeRunner(null, new AgyProbeRunnerOptions
        {
            BasePath = _basePath,
            ResolveExecutable = () => AgyFixtures.CmdExe,
            CommandLine = (_, _) => CopyThenWaitCommand(fixture),
            PlanGrace = TimeSpan.Zero,
            Time = time,
            Uptime = uptime
        });

        Assert.Equal(AgyProbeStatus.Success, (await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None)).Status);

        var stored = AgyProbeSchedule.Parse(File.ReadAllText(Path.Combine(ProbeDirectory, AgyProbeRunner.ScheduleFileName))).State!;
        Assert.Equal(0, stored.ConsecutiveFailures);
        Assert.Null(stored.BackoffUntil);
    }

    [Fact]
    public async Task A_held_lock_means_busy_and_nothing_starts()
    {
        Directory.CreateDirectory(ProbeDirectory);
        using var held = new FileStream(
            Path.Combine(ProbeDirectory, AgyProbeRunner.LockFileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var launch = new LaunchRecorder(waitForChild: false);

        var result = await Runner(LongRunningCommand(), launch).RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.Busy, result.Status);
        Assert.Equal(0, launch.Count);
    }

    [Fact]
    public async Task A_second_runner_is_busy_while_the_first_one_runs()
    {
        var first = new LaunchRecorder(waitForChild: false);
        var second = new LaunchRecorder(waitForChild: false);
        var running = Runner(LongRunningCommand(), first, TimeSpan.FromSeconds(3))
            .RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);
        await first.Launched.WaitAsync(TimeSpan.FromSeconds(10));

        var result = await Runner(LongRunningCommand(), second).RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.Busy, result.Status);
        Assert.Equal(0, second.Count);
        Assert.Equal(AgyProbeStatus.NoQuota, (await running).Status);
    }

    [Fact]
    public async Task Two_sources_racing_start_exactly_one_check()
    {
        var launches = 0;
        AntigravityAgySource NewSource() => new(
            new AppSettings { AntigravityEnabled = true },
            new AgyProbeRunner(null, new AgyProbeRunnerOptions
            {
                BasePath = _basePath,
                ResolveExecutable = () => AgyFixtures.CmdExe,
                CommandLine = (_, _) => LongRunningCommand(),
                Deadline = TimeSpan.FromSeconds(1.5),
                OnLaunched = _ => Interlocked.Increment(ref launches)
            }),
            null,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50));
        var sources = Enumerable.Range(0, 4).Select(_ => NewSource()).ToList();
        using var start = new ManualResetEventSlim();

        var reads = sources
            .Select(source => Task.Run(async () =>
            {
                start.Wait();
                return await source.ReadAsync(CancellationToken.None);
            }))
            .ToList();
        start.Set();
        await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, launches);
        AssertNoProbeFolders();
    }

    [Fact]
    public async Task A_lock_that_cannot_be_opened_is_storage_failed()
    {
        // A folder named like the lock file cannot be opened as a file: access denied.
        Directory.CreateDirectory(Path.Combine(ProbeDirectory, AgyProbeRunner.LockFileName));
        var launch = new LaunchRecorder(waitForChild: false);

        var result = await Runner(LongRunningCommand(), launch).RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.StorageFailed, result.Status);
        Assert.Equal(0, launch.Count);
    }

    [Fact]
    public async Task A_data_folder_that_cannot_be_created_is_storage_failed()
    {
        var dataRoot = Path.Combine(_basePath, "AIUsageTray");
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, AgyProbeRunner.FolderName), "a file where the folder should be");
        var launch = new LaunchRecorder(waitForChild: false);

        var result = await Runner(LongRunningCommand(), launch).RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        Assert.Equal(AgyProbeStatus.StorageFailed, result.Status);
        Assert.Equal(0, launch.Count);
    }

    [Fact]
    public async Task The_first_run_sweeps_leftover_probe_folders_and_nothing_else()
    {
        var leftover = Directory.CreateDirectory(Path.Combine(ProbeDirectory, "0123456789abcdef0123456789abcdef")).FullName;
        File.WriteAllText(Path.Combine(leftover, AgyProbeRunner.PayloadFileName), AgyFixtures.RealPayloadWithPlaceholderEmail);
        Directory.CreateDirectory(Path.Combine(ProbeDirectory, "empty-leftover"));
        var unrelated = Path.Combine(ProbeDirectory, "keep.txt");
        File.WriteAllText(unrelated, "not ours to delete");
        var runner = new AgyProbeRunner(null, new AgyProbeRunnerOptions
        {
            BasePath = _basePath,
            ResolveExecutable = () => null
        });

        await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);

        AssertNoProbeFolders();
        Assert.True(File.Exists(unrelated));

        // Only once per runner lifetime: a folder that appears later stays until the next start.
        var later = Directory.CreateDirectory(Path.Combine(ProbeDirectory, "later")).FullName;
        await runner.RunAsync(new AgyProbeRequest(Fifteen), CancellationToken.None);
        Assert.True(Directory.Exists(later));
    }

    [Fact]
    public void A_payload_that_grows_past_the_limit_while_it_is_read_is_invalid()
    {
        // agy may still be appending: the length seen when the file is opened
        // is small, the bytes that follow are not.
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            AgyFixtures.RealPayload + new string(' ', AgyProbeRunner.MaxPayloadBytes + 10));
        using var stream = new GrowingStream(bytes, reportedLength: 100);

        Assert.Equal(AgyPayloadKind.Invalid, AgyProbeRunner.ReadPayload(stream).Kind);
    }

    [Fact]
    public void A_payload_file_larger_than_the_limit_is_invalid()
    {
        var path = WriteFixture("huge.json", AgyFixtures.RealPayload + new string(' ', AgyProbeRunner.MaxPayloadBytes + 10));
        using var stream = File.OpenRead(path);

        Assert.Equal(AgyPayloadKind.Invalid, AgyProbeRunner.ReadPayload(stream).Kind);
    }

    [Fact]
    public void A_payload_within_the_limit_is_read()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(AgyFixtures.RealPayload));

        Assert.Equal(AgyPayloadKind.Accepted, AgyProbeRunner.ReadPayload(stream).Kind);
    }

    /// <summary>A stream that reports a smaller length than it delivers, like a file still being written.</summary>
    private sealed class GrowingStream(byte[] content, long reportedLength) : MemoryStream(content)
    {
        public override long Length => reportedLength;
    }

    [Fact]
    public void The_agy_command_line_passes_the_inline_settings_and_the_log_inside_the_probe_folder()
    {
        var commandLine = AgyProbeRunner.BuildAgyCommandLine(
            @"C:\Users\Some One\AppData\Local\agy\bin\agy.exe",
            @"C:\Users\Some One\AppData\Local\AIUsageTray\antigravity-probe\abc");

        Assert.Equal(
            "\"C:\\Users\\Some One\\AppData\\Local\\agy\\bin\\agy.exe\" --settings " +
            "\"{\\\"statusLine\\\":{\\\"type\\\":\\\"command\\\",\\\"command\\\":\\\"more > payload.json\\\"}}\" " +
            "--log-file \"C:\\Users\\Some One\\AppData\\Local\\AIUsageTray\\antigravity-probe\\abc\\agy.log\"",
            commandLine);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\path with space\", "\"C:\\path with space\\\\\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    public void Arguments_are_quoted_with_the_windows_argv_rules(string argument, string expected)
    {
        var builder = new System.Text.StringBuilder();
        JobObjectProcess.AppendQuotedArgument(builder, argument);

        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void The_environment_disables_agy_auto_update_and_the_block_is_sorted_and_double_terminated()
    {
        var environment = AgyProbeRunner.BuildEnvironment();
        Assert.Equal("true", environment["AGY_CLI_DISABLE_AUTO_UPDATE"]);
        Assert.False(string.IsNullOrEmpty(environment["USERPROFILE"]));

        var block = JobObjectProcess.BuildEnvironmentBlock(new Dictionary<string, string>
        {
            ["b"] = "2",
            ["A"] = "1"
        });
        Assert.Equal("A=1\0b=2\0\0", block);
    }

    private AgyProbeRunner Runner(
        string commandLine,
        LaunchRecorder? launch = null,
        TimeSpan? deadline = null,
        TimeSpan? planGrace = null) =>
        new(null, new AgyProbeRunnerOptions
        {
            BasePath = _basePath,
            ResolveExecutable = () => AgyFixtures.CmdExe,
            CommandLine = (_, _) => commandLine,
            Deadline = deadline ?? TimeSpan.FromSeconds(15),
            PlanGrace = planGrace ?? TimeSpan.Zero,
            OnLaunched = launch is null ? null : launch.Record
        });

    private static string LongRunningCommand() =>
        Quote(AgyFixtures.CmdExe) + " /d /c ping -n 60 127.0.0.1 >nul";

    private static string CopyThenWaitCommand(string fixture) =>
        Quote(AgyFixtures.CmdExe) + $" /d /c copy /y {Quote(fixture)} payload.json >nul & ping -n 60 127.0.0.1 >nul";

    private static string Quote(string path) => "\"" + path + "\"";

    private string WriteFixture(string name, string content)
    {
        var path = Path.Combine(_fixtureDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private void AssertNoProbeFolders()
    {
        if (Directory.Exists(ProbeDirectory))
        {
            Assert.Empty(Directory.GetDirectories(ProbeDirectory));
        }
    }

    private static void AssertAllExited(IEnumerable<int> processIds)
    {
        foreach (var id in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                Assert.True(process.WaitForExit(5000), $"process {id} is still running");
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, message);
            Thread.Sleep(20);
        }
    }

    private sealed class LaunchRecorder(bool waitForChild)
    {
        private readonly TaskCompletionSource _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public bool InJob { get; private set; }

        public IReadOnlyList<int> ProcessIds { get; private set; } = [];

        public Task Launched => _launched.Task;

        public void Record(JobObjectProcess job)
        {
            Interlocked.Increment(ref _count);
            InJob = job.IsInJob();
            if (waitForChild)
            {
                WaitUntil(() => job.ActiveProcessCount() >= 2, "the child (ping) never started");
            }

            ProcessIds = job.ProcessIds();
            _launched.TrySetResult();
        }
    }
}
