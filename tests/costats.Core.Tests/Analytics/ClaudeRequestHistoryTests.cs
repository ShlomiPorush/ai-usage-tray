using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Core.Remote;
using costats.Infrastructure.Analytics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace costats.Core.Tests.Analytics;

/// <summary>
/// The usage history keeps Claude requests by identity once it has converted:
/// requests counted at the conversion are marks, later ones are records. These
/// tests drive the analytics service over real log files in a temporary folder.
/// </summary>
public sealed class ClaudeRequestHistoryTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "costats-claude-request-tests", Guid.NewGuid().ToString("n"));

    public ClaudeRequestHistoryTests(ITestOutputHelper output) => _output = output;

    private string HistoryDir => Path.Combine(_root, "history");

    private string StorePath => Path.Combine(HistoryDir, "claude-requests.db");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    // -- Helpers -------------------------------------------------------------

    private const string ClaudeTemplate = """
        {"type":"assistant","timestamp":"@TS@","requestId":"@RID@","sessionId":"s1","uuid":"u1","message":{"id":"@MID@","role":"assistant","model":"@MODEL@","content":[{"type":"text","text":"x"}],"stop_reason":"end_turn","usage":{"input_tokens":@INPUT@,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":5,"service_tier":"standard"}}}
        """;

    // A line without a requestId has no identity (DedupKey 0).
    private const string KeylessTemplate = """
        {"type":"assistant","timestamp":"@TS@","sessionId":"s1","uuid":"u1","message":{"id":"@MID@","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"x"}],"stop_reason":"end_turn","usage":{"input_tokens":@INPUT@,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":5,"service_tier":"standard"}}}
        """;

    private const string CodexTurnContext = """
        {"timestamp":"@TS@","type":"turn_context","payload":{"turn_id":"t1","cwd":"C:\\work","model":"gpt-5.6-sol","effort":"high"}}
        """;

    private const string CodexTokenCount = """
        {"timestamp":"@TS@","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":999999,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":999999,"reasoning_output_tokens":0,"total_tokens":999999},"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":10,"reasoning_output_tokens":0,"total_tokens":110},"model_context_window":258400}}}
        """;

    private static string Line(string id, string timestamp, long input = 10, string model = "claude-opus-5") => ClaudeTemplate
        .Replace("@TS@", timestamp, StringComparison.Ordinal)
        .Replace("@RID@", "req_" + id, StringComparison.Ordinal)
        .Replace("@MID@", "msg_" + id, StringComparison.Ordinal)
        .Replace("@MODEL@", model, StringComparison.Ordinal)
        .Replace("@INPUT@", input.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string Line(string id, DateTimeOffset timestamp, long input = 10, string model = "claude-opus-5") =>
        Line(id, Ts(timestamp), input, model);

    private static string KeylessLine(string id, DateTimeOffset timestamp, long input = 10) => KeylessTemplate
        .Replace("@TS@", Ts(timestamp), StringComparison.Ordinal)
        .Replace("@MID@", "msg_" + id, StringComparison.Ordinal)
        .Replace("@INPUT@", input.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string Ts(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset At(string instant) =>
        DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// One minute into a quarter hour that starts 15 to 30 minutes from now.
    /// A delete there has its point well inside one bucket, whatever the time
    /// of day, so tests never wait for a bucket boundary.
    /// </summary>
    private static DateTimeOffset QuarterHourAhead() =>
        DateTimeOffset.FromUnixTimeSeconds(UsageHistory.BucketStartOf(DateTimeOffset.UtcNow) + 2 * UsageHistory.BucketSeconds + 60);

    /// <summary>A time after any conversion a test already ran: it gets recorded.</summary>
    private static DateTimeOffset AfterConversion(double seconds = 5) => DateTimeOffset.UtcNow.AddSeconds(seconds);

    private string LogPath(string configDir, string name = "s.jsonl") => Path.Combine(_root, configDir, "projects", "p", name);

    private string WriteLog(string configDir, string name, params string[] lines)
    {
        var path = LogPath(configDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    private void EmptyClaudeFolder(string configDir) =>
        Directory.CreateDirectory(Path.Combine(_root, configDir, "projects"));

    private MonitoredAccountSettings Claude(string id, string dir, string? name = null) => new()
    {
        Id = id,
        Type = MonitoredAccountSettings.ClaudeType,
        DisplayName = name ?? id,
        ConfigDir = Path.Combine(_root, dir)
    };

    private UsageAnalyticsService Service(
        Func<IReadOnlyList<MonitoredAccountSettings>> accounts,
        int retentionDays = 0,
        UsageFileCache? cache = null,
        Func<int>? retention = null,
        UsageHistoryStore? store = null) =>
        new(new UsageLogCollector(accounts, cache ?? UsageFileCache.Disabled), ModelPricingTable.Default, freshness: TimeSpan.Zero)
        {
            TimeZone = TimeZoneInfo.Utc,
            History = new UsageHistoryOptions(store ?? new UsageHistoryStore(HistoryDir), accounts, retention ?? (() => retentionDays))
        };

    private static async Task<long> Requests(UsageAnalyticsService service, params string[] accountIds) =>
        (await service.GetReportAsync(UsageDateRange.All, accountIds.Length == 0 ? null : accountIds)).Totals.RequestCount;

    private SqliteConnection OpenStore()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StorePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private (long Marks, long Records) StoreRows()
    {
        using var connection = OpenStore();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(kind = 1), 0), COALESCE(SUM(kind = 0), 0) FROM request;";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private void SetStoreMeta(string name, string value)
    {
        using var connection = OpenStore();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE meta SET value = $value WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A store as a conversion at <paramref name="freeze"/> would have left it,
    /// so a test can record requests with timestamps in the past.
    /// </summary>
    private void SeedStore(DateTimeOffset freeze)
    {
        Directory.CreateDirectory(HistoryDir);
        Assert.True(new ClaudeRequestStore(HistoryDir, NullLogger.Instance)
            .Create(new ClaudeStoreMeta(freeze.ToUnixTimeMilliseconds(), null, null), [], []));
    }

    private void EditState(Action<JsonObject> edit)
    {
        foreach (var path in new[] { Path.Combine(HistoryDir, "state.json"), Path.Combine(HistoryDir, "state.json.bak") })
        {
            if (File.Exists(path))
            {
                var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                edit(state);
                File.WriteAllText(path, state.ToJsonString());
            }
        }
    }

    private static UsageHistoryKey KeyFor(MonitoredAccountSettings account, DateTimeOffset instant, string model = "claude-opus-5") =>
        new(UsageHistory.BucketStartOf(instant), UsageProviderKind.Claude, UsageLogRootResolver.HistoryKeyFor(account), model);

    private static UsageHistoryBucket Bucket(long input, long requests = 1) =>
        new(new UsageTokens { UncachedInputTokens = input, OutputTokens = 5 }, requests);

    internal static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null || !process.WaitForExit(10_000))
            {
                return false;
            }

            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // -- The probe scenarios: requests that existed at the conversion (marks) --

    [Fact]
    public async Task Probe_folder_edited_to_a_copy()
    {
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"), Line("2", "2026-08-21T12:07:00Z"));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        WriteLog("c2", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"), Line("2", "2026-08-21T12:07:00Z"));
        accounts = [Claude("claude-1", "c2")];
        await service.SyncHistoryAsync();

        Assert.Equal(2, await Requests(service));
    }

    [Fact]
    public async Task Probe_logs_moved_to_another_account()
    {
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"), Line("2", "2026-08-21T12:07:00Z"));
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        WriteLog("c2", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"), Line("2", "2026-08-21T12:07:00Z"));
        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();

        Assert.Equal(2, await Requests(service));
    }

    [Fact]
    public async Task Probe_relogin_in_the_same_folder()
    {
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"), Line("2", "2026-08-21T12:07:00Z"));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        accounts = [Claude("claude-7", "c1")];
        await service.SyncHistoryAsync();

        Assert.Equal(2, await Requests(service));
    }

    [Fact]
    public async Task Pre_conversion_requests_keep_counting_once_after_their_logs_move_and_vanish()
    {
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"), Line("2", "2026-08-21T12:07:00Z"));
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.Equal((2L, 0L), StoreRows());

        WriteLog("c2", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"), Line("2", "2026-08-21T12:07:00Z"));
        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service, "claude-1"));
        Assert.Equal(0, await Requests(service, "claude-2"));

        File.Delete(LogPath("c2"));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
        Assert.DoesNotContain(new UsageHistoryStore(HistoryDir).Read().Buckets.Keys,
            key => key.AccountKey == UsageLogRootResolver.HistoryKeyFor(Claude("claude-2", "c2")));
    }

    // -- After the conversion: records --------------------------------------

    [Fact]
    public async Task After_conversion_a_folder_edited_to_a_copy_counts_once_also_after_the_logs_are_deleted()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.True(File.Exists(StorePath));

        var t = AfterConversion();
        string[] lines = [Line("1", t), Line("2", t.AddSeconds(30))];
        WriteLog("c1", "s.jsonl", lines);
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));

        WriteLog("c2", "s.jsonl", lines);
        accounts = [Claude("claude-1", "c2")];
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));

        File.Delete(LogPath("c1"));
        File.Delete(LogPath("c2"));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
        Assert.Equal((0L, 2L), StoreRows());
    }

    [Fact]
    public async Task After_conversion_a_session_moved_between_accounts_counts_once()
    {
        EmptyClaudeFolder("c1");
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        var t = AfterConversion();
        string[] lines = [Line("1", t), Line("2", t.AddSeconds(30))];
        WriteLog("c1", "s.jsonl", lines);
        await service.SyncHistoryAsync();

        WriteLog("c2", "s.jsonl", lines);
        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
        Assert.Equal(2, await Requests(service, "claude-1"));

        File.Delete(LogPath("c2"));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
    }

    [JunctionFact]
    public async Task Removing_the_owner_of_a_shared_folder_counts_its_requests_once()
    {
        // c2's projects folder is a junction to c1's: the scan reads those
        // files once, for the first account. Removing that account hands them
        // to the second one, under another history key.
        Directory.CreateDirectory(Path.Combine(_root, "c1", "projects"));
        Directory.CreateDirectory(Path.Combine(_root, "c2"));
        Assert.True(
            TryCreateJunction(Path.Combine(_root, "c2", "projects"), Path.Combine(_root, "c1", "projects")),
            "A junction could be created at discovery but not here.");

        WriteLog("c1", "old.jsonl", Line("1", "2026-08-21T12:01:00Z"));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        WriteLog("c1", "new.jsonl", Line("2", AfterConversion()));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));

        accounts = [Claude("claude-2", "c2")];
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));

        File.Delete(LogPath("c1", "old.jsonl"));
        File.Delete(LogPath("c1", "new.jsonl"));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
    }

    [Fact]
    public async Task A_recorded_request_counts_once_whatever_its_copies_change()
    {
        EmptyClaudeFolder("c1");
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        var t = AfterConversion();
        WriteLog("c1", "s.jsonl", Line("1", t, input: 10));
        await service.SyncHistoryAsync();

        // A copy in another account, another month, with another model and
        // other token counts: the first recorded copy wins.
        WriteLog("c2", "s.jsonl", Line("1", t.AddDays(40), input: 500, model: "claude-sonnet-5"));
        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();

        var report = await service.GetReportAsync(UsageDateRange.All);
        Assert.Equal(1, report.Totals.RequestCount);
        Assert.Equal(15, report.Totals.Tokens.ProcessedTokens);
        Assert.Equal(1, await Requests(service, "claude-1"));
        Assert.Equal((0L, 1L), StoreRows());
    }

    [Fact]
    public async Task A_marked_copy_lands_in_the_bucket_it_was_counted_in()
    {
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z", input: 10));
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        WriteLog("c2", "s.jsonl", Line("1", "2026-09-15T08:00:00Z", input: 500, model: "claude-sonnet-5"));
        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();

        var report = await service.GetReportAsync(UsageDateRange.All);
        Assert.Equal(1, report.Totals.RequestCount);
        Assert.Equal(15, report.Totals.Tokens.ProcessedTokens);
        Assert.Equal(new DateOnly(2026, 8, 21), Assert.Single(report.Daily).Day);
        Assert.Equal(1, await Requests(service, "claude-1"));
        var buckets = new UsageHistoryStore(HistoryDir).Read().Buckets;
        Assert.DoesNotContain(buckets.Keys, key => UsageHistoryStore.MonthOf(key.BucketStart) == "2026-09");
    }

    [Fact]
    public async Task A_changed_copy_of_a_marked_request_never_replaces_history_only_usage()
    {
        // The spec's S2 example: X=100 and Y=10 are stored under A; Y's log is
        // gone; a copy of X with 200 tokens turns up under B.
        WriteLog("c1", "x.jsonl", Line("x", "2026-08-21T12:01:00Z", input: 95));
        WriteLog("c1", "y.jsonl", Line("y", "2026-08-21T12:03:00Z", input: 5));
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        File.Delete(LogPath("c1", "y.jsonl"));
        File.Delete(LogPath("c1", "x.jsonl"));
        WriteLog("c2", "x.jsonl", Line("x", "2026-08-21T12:01:00Z", input: 195));
        await service.SyncHistoryAsync();

        var a = await service.GetReportAsync(UsageDateRange.All, ["claude-1"]);
        Assert.Equal(110, a.Totals.Tokens.ProcessedTokens);
        Assert.Equal(2, a.Totals.RequestCount);
        Assert.True((await service.GetReportAsync(UsageDateRange.All, ["claude-2"])).IsEmpty);
        var stored = Assert.Single(new UsageHistoryStore(HistoryDir).Read().Buckets).Value;
        Assert.Equal((110L, 2L), (stored.Tokens.ProcessedTokens, stored.Requests));
    }

    [Fact]
    public async Task A_late_request_in_a_marked_bucket_never_drops_the_marked_requests()
    {
        // A request from before the conversion that turns up only after it
        // (still being written during the conversion scan, or a restored log)
        // follows today's rules. Today the live bucket holds the marked
        // requests too, so nothing is lost; neither may it be here.
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"), Line("2", "2026-08-21T12:03:00Z"));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        WriteLog("c1", "late.jsonl", Line("3", "2026-08-21T12:05:00Z", input: 1000));
        await service.SyncHistoryAsync();
        Assert.Equal(3, await Requests(service));

        File.Delete(LogPath("c1"));
        File.Delete(LogPath("c1", "late.jsonl"));
        await service.SyncHistoryAsync();
        Assert.Equal(3, await Requests(service));
    }

    [Fact]
    public async Task A_restored_copy_of_a_log_deleted_before_the_conversion_behaves_as_today()
    {
        var a = Claude("claude-1", "c1");
        var b = Claude("claude-2", "c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [a, b];
        EmptyClaudeFolder("c1");
        EmptyClaudeFolder("c2");

        // History saved by an earlier version; the log itself is gone, so the
        // conversion cannot mark the request.
        var instant = At("2026-08-21T12:01:00Z");
        new UsageHistoryStore(HistoryDir).Merge(
            new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [KeyFor(a, instant)] = Bucket(10) },
            [],
            cacheRecovered: true);
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.True(File.Exists(StorePath));
        Assert.Equal((0L, 0L), StoreRows());

        // Restored into the same account: the larger bucket wins, as today.
        WriteLog("c1", "s.jsonl", Line("1", instant));
        await service.SyncHistoryAsync();
        Assert.Equal(1, await Requests(service));

        // Restored into another account: counted again, as today (a
        // documented limit for usage from before the conversion).
        File.Delete(LogPath("c1"));
        WriteLog("c2", "s.jsonl", Line("1", instant));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
    }

    // -- Conversion ----------------------------------------------------------

    [Fact]
    public async Task The_conversion_changes_no_totals()
    {
        var a = Claude("claude-1", "c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [a];
        new UsageHistoryStore(HistoryDir).Merge(
            new Dictionary<UsageHistoryKey, UsageHistoryBucket>
            {
                // History only: its log was deleted long ago.
                [KeyFor(a, At("2026-07-10T09:00:00Z"))] = Bucket(40, 2),
                // Larger than what the logs still hold for this bucket.
                [KeyFor(a, At("2026-08-21T12:00:00Z"))] = Bucket(100, 3)
            },
            [],
            cacheRecovered: true);
        WriteLog("c1", "s.jsonl",
            Line("1", "2026-08-21T12:01:00Z"),
            Line("2", "2026-08-21T12:07:00Z"),
            Line("3", "2026-08-22T09:00:00Z"),
            KeylessLine("k", At("2026-08-22T09:05:00Z")));
        var service = Service(() => accounts);
        var today = await service.GetReportAsync(UsageDateRange.All);

        await service.SyncHistoryAsync();
        Assert.Equal((3L, 0L), StoreRows());
        var converted = await service.GetReportAsync(UsageDateRange.All);
        Assert.Equal(today.Totals, converted.Totals);
        Assert.Equal(today.Daily, converted.Daily);

        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();
        var gone = await service.GetReportAsync(UsageDateRange.All);
        Assert.Equal(today.Totals, gone.Totals);
        Assert.Equal(today.Daily, gone.Daily);
    }

    [Fact]
    public async Task A_busy_month_postpones_the_conversion_without_changes()
    {
        var a = Claude("claude-1", "c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [a];
        new UsageHistoryStore(HistoryDir).Merge(
            new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [KeyFor(a, At("2026-08-21T12:00:00Z"))] = Bucket(10) },
            [],
            cacheRecovered: true);
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"), Line("2", "2026-08-21T12:03:00Z"));
        var month = Path.Combine(HistoryDir, "usage-2026-08.json");
        var before = File.ReadAllBytes(month);
        var service = Service(() => accounts);

        using (new FileStream(month, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await service.SyncHistoryAsync();
        }

        Assert.False(File.Exists(StorePath));
        Assert.Equal(before, File.ReadAllBytes(month));

        await service.SyncHistoryAsync();
        Assert.True(File.Exists(StorePath));
        Assert.Equal((2L, 0L), StoreRows());
        Assert.Equal(2, await Requests(service));
    }

    [Fact]
    public async Task The_conversion_waits_while_a_claude_folder_is_out_of_reach()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "unplugged")];
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"));
        new UsageHistoryStore(HistoryDir).Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket>(), [], cacheRecovered: true);
        var service = Service(() => accounts);

        await service.SyncHistoryAsync();

        Assert.False(File.Exists(StorePath));
        Assert.Single(new UsageHistoryStore(HistoryDir).Read().Buckets);
        Assert.Equal(1, await Requests(service));
    }

    // -- Report --------------------------------------------------------------

    [Fact]
    public async Task A_recorded_request_keeps_its_exact_time_in_a_window()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        // 14 minutes into a bucket that starts after the conversion.
        var bucket = DateTimeOffset.FromUnixTimeSeconds(UsageHistory.BucketStartOf(DateTimeOffset.UtcNow) + UsageHistory.BucketSeconds);
        WriteLog("c1", "s.jsonl", Line("1", bucket.AddMinutes(14)));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();

        var window = await service.GetWindowReportAsync(new UsageTimeWindow(bucket.AddMinutes(5), bucket.AddMinutes(30)));
        Assert.Equal(1, window.Totals.RequestCount);
    }

    [Fact]
    public async Task The_deletion_point_and_retention_hide_records_by_their_timestamp()
    {
        var now = DateTimeOffset.UtcNow;
        SeedStore(now.AddDays(-30));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", now.AddDays(-20)), Line("2", now.AddDays(-5)));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));
        Assert.Equal((0L, 2L), StoreRows());
        Assert.Equal(2, await Requests(service));

        Assert.Equal(1, await Requests(Service(() => accounts, retentionDays: 10)));

        EditState(state => state["deletedBefore"] = now.AddDays(-10).ToUnixTimeSeconds());
        Assert.Equal(1, await Requests(Service(() => accounts)));
    }

    [Fact]
    public async Task A_keyless_request_in_the_same_bucket_as_a_record_is_counted_too()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        var bucket = DateTimeOffset.FromUnixTimeSeconds(UsageHistory.BucketStartOf(DateTimeOffset.UtcNow) + UsageHistory.BucketSeconds);
        WriteLog("c1", "s.jsonl", Line("1", bucket.AddMinutes(1)), KeylessLine("k", bucket.AddMinutes(2), input: 1000));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));

        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
        Assert.Equal(1, Assert.Single(new UsageHistoryStore(HistoryDir).Read().Buckets).Value.Requests);
    }

    [Fact]
    public async Task A_bucket_an_older_version_saved_after_the_conversion_is_added_to_the_records()
    {
        var a = Claude("claude-1", "c1");
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [a];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        var t = AfterConversion();
        WriteLog("c1", "s.jsonl", Line("1", t));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));

        // An older version ignores the store and saves its own bucket for the
        // same bucket, account and model: added, never compared (an older
        // version after the upgrade is not supported and can double count).
        new UsageHistoryStore(HistoryDir).Merge(
            new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [KeyFor(a, t)] = Bucket(10) },
            [],
            cacheRecovered: true);

        Assert.Equal(2, await Requests(service));
    }

    [Fact]
    public async Task Codex_usage_is_saved_as_before_after_the_conversion()
    {
        var codexDir = Path.Combine(_root, "codex1");
        var rollout = Path.Combine(codexDir, "sessions", "2026", "rollout-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        File.WriteAllLines(rollout,
        [
            CodexTurnContext.Replace("@TS@", "2026-08-21T12:00:00.000Z", StringComparison.Ordinal),
            CodexTokenCount.Replace("@TS@", "2026-08-21T12:01:00.000Z", StringComparison.Ordinal)
        ]);
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"));
        IReadOnlyList<MonitoredAccountSettings> accounts =
        [
            Claude("claude-1", "c1"),
            new() { Id = "codex-1", Type = MonitoredAccountSettings.CodexType, DisplayName = "Codex", ConfigDir = codexDir }
        ];
        var service = Service(() => accounts);
        var before = await service.GetReportAsync(UsageDateRange.All);

        await service.SyncHistoryAsync();
        File.Delete(rollout);
        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();

        var after = await service.GetReportAsync(UsageDateRange.All);
        Assert.Equal(2, after.Totals.RequestCount);
        Assert.Equal(before.Totals, after.Totals);
        Assert.Contains(new UsageHistoryStore(HistoryDir).Read().Buckets.Keys, key => key.Provider == UsageProviderKind.Codex);
    }

    [Fact]
    public async Task A_removed_account_with_records_only_keeps_its_name()
    {
        EmptyClaudeFolder("c1");
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1", "Work"), Claude("claude-2", "c2", "Home")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        WriteLog("c1", "s.jsonl", Line("1", AfterConversion()));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));

        accounts = [Claude("claude-2", "c2", "Home")];

        var removed = Assert.Single(await service.GetAccountsAsync(), account => account.AccountId != "claude-2");
        Assert.StartsWith(UsageHistory.ArchivedAccountIdPrefix, removed.AccountId);
        Assert.Equal("Work (removed)", removed.DisplayName);
        Assert.Equal(1, await Requests(service, removed.AccountId));
        var source = await service.GetRemoteUsageSourceAsync();
        Assert.Contains(source.Accounts, account => account is { Nickname: "Work", Kind: RemoteUsageAccountKind.Removed });
    }

    [Fact]
    public async Task A_busy_history_lock_reports_without_the_records()
    {
        var now = DateTimeOffset.UtcNow;
        SeedStore(now.AddDays(-30));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", now.AddDays(-20)));
        await Service(() => accounts).SyncHistoryAsync();
        File.Delete(LogPath("c1"));
        WriteLog("c1", "later.jsonl", Line("2", now.AddDays(-10)));

        var store = new UsageHistoryStore(HistoryDir) { ReportLockTimeout = TimeSpan.FromMilliseconds(200) };
        using (new FileStream(Path.Combine(HistoryDir, "history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            // The saved record is left out; the request still in its log shows.
            Assert.Equal(1, await Requests(Service(() => accounts, store: store)));
        }

        Assert.Equal(2, await Requests(Service(() => accounts, store: store)));
    }

    [Fact]
    public async Task Settings_info_counts_a_history_of_records_only()
    {
        var now = DateTimeOffset.UtcNow;
        SeedStore(now.AddDays(-30));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var first = DateTimeOffset.FromUnixTimeMilliseconds(now.AddDays(-20).ToUnixTimeMilliseconds());
        WriteLog("c1", "s.jsonl", Line("1", first), Line("2", now.AddDays(-5)));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        var info = service.GetHistoryInfo();
        Assert.Equal(0, info.MonthCount);
        Assert.Equal(first, info.FirstBucket);
        Assert.True(info.Bytes >= new FileInfo(StorePath).Length);
    }

    // -- Recovery ------------------------------------------------------------

    [Fact]
    public async Task A_rerun_of_the_cache_recovery_skips_marks_and_records()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        var old = WriteLog("c1", "old.jsonl", Line("x", "2026-07-01T10:00:00Z"));
        await new UsageLogCollector(() => accounts, cache).CollectAsync();
        File.Delete(old);
        WriteLog("c1", "live.jsonl", Line("y", "2026-08-21T12:00:00Z"));

        var service = Service(() => accounts, cache: cache);
        await service.SyncHistoryAsync();
        Assert.Equal((2L, 0L), StoreRows());

        WriteLog("c1", "new.jsonl", Line("z", AfterConversion()));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1", "live.jsonl"));
        File.Delete(LogPath("c1", "new.jsonl"));
        await service.SyncHistoryAsync();
        Assert.Equal(3, await Requests(service));

        // The recovery runs again, as after a lost state: every orphaned
        // cache entry is offered again, including the record's.
        EditState(state => state["cacheRecovered"] = false);
        Assert.True(new UsageHistoryStore(HistoryDir).NeedsCacheRecovery());
        await service.SyncHistoryAsync();

        Assert.False(new UsageHistoryStore(HistoryDir).NeedsCacheRecovery());
        Assert.Equal(3, await Requests(service));
        Assert.Equal((2L, 1L), StoreRows());
    }

    // -- Delete, prune, clock ------------------------------------------------

    [Fact]
    public async Task A_delete_from_another_process_during_a_sync_brings_nothing_back()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"));
        var armed = 0;
        int Retention()
        {
            // Read after the scan and before the write: the moment a second
            // app process deletes the history.
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                Assert.True(new UsageHistoryStore(HistoryDir).DeleteAll(DateTimeOffset.UtcNow));
            }

            return 0;
        }

        var service = Service(() => accounts, retention: Retention);
        await service.SyncHistoryAsync();
        Assert.True(File.Exists(StorePath));

        WriteLog("c1", "more.jsonl", Line("2", "2026-08-22T12:01:00Z"));
        armed = 1;
        await service.SyncHistoryAsync();

        Assert.Empty(new UsageHistoryStore(HistoryDir).Read().Buckets);
        Assert.False(File.Exists(StorePath));
        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
    }

    [Fact]
    public async Task A_repaired_state_does_not_block_syncing()
    {
        var a = Claude("claude-1", "c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [a];
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        File.Delete(Path.Combine(HistoryDir, "state.json"));
        File.Delete(Path.Combine(HistoryDir, "state.json.bak"));

        WriteLog("c1", "more.jsonl", Line("2", "2026-08-22T12:01:00Z"));
        await service.SyncHistoryAsync();
        await service.SyncHistoryAsync();

        Assert.Contains(KeyFor(a, At("2026-08-22T12:01:00Z")), new UsageHistoryStore(HistoryDir).Read().Buckets.Keys);
        File.Delete(LogPath("c1"));
        File.Delete(LogPath("c1", "more.jsonl"));
        Assert.Equal(2, await Requests(service));
    }

    [Fact]
    public async Task A_lost_state_with_records_only_hides_nothing()
    {
        var now = DateTimeOffset.UtcNow;
        SeedStore(now.AddDays(-30));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", now.AddDays(-20)));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));
        File.Delete(Path.Combine(HistoryDir, "state.json"));
        File.Delete(Path.Combine(HistoryDir, "state.json.bak"));

        Assert.Equal(1, await Requests(service));
        await service.SyncHistoryAsync();
        Assert.True(File.Exists(Path.Combine(HistoryDir, "state.json")));
        Assert.Equal(1, await Requests(service));
    }

    [Fact]
    public async Task Deleting_history_removes_every_store_file()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        WriteLog("c1", "s.jsonl", Line("1", AfterConversion()));
        await service.SyncHistoryAsync();
        Assert.True(File.Exists(StorePath + ".bak"));
        File.WriteAllText(StorePath + ".bak.tmp", "left by a stopped backup");
        File.WriteAllText(StorePath + ".corrupt-20260101000000000", "set aside");

        Assert.True(await service.DeleteHistoryAsync());

        Assert.Empty(Directory.GetFiles(HistoryDir, "claude-requests.db*"));
        Assert.Empty(Directory.GetFiles(HistoryDir, "usage-*"));
    }

    [Fact]
    public async Task Prune_removes_expired_marks_and_records()
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", now.AddDays(-400)), Line("2", now.AddDays(-10)));
        await Service(() => accounts).SyncHistoryAsync();
        Assert.Equal((2L, 0L), StoreRows());

        await Service(() => accounts, retentionDays: 365).SyncHistoryAsync();
        Assert.Equal((1L, 0L), StoreRows());

        Directory.Delete(HistoryDir, recursive: true);
        SeedStore(now.AddDays(-500));
        await Service(() => accounts).SyncHistoryAsync();
        Assert.Equal((0L, 2L), StoreRows());

        await Service(() => accounts, retentionDays: 365).SyncHistoryAsync();
        Assert.Equal((0L, 1L), StoreRows());
    }

    // -- Durability ----------------------------------------------------------

    [Fact]
    public async Task A_damaged_store_is_restored_from_its_backup()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        WriteLog("c1", "s.jsonl", Line("1", AfterConversion()));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));
        Assert.True(File.Exists(StorePath + ".bak"));

        File.WriteAllText(StorePath, "this is not a database");

        Assert.Equal(1, await Requests(service));
        Assert.Single(Directory.GetFiles(HistoryDir, "claude-requests.db.corrupt-*"));
        Assert.Equal((0L, 1L), StoreRows());
    }

    [Fact]
    public async Task With_store_and_backup_damaged_nothing_new_is_saved_and_live_usage_still_shows()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        WriteLog("c1", "s.jsonl", Line("1", AfterConversion()));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));

        File.WriteAllText(StorePath, "damaged");
        File.WriteAllText(StorePath + ".bak", "damaged too");
        WriteLog("c1", "new.jsonl", Line("2", AfterConversion()));
        await service.SyncHistoryAsync();

        Assert.Empty(new UsageHistoryStore(HistoryDir).Read().Buckets);
        Assert.Equal("damaged", File.ReadAllText(StorePath));
        Assert.Equal(1, await Requests(service));
    }

    [Fact]
    public async Task A_backup_older_than_a_prune_brings_no_expired_rows_back()
    {
        var now = DateTimeOffset.UtcNow;
        SeedStore(now.AddDays(-500));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", now.AddDays(-400)), Line("2", now.AddDays(-10)));
        await Service(() => accounts).SyncHistoryAsync();
        var stale = File.ReadAllBytes(StorePath + ".bak");
        File.Delete(LogPath("c1"));

        await Service(() => accounts, retentionDays: 365).SyncHistoryAsync();
        Assert.Equal((0L, 1L), StoreRows());

        File.WriteAllBytes(StorePath + ".bak", stale);
        File.WriteAllText(StorePath, "damaged");

        // No retention in this report, so only the committed prune floor can
        // keep the expired record out.
        Assert.Equal(1, await Requests(Service(() => accounts)));
        Assert.Equal((0L, 1L), StoreRows());
    }

    [Fact]
    public async Task A_backup_older_than_a_delete_brings_nothing_back()
    {
        var now = DateTimeOffset.UtcNow;
        SeedStore(now.AddDays(-500));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        // One record dated after the delete point: only the deletion id can
        // tell that the backup predates the delete.
        WriteLog("c1", "s.jsonl", Line("1", now.AddDays(-20)), Line("2", now.AddHours(2)));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        var stale = File.ReadAllBytes(StorePath + ".bak");
        File.Delete(LogPath("c1"));

        Assert.True(await service.DeleteHistoryAsync());
        File.WriteAllBytes(StorePath + ".bak", stale);

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.Equal((0L, 0L), StoreRows());
    }

    [Fact]
    public async Task An_unknown_store_format_is_left_alone()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        SetStoreMeta("schema", "2");

        WriteLog("c1", "s.jsonl", Line("1", AfterConversion()));
        await service.SyncHistoryAsync();

        Assert.Equal(1, await Requests(service));
        Assert.Empty(new UsageHistoryStore(HistoryDir).Read().Buckets);
        Assert.Equal((0L, 0L), StoreRows());

        SetStoreMeta("schema", "1");
        SetStoreMeta("key_scheme", "9");
        await service.SyncHistoryAsync();
        Assert.Equal((0L, 0L), StoreRows());
        Assert.Equal(1, await Requests(service));
    }

    // -- Review round 1 regressions -------------------------------------------

    private sealed class SimulatedStop : Exception
    {
        public SimulatedStop(string step)
            : base("Simulated stop at " + step)
        {
        }
    }

    /// <summary>A store whose test hook throws once at <paramref name="step"/>, as if the app stopped there.</summary>
    private UsageHistoryStore StoppingAt(string step)
    {
        var armed = 1;
        return new UsageHistoryStore(HistoryDir)
        {
            StopAt = name =>
            {
                if (name == step && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    throw new SimulatedStop(name);
                }
            }
        };
    }

    private long StoreFreezeMs()
    {
        using var connection = OpenStore();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE name = 'freeze_ms';";
        return long.Parse((string)command.ExecuteScalar()!, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_request_in_the_conversion_second_read_from_the_parse_cache_is_recorded()
    {
        // The review's example: the conversion at .500 of a second, a request
        // at .900 of the same second. The parse cache keeps whole seconds, so
        // a sync that reads the request from the cache sees .000.
        var second = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
        SeedStore(second.AddMilliseconds(500));
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        string[] lines = [Line("1", second.AddMilliseconds(900))];
        WriteLog("c1", "s.jsonl", lines);
        var service = Service(() => accounts, cache: cache);

        // A report parses the log into the cache before the next sync.
        Assert.Equal(1, await Requests(service));
        await service.SyncHistoryAsync();
        Assert.Equal((0L, 1L), StoreRows());
        Assert.Empty(new UsageHistoryStore(HistoryDir).Read().Buckets);

        WriteLog("c2", "s.jsonl", lines);
        File.Delete(LogPath("c1"));
        await service.SyncHistoryAsync();
        Assert.Equal(1, await Requests(service));
        File.Delete(LogPath("c2"));
        await service.SyncHistoryAsync();
        Assert.Equal(1, await Requests(service));
    }

    [Fact]
    public async Task Requests_around_a_real_conversion_land_on_one_side_through_the_parse_cache()
    {
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        var service = Service(() => accounts, cache: cache);

        // Read by the conversion scan, logged in about the same second as the
        // conversion: a mark, however its timestamp compares later.
        WriteLog("c1", "a.jsonl", Line("1", DateTimeOffset.UtcNow));
        await service.SyncHistoryAsync();
        var freeze = StoreFreezeMs();
        Assert.Equal(0, freeze % 1000);
        Assert.Equal((1L, 0L), StoreRows());

        // Logged in the conversion's own second after the scan read the logs,
        // and parsed into the cache by a report before the next sync.
        WriteLog("c1", "b.jsonl", Line("2", DateTimeOffset.FromUnixTimeMilliseconds(freeze + 999)));
        Assert.Equal(2, await Requests(service));
        await service.SyncHistoryAsync();
        Assert.Equal((1L, 1L), StoreRows());

        WriteLog("c2", "a.jsonl", File.ReadAllLines(LogPath("c1", "a.jsonl")));
        WriteLog("c2", "b.jsonl", File.ReadAllLines(LogPath("c1", "b.jsonl")));
        File.Delete(LogPath("c1", "a.jsonl"));
        File.Delete(LogPath("c1", "b.jsonl"));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
        Assert.Equal((1L, 1L), StoreRows());
    }

    [Fact]
    public async Task A_delete_stopped_right_after_its_journal_is_finished_before_anything_is_read()
    {
        // The delete point falls inside the bucket that holds the usage.
        var deleteAt = QuarterHourAhead();
        var bucket = DateTimeOffset.FromUnixTimeSeconds(UsageHistory.BucketStartOf(deleteAt));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", bucket));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.Single(new UsageHistoryStore(HistoryDir).Read().Buckets);

        Assert.Throws<SimulatedStop>(() => StoppingAt("journal-written").DeleteAll(deleteAt));

        // The journal holds the delete; nothing was removed yet.
        Assert.Single(Directory.GetFiles(HistoryDir, "usage-*.json"));
        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.Empty(Directory.GetFiles(HistoryDir, "usage-*"));
        Assert.NotNull(new UsageHistoryStore(HistoryDir).Read().DeletedBefore);

        await service.SyncHistoryAsync();
        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.Empty(new UsageHistoryStore(HistoryDir).Read().Buckets);
    }

    [Fact]
    public async Task A_prune_stopped_before_its_backup_brings_no_expired_rows_back()
    {
        var now = DateTimeOffset.UtcNow;
        SeedStore(now.AddDays(-500));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", now.AddDays(-400)), Line("2", now.AddDays(-10)));
        await Service(() => accounts).SyncHistoryAsync();
        var backupBefore = File.ReadAllBytes(StorePath + ".bak");
        File.Delete(LogPath("c1"));

        // The rows are gone from the store, but the app stops before the
        // backup is taken: the backup still holds the expired record.
        await Assert.ThrowsAsync<SimulatedStop>(
            () => Service(() => accounts, retentionDays: 365, store: StoppingAt("prune-rows-removed")).SyncHistoryAsync());
        Assert.Equal(backupBefore, File.ReadAllBytes(StorePath + ".bak"));
        Assert.Equal((0L, 1L), StoreRows());

        File.WriteAllText(StorePath, "damaged");

        // No retention in this report: only a floor the restore can read
        // keeps the expired record out.
        Assert.Equal(1, await Requests(Service(() => accounts)));
        Assert.Equal((0L, 1L), StoreRows());
    }

    [Fact]
    public async Task A_mark_stays_while_the_month_of_its_bucket_could_not_be_pruned()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-400);
        EmptyClaudeFolder("c2");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        string[] lines = [Line("1", old), Line("2", now.AddDays(-10))];
        WriteLog("c1", "s.jsonl", lines);
        await Service(() => accounts).SyncHistoryAsync();
        Assert.Equal((2L, 0L), StoreRows());

        var month = Path.Combine(HistoryDir, "usage-" + UsageHistoryStore.MonthOf(old.ToUnixTimeSeconds()) + ".json");
        using (new FileStream(month, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Service(() => accounts, retentionDays: 365).SyncHistoryAsync();
        }

        // The old bucket is still on disk, so its mark is too.
        Assert.True(File.Exists(month));
        Assert.Equal((2L, 0L), StoreRows());

        // Retention is extended and the log moves to the other account.
        WriteLog("c2", "s.jsonl", lines);
        File.Delete(LogPath("c1"));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));
        File.Delete(LogPath("c2"));
        await service.SyncHistoryAsync();
        Assert.Equal(2, await Requests(service));

        // Once the month can be pruned, its mark goes with its bucket.
        await Service(() => accounts, retentionDays: 365).SyncHistoryAsync();
        Assert.False(File.Exists(month));
        Assert.Equal((1L, 0L), StoreRows());
    }

    [Fact]
    public async Task A_backup_from_before_a_delete_at_the_same_point_brings_nothing_back()
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        EmptyClaudeFolder("c1");
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.True(await service.DeleteHistoryAsync());

        // A new store after the first delete, with a record dated after it.
        await service.SyncHistoryAsync();
        WriteLog("c1", "s.jsonl", Line("1", now.AddHours(2)));
        await service.SyncHistoryAsync();
        Assert.Equal((0L, 1L), StoreRows());
        var stale = File.ReadAllBytes(StorePath + ".bak");
        File.Delete(LogPath("c1"));

        // The clock went back: the second delete keeps the same deletion
        // point but gets a new deletion id.
        Assert.True(new UsageHistoryStore(HistoryDir).DeleteAll(now.AddDays(-30)));
        File.WriteAllBytes(StorePath + ".bak", stale);

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.Equal((0L, 0L), StoreRows());
    }

    // -- Review round 2: only explicit deletes remove anything -----------------

    private void DamageStateFiles()
    {
        File.WriteAllText(Path.Combine(HistoryDir, "state.json"), "{ damaged");
        File.WriteAllText(Path.Combine(HistoryDir, "state.json.bak"), "{ damaged");
    }

    /// <summary>Holds the store file so SQLite cannot open it, as a scanner or a stuck handle would.</summary>
    private FileStream HoldStore() => new(StorePath, FileMode.Open, FileAccess.Read, FileShare.None);

    [Fact]
    public async Task A_state_rebuilt_while_the_store_was_unreachable_deletes_nothing_valid()
    {
        // The review's route: a delete, new usage saved in the same quarter
        // hour, then both state files damaged while the store cannot be
        // opened, so a sync rebuilds the state.
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.True(new UsageHistoryStore(HistoryDir).DeleteAll(QuarterHourAhead()));
        var deletedBefore = new UsageHistoryStore(HistoryDir).Read().DeletedBefore!.Value;
        await service.SyncHistoryAsync();
        Assert.True(File.Exists(StorePath));

        WriteLog("c1", "s.jsonl", KeylessLine("k", deletedBefore.AddSeconds(2)), Line("r", deletedBefore.AddSeconds(3)));
        var bucket = UsageHistory.BucketStartOf(deletedBefore);
        new UsageHistoryStore(HistoryDir).Merge(
            new Dictionary<UsageHistoryKey, UsageHistoryBucket>
            {
                [new UsageHistoryKey(bucket, UsageProviderKind.Codex, UsageHistory.CodexAccountKey, "gpt-5.6-sol")] = Bucket(100),
                [new UsageHistoryKey(bucket, UsageProviderKind.Antigravity, UsageHistory.AntigravityAccountKey, "gemini-3-pro")] = Bucket(200)
            },
            [],
            cacheRecovered: true);
        await service.SyncHistoryAsync();
        Assert.Equal((0L, 1L), StoreRows());
        Assert.Equal(3, new UsageHistoryStore(HistoryDir).Read().Buckets.Count);
        Assert.Equal(4, await Requests(service));

        DamageStateFiles();
        using (HoldStore())
        {
            await service.SyncHistoryAsync();
        }

        await service.SyncHistoryAsync();

        Assert.True(File.Exists(StorePath));
        Assert.True(File.Exists(StorePath + ".bak"));
        Assert.Equal((0L, 1L), StoreRows());
        var buckets = new UsageHistoryStore(HistoryDir).Read().Buckets.Keys.ToList();
        Assert.Contains(buckets, key => key.Provider == UsageProviderKind.Codex);
        Assert.Contains(buckets, key => key.Provider == UsageProviderKind.Antigravity);
        Assert.Contains(buckets, key => key.Provider == UsageProviderKind.Claude);
        File.Delete(LogPath("c1"));
        Assert.Equal(4, await Requests(service));
    }

    [Fact]
    public async Task Backed_up_records_survive_a_restore_after_a_state_rebuilt_without_the_store()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        WriteLog("c1", "a.jsonl", Line("1", AfterConversion()));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1", "a.jsonl"));
        Assert.True(File.Exists(StorePath + ".bak"));

        // The state is rebuilt with a new id while the store cannot be read.
        DamageStateFiles();
        using (HoldStore())
        {
            await service.SyncHistoryAsync();
        }

        // Restored while the state still holds the rebuilt id: no delete
        // happened, so the backed-up record stays.
        File.WriteAllText(StorePath, "damaged");
        Assert.Equal(1, await Requests(service));
        Assert.Equal((0L, 1L), StoreRows());

        // More records saved and backed up after it, then another restore.
        WriteLog("c1", "b.jsonl", Line("2", AfterConversion()));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1", "b.jsonl"));
        await service.SyncHistoryAsync();
        File.WriteAllText(StorePath, "damaged again");
        Assert.Equal(2, await Requests(service));
        Assert.Equal((0L, 2L), StoreRows());
    }

    [Fact]
    public async Task A_record_saved_again_after_retention_was_extended_survives_a_restore()
    {
        var now = DateTimeOffset.UtcNow;
        SeedStore(now.AddDays(-500));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", now.AddDays(-400)));
        WriteLog("c1", "recent.jsonl", Line("2", now.AddDays(-10)));
        await Service(() => accounts).SyncHistoryAsync();
        Assert.Equal((0L, 2L), StoreRows());

        // A prune at a one-year floor removes the record; its log stays.
        await Service(() => accounts, retentionDays: 365).SyncHistoryAsync();
        Assert.Equal((0L, 1L), StoreRows());

        // Retention is extended: the request comes back from its log, is
        // saved again and backed up. Then the log goes and the store is damaged.
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.Equal((0L, 2L), StoreRows());
        File.Delete(LogPath("c1"));
        File.Delete(LogPath("c1", "recent.jsonl"));
        File.WriteAllText(StorePath, "damaged");

        Assert.Equal(2, await Requests(service));
        Assert.Equal((0L, 2L), StoreRows());
    }

    // -- Review round 3 ---------------------------------------------------------

    [Fact]
    public async Task A_restore_never_reuses_a_write_sequence_a_prune_has_seen()
    {
        // The review's route: a backup at sequence 2; a write at 3 whose
        // backup fails; a prune recorded at 3; a restore back to 2; the old
        // request saved again after the retention was extended; a second
        // restore must keep it.
        var now = DateTimeOffset.UtcNow;
        SeedStore(now.AddDays(-500));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "recent.jsonl", Line("1", now.AddDays(-10)));
        await Service(() => accounts).SyncHistoryAsync();
        Assert.True(File.Exists(StorePath + ".bak"));

        // A folder in the way of the backup's temporary file makes every
        // backup fail from here on.
        Directory.CreateDirectory(StorePath + ".bak.tmp");
        WriteLog("c1", "old.jsonl", Line("2", now.AddDays(-400)));
        await Service(() => accounts).SyncHistoryAsync();
        Assert.Equal((0L, 2L), StoreRows());
        await Service(() => accounts, retentionDays: 365).SyncHistoryAsync();
        Assert.Equal((0L, 1L), StoreRows());

        Directory.Delete(StorePath + ".bak.tmp");
        File.WriteAllText(StorePath, "damaged");
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.Equal((0L, 2L), StoreRows());

        File.Delete(LogPath("c1", "recent.jsonl"));
        File.Delete(LogPath("c1", "old.jsonl"));
        File.WriteAllText(StorePath, "damaged again");
        Assert.Equal(2, await Requests(service));
        Assert.Equal((0L, 2L), StoreRows());
    }

    [Fact]
    public async Task A_delete_made_while_the_store_was_locked_reaches_the_store_when_it_opens()
    {
        // The review's route: an earlier delete; both state files are damaged
        // while the store is locked and the state is rebuilt; the user deletes
        // while it is still locked, so the store file stays. A row dated after
        // the delete point must not show again.
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.True(await service.DeleteHistoryAsync());
        await service.SyncHistoryAsync();
        WriteLog("c1", "s.jsonl", Line("1", DateTimeOffset.UtcNow.AddHours(2)));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));
        Assert.Equal((0L, 1L), StoreRows());

        DamageStateFiles();
        using (HoldStore())
        {
            await service.SyncHistoryAsync();
            Assert.False(await service.DeleteHistoryAsync());

            // The delete waits in its journal; no saved usage shows meanwhile.
            Assert.True(File.Exists(JournalPath));
            Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        }

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.False(File.Exists(StorePath));
        Assert.False(File.Exists(JournalPath));
        await service.SyncHistoryAsync();
        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
    }

    // -- Deletion journal -------------------------------------------------------

    private string StatePath => Path.Combine(HistoryDir, "state.json");

    private string JournalPath => Path.Combine(HistoryDir, "deletion-journal.json");

    private bool StateCopiesEqual() =>
        File.ReadAllBytes(StatePath).AsSpan().SequenceEqual(File.ReadAllBytes(StatePath + ".bak"));

    private UsageHistoryKey CodexKey(DateTimeOffset instant) =>
        new(UsageHistory.BucketStartOf(instant), UsageProviderKind.Codex, UsageHistory.CodexAccountKey, "gpt-5.6-sol");

    [Fact]
    public async Task A_delete_finished_again_from_a_stale_journal_copy_keeps_what_was_saved_after_it()
    {
        // A stop between removing the journal's two copies leaves the backup
        // copy behind. Finishing the delete again must keep everything saved
        // after it: the new store and the new months carry the delete's id.
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.Throws<SimulatedStop>(() => StoppingAt("journal-main-removed").DeleteAll(DateTimeOffset.UtcNow));
        Assert.False(File.Exists(JournalPath));
        var staleJournal = File.ReadAllBytes(JournalPath + ".bak");

        await service.SyncHistoryAsync();
        Assert.False(File.Exists(JournalPath + ".bak"));
        await service.SyncHistoryAsync();
        WriteLog("c1", "s.jsonl", Line("1", DateTimeOffset.UtcNow.AddHours(2)));
        var store = new UsageHistoryStore(HistoryDir);
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [CodexKey(DateTimeOffset.UtcNow)] = Bucket(100) }, [], cacheRecovered: true);
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));
        Assert.Equal((0L, 1L), StoreRows());
        Assert.True(File.Exists(StorePath + ".bak"));

        File.WriteAllBytes(JournalPath + ".bak", staleJournal);
        File.WriteAllText(StatePath, "{ damaged");

        Assert.Equal(2, await Requests(service));
        Assert.Equal((0L, 1L), StoreRows());
        Assert.Single(store.Read().Buckets);
        Assert.False(File.Exists(JournalPath + ".bak"));
    }

    [Fact]
    public async Task A_stale_state_backup_after_a_delete_never_removes_records_saved_after_it()
    {
        // A delete stops after the main state copy took it and before the
        // backup copy did. A sync then saves and backs up a new record; its
        // log goes, the main state file is damaged, and only the backup copy
        // is left.
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        Assert.Throws<SimulatedStop>(() => StoppingAt("state-main-written").DeleteAll(DateTimeOffset.UtcNow));
        Assert.True(File.Exists(JournalPath));
        Assert.False(StateCopiesEqual());

        await service.SyncHistoryAsync();
        Assert.False(File.Exists(JournalPath));
        WriteLog("c1", "s.jsonl", Line("1", DateTimeOffset.UtcNow.AddHours(2)));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1"));
        Assert.Equal((0L, 1L), StoreRows());
        Assert.True(File.Exists(StorePath + ".bak"));

        File.WriteAllText(StatePath, "{ damaged");

        Assert.Equal(1, await Requests(service));
        Assert.True(File.Exists(StorePath));
        Assert.Equal((0L, 1L), StoreRows());
    }

    [Fact]
    public async Task After_any_locked_open_with_a_valid_main_state_both_state_copies_are_identical()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        var store = new UsageHistoryStore(HistoryDir);

        (string Name, Func<Task> Run)[] opens =
        [
            ("sync", () => service.SyncHistoryAsync()),
            ("report", () => service.GetReportAsync(UsageDateRange.All)),
            ("merge", () => Task.FromResult(store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket>(), [], cacheRecovered: false))),
            ("prune", () => Task.FromResult(store.Prune(DateTimeOffset.UtcNow.AddDays(-1000)))),
            ("postponed recovery", () => Task.FromResult(store.RecordPostponedCacheRecovery())),
            ("delete", () => Task.FromResult(store.DeleteAll(DateTimeOffset.UtcNow)))
        ];

        foreach (var (name, run) in opens)
        {
            // A valid backup copy that differs from the main copy.
            var stale = JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject();
            stale["recoveryAttempts"] = 99;
            File.WriteAllText(StatePath + ".bak", stale.ToJsonString());
            Assert.False(StateCopiesEqual(), name);

            await run();

            Assert.True(StateCopiesEqual(), name);
        }
    }

    [Fact]
    public async Task A_delete_stopped_before_the_state_with_only_an_old_state_backup_left_shows_no_month_usage()
    {
        // Month files hold usage whose logs are gone, dated after the delete
        // point because the clock was set back; the delete stops after the
        // store is removed and before the months are; then the main state is
        // unreadable and its backup predates the delete.
        var a = Claude("claude-1", "c1");
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [a];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        var later = DateTimeOffset.UtcNow.AddHours(2);
        new UsageHistoryStore(HistoryDir).Merge(
            new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [KeyFor(a, later)] = Bucket(10), [CodexKey(later)] = Bucket(100) },
            [],
            cacheRecovered: true);
        Assert.Equal(2, await Requests(service));
        Assert.True(StateCopiesEqual());

        // The clock is two hours behind that usage when the user deletes.
        Assert.Throws<SimulatedStop>(() => StoppingAt("store-removed").DeleteAll(DateTimeOffset.UtcNow));
        File.WriteAllText(StatePath, "{ damaged");

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.Empty(Directory.GetFiles(HistoryDir, "usage-*.json*"));
        Assert.Empty(new UsageHistoryStore(HistoryDir).Read().Buckets);
        await service.SyncHistoryAsync();
        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
    }

    [Fact]
    public async Task While_a_delete_waits_nothing_is_saved_and_the_logs_catch_up_afterwards()
    {
        // The C12 route: a delete stops right after its journal; the main
        // state is damaged and only its old backup is left; the store cannot
        // be opened for a while, and new Codex and key-0 Claude usage is
        // logged meanwhile. Nothing may be saved under the old deletion id;
        // once the store is back the delete is finished and the next sync
        // saves the new usage from the logs.
        var codexDir = Path.Combine(_root, "codex1");
        Directory.CreateDirectory(Path.Combine(codexDir, "sessions"));
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts =
        [
            Claude("claude-1", "c1"),
            new() { Id = "codex-1", Type = MonitoredAccountSettings.CodexType, DisplayName = "Codex", ConfigDir = codexDir }
        ];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        // Usage that is about to be deleted, saved and its logs gone.
        string Rollout(string name) => Path.Combine(codexDir, "sessions", name);
        void WriteRollout(string name, DateTimeOffset at) => File.WriteAllLines(Rollout(name),
        [
            CodexTurnContext.Replace("@TS@", Ts(at), StringComparison.Ordinal),
            CodexTokenCount.Replace("@TS@", Ts(at.AddSeconds(1)), StringComparison.Ordinal)
        ]);
        WriteRollout("rollout-old.jsonl", At("2026-08-21T12:00:00Z"));
        WriteLog("c1", "old.jsonl", Line("1", DateTimeOffset.UtcNow.AddHours(2)), KeylessLine("k", At("2026-08-21T12:00:00Z")));
        await service.SyncHistoryAsync();
        File.Delete(Rollout("rollout-old.jsonl"));
        File.Delete(LogPath("c1", "old.jsonl"));
        Assert.Equal(3, await Requests(service));

        Assert.Throws<SimulatedStop>(() => StoppingAt("journal-written").DeleteAll(DateTimeOffset.UtcNow));
        File.WriteAllText(StatePath, "{ damaged");
        var monthsBefore = Directory.GetFiles(HistoryDir, "usage-*").Order(StringComparer.Ordinal)
            .Select(path => (path, File.ReadAllBytes(path))).ToList();

        var after = DateTimeOffset.UtcNow.AddSeconds(5);
        WriteRollout("rollout-new.jsonl", after);
        WriteLog("c1", "new.jsonl", KeylessLine("n", after.AddSeconds(2)));
        using (HoldStore())
        {
            await service.SyncHistoryAsync();
            var monthsNow = Directory.GetFiles(HistoryDir, "usage-*").Order(StringComparer.Ordinal)
                .Select(path => (path, File.ReadAllBytes(path))).ToList();
            Assert.Equal(monthsBefore.Select(file => file.path), monthsNow.Select(file => file.path));
            Assert.True(monthsBefore.Zip(monthsNow).All(pair => pair.First.Item2.AsSpan().SequenceEqual(pair.Second.Item2)));
            Assert.True(File.Exists(JournalPath));

            // The logs alone, after the delete point.
            Assert.Equal(2, await Requests(service));
        }

        await service.SyncHistoryAsync();
        Assert.False(File.Exists(JournalPath));
        File.Delete(Rollout("rollout-new.jsonl"));
        File.Delete(LogPath("c1", "new.jsonl"));
        Assert.Equal(2, await Requests(service));
        var buckets = new UsageHistoryStore(HistoryDir).Read().Buckets.Keys.ToList();
        Assert.Contains(buckets, key => key.Provider == UsageProviderKind.Codex);
        Assert.Contains(buckets, key => key.Provider == UsageProviderKind.Claude);
    }

    public static TheoryData<string, bool, bool> DeleteSteps()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var step in new[] { "journal-main-written", "journal-written", "store-removed", "months-removed", "state-main-written", "state-written", "journal-main-removed" })
        {
            foreach (var damageMainState in new[] { false, true })
            {
                data.Add(step, damageMainState, false);

                // Only the first steps run while the store file still exists.
                if (step is "journal-main-written" or "journal-written")
                {
                    data.Add(step, damageMainState, true);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DeleteSteps))]
    public async Task A_delete_stopped_at_any_step_hides_deleted_usage_and_keeps_later_usage(string step, bool damageMainState, bool holdStore)
    {
        var a = Claude("claude-1", "c1");
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [a];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();

        // Usage to delete: a record and a key-0 bucket dated after the delete
        // point (the clock set back), and a Codex bucket.
        var later = DateTimeOffset.UtcNow.AddHours(2);
        WriteLog("c1", "old.jsonl", Line("1", later), KeylessLine("k", later));
        await service.SyncHistoryAsync();
        var store = new UsageHistoryStore(HistoryDir);
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [CodexKey(DateTimeOffset.UtcNow.AddDays(-1))] = Bucket(100) }, [], cacheRecovered: true);
        File.Delete(LogPath("c1", "old.jsonl"));
        Assert.Equal(3, await Requests(service));

        Assert.Throws<SimulatedStop>(() => StoppingAt(step).DeleteAll(DateTimeOffset.UtcNow));
        if (damageMainState && File.Exists(StatePath))
        {
            File.WriteAllText(StatePath, "{ damaged");
        }

        using (holdStore ? HoldStore() : null)
        {
            Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty, step);
            await service.SyncHistoryAsync();
            Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty, step);
        }

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty, step);
        Assert.False(File.Exists(JournalPath), step);
        Assert.False(File.Exists(JournalPath + ".bak"), step);

        // Usage saved after the delete survives a damaged main state.
        await service.SyncHistoryAsync();
        WriteLog("c1", "new.jsonl", Line("2", later.AddHours(1)));
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [CodexKey(DateTimeOffset.UtcNow.AddMinutes(1))] = Bucket(100) }, [], cacheRecovered: true);
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1", "new.jsonl"));
        File.WriteAllText(StatePath, "{ damaged");
        Assert.Equal(2, await Requests(service));
        Assert.Equal((0L, 1L), StoreRows());
    }

    [Fact]
    public async Task A_journal_that_cannot_be_read_still_finishes_the_delete()
    {
        // Both journal copies exist but neither can be read: the delete was
        // intended, so it is finished with the newer copy's write time.
        var a = Claude("claude-1", "c1");
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [a];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        new UsageHistoryStore(HistoryDir).Merge(
            new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [KeyFor(a, DateTimeOffset.UtcNow.AddHours(-1))] = Bucket(10), [CodexKey(DateTimeOffset.UtcNow.AddHours(2))] = Bucket(100) },
            [],
            cacheRecovered: true);
        Assert.Equal(2, await Requests(service));

        var written = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        File.WriteAllText(JournalPath, "{ damaged");
        File.WriteAllText(JournalPath + ".bak", "{ damaged too");
        File.SetLastWriteTimeUtc(JournalPath, written.AddMinutes(-5));
        File.SetLastWriteTimeUtc(JournalPath + ".bak", written);

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.False(File.Exists(JournalPath));
        Assert.False(File.Exists(JournalPath + ".bak"));
        Assert.Empty(Directory.GetFiles(HistoryDir, "usage-*"));
        Assert.Equal(new DateTimeOffset(written), new UsageHistoryStore(HistoryDir).Read().DeletedBefore);
    }
    // -- Review round 8 ---------------------------------------------------------

    private int? MonthStampOf(DateTimeOffset instant) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(HistoryDir, "usage-" + UsageHistoryStore.MonthOf(instant.ToUnixTimeSeconds()) + ".json")))!["stamp"]?.GetValue<int>();

    [Fact]
    public async Task Usage_saved_while_the_deletion_id_was_unknown_is_kept_whole_when_it_is_adopted()
    {
        // The review's route: a delete finished and the store was created
        // again with its id; no month files; both state files damaged while
        // the store is locked, so the state is rebuilt as unknown; new Codex
        // and key-0 Claude usage in the delete point's own bucket is saved
        // meanwhile. When the store opens and the real id is adopted, that
        // usage must stay whole.
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.True(new UsageHistoryStore(HistoryDir).DeleteAll(QuarterHourAhead()));
        var deletedBefore = new UsageHistoryStore(HistoryDir).Read().DeletedBefore!.Value;
        await service.SyncHistoryAsync();
        Assert.True(File.Exists(StorePath));
        Assert.Empty(Directory.GetFiles(HistoryDir, "usage-*"));

        var after = deletedBefore.AddSeconds(30);
        var store = new UsageHistoryStore(HistoryDir);
        DamageStateFiles();
        using (HoldStore())
        {
            WriteLog("c1", "a.jsonl", KeylessLine("a", after));
            await service.SyncHistoryAsync();
            store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [CodexKey(after)] = Bucket(100) }, [], cacheRecovered: true);
        }

        Assert.Equal(2, MonthStampOf(after));
        Assert.Equal(2, store.Read().Buckets.Count);

        // The store opens: the real id is adopted and the month restamped.
        await service.SyncHistoryAsync();
        Assert.Equal(1, MonthStampOf(after));

        // A later write into the same month keeps everything.
        WriteLog("c1", "b.jsonl", KeylessLine("b", after.AddSeconds(5)));
        await service.SyncHistoryAsync();
        File.Delete(LogPath("c1", "a.jsonl"));
        File.Delete(LogPath("c1", "b.jsonl"));
        Assert.Equal(3, await Requests(service));
        var buckets = store.Read().Buckets.Keys.ToList();
        Assert.Contains(buckets, key => key.Provider == UsageProviderKind.Codex);
        Assert.Contains(buckets, key => key.Provider == UsageProviderKind.Claude);
    }

    [Fact]
    public async Task A_delete_that_cannot_write_the_state_stays_pending_and_hides_deleted_logs()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.Equal(1, await Requests(service));
        Assert.False(service.GetHistoryInfo().SavingPaused);

        // The state file cannot be written: the delete stays pending, and the
        // log of the deleted usage, still on disk, must not show.
        File.SetAttributes(StatePath, FileAttributes.ReadOnly);
        try
        {
            Assert.False(await service.DeleteHistoryAsync());
            Assert.True(File.Exists(JournalPath));
            Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
            await service.SyncHistoryAsync();
            Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
            Assert.True(service.GetHistoryInfo().SavingPaused);
        }
        finally
        {
            File.SetAttributes(StatePath, FileAttributes.Normal);
        }

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.False(File.Exists(JournalPath));
        Assert.False(service.GetHistoryInfo().SavingPaused);
    }

    [Fact]
    public async Task A_locked_leftover_store_file_keeps_the_delete_pending()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.True(File.Exists(StorePath));
        var leftover = StorePath + ".corrupt-20260101000000000";
        File.WriteAllText(leftover, "set aside");

        using (new FileStream(leftover, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await service.DeleteHistoryAsync());
            Assert.False(File.Exists(StorePath));
            Assert.False(File.Exists(StorePath + ".bak"));

            // A second attempt meets only the leftover; the delete still waits.
            await service.GetReportAsync(UsageDateRange.All);
            Assert.True(File.Exists(JournalPath));
            Assert.True(service.GetHistoryInfo().SavingPaused);
        }

        await service.GetReportAsync(UsageDateRange.All);
        Assert.False(File.Exists(JournalPath));
        Assert.Empty(Directory.GetFiles(HistoryDir, "claude-requests.db*"));
    }

    // -- Review round 9 ---------------------------------------------------------

    [Fact]
    public async Task When_a_pending_delete_cannot_be_checked_no_deleted_usage_shows()
    {
        // A delete is pending, and whether its journal exists cannot be
        // determined (access denied on the folder, say). No log from before
        // the moment that cannot be checked may show, and nothing is saved.
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.Equal(1, await Requests(service));
        File.SetAttributes(StatePath, FileAttributes.ReadOnly);
        try
        {
            Assert.False(await service.DeleteHistoryAsync());
        }
        finally
        {
            File.SetAttributes(StatePath, FileAttributes.Normal);
        }

        var blind = new UsageHistoryStore(HistoryDir)
        {
            JournalProbe = path => path.Contains("deletion-journal", StringComparison.Ordinal) ? null : File.Exists(path)
        };
        var blindService = Service(() => accounts, store: blind);
        Assert.True((await blindService.GetReportAsync(UsageDateRange.All)).IsEmpty);
        await blindService.SyncHistoryAsync();
        Assert.True((await blindService.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.Empty(blind.Read().Buckets);
        Assert.True(blind.PendingDeletionPoint() >= DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.True(blind.Inspect().SavingPaused);
        Assert.True(File.Exists(JournalPath));

        // Once it can be checked again, the delete finishes.
        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task Settings_says_at_once_that_saving_is_paused_after_a_delete_that_could_not_finish()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", "2026-08-21T12:01:00Z"));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        File.SetAttributes(StatePath, FileAttributes.ReadOnly);
        try
        {
            var deleted = await service.DeleteHistoryAsync();

            var text = costats.App.ViewModels.UsageHistorySummaryText.AfterDelete(deleted, service.GetHistoryInfo());
            Assert.False(deleted);
            Assert.Equal(costats.App.ViewModels.UsageHistorySummaryText.Describe(service.GetHistoryInfo()), text);
            Assert.StartsWith("Saving usage history is paused", text, StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(StatePath, FileAttributes.Normal);
        }

        Assert.Equal(
            "Usage history could not be fully deleted right now. Try again in a moment.",
            costats.App.ViewModels.UsageHistorySummaryText.AfterDelete(false, new UsageHistoryInfo(0, 0, null)));
        Assert.Equal(
            "Usage history deleted. Usage from now on will be saved again.",
            costats.App.ViewModels.UsageHistorySummaryText.AfterDelete(true, new UsageHistoryInfo(0, 0, null)));
    }
    // -- Review round 10 --------------------------------------------------------

    [Fact]
    public async Task When_a_pending_delete_cannot_be_checked_after_the_clock_went_back_nothing_shows()
    {
        // The review's route: a delete recorded at 12:30 stays pending; the
        // clock is then set back to 12:00; whether the journal exists cannot
        // be checked. A sample from 12:15, before the real deletion point,
        // must not show in the report or in the remote share.
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", Line("1", now.AddMinutes(15)));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.Equal(1, await Requests(service));
        File.SetAttributes(StatePath, FileAttributes.ReadOnly);
        try
        {
            Assert.False(new UsageHistoryStore(HistoryDir).DeleteAll(now.AddMinutes(30)));
        }
        finally
        {
            File.SetAttributes(StatePath, FileAttributes.Normal);
        }

        var blind = new UsageHistoryStore(HistoryDir)
        {
            JournalProbe = path => path.Contains("deletion-journal", StringComparison.Ordinal) ? null : File.Exists(path)
        };
        var blindService = Service(() => accounts, store: blind);

        Assert.True((await blindService.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.Empty((await blindService.GetRemoteUsageSourceAsync()).Samples);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.MaxValue.ToUnixTimeSeconds()), blind.PendingDeletionPoint());
    }

    [Fact]
    public async Task Settings_shows_saving_paused_when_the_history_folder_cannot_be_listed()
    {
        EmptyClaudeFolder("c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        Assert.False(service.GetHistoryInfo().SavingPaused);

        static void DenyListing(string step)
        {
            if (step == "inspect-enumerate")
            {
                throw new UnauthorizedAccessException("listing denied");
            }
        }

        // Neither the journal nor the folder listing can be checked.
        var blocked = new UsageHistoryStore(HistoryDir)
        {
            JournalProbe = path => path.Contains("deletion-journal", StringComparison.Ordinal) ? null : File.Exists(path),
            StopAt = DenyListing
        };
        var info = Service(() => accounts, store: blocked).GetHistoryInfo();
        Assert.True(info.SavingPaused);
        Assert.Null(info.FirstBucket);
        Assert.Equal(0, info.Bytes);

        // The journal can be checked but the folder cannot be listed.
        var unlisted = new UsageHistoryStore(HistoryDir) { StopAt = DenyListing };
        Assert.True(Service(() => accounts, store: unlisted).GetHistoryInfo().SavingPaused);
    }
}

/// <summary>
/// A fact that xUnit reports as skipped when this machine cannot create a
/// directory junction, decided once when the tests are discovered.
/// </summary>
public sealed class JunctionFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Supported = new(Probe);

    public JunctionFactAttribute()
    {
        if (!Supported.Value)
        {
            Skip = "A directory junction cannot be created on this machine (mklink /J failed).";
        }
    }

    private static bool Probe()
    {
        var root = Path.Combine(Path.GetTempPath(), "costats-junction-probe", Guid.NewGuid().ToString("n"));
        try
        {
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            return ClaudeRequestHistoryTests.TryCreateJunction(Path.Combine(root, "link"), target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover probe folder in the temp directory is harmless.
            }
        }
    }
}
