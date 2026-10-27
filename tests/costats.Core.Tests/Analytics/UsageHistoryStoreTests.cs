using System.Globalization;
using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Infrastructure.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsageHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "costats-usage-history-tests", Guid.NewGuid().ToString("n"));

    private string HistoryDir => Path.Combine(_root, "history");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static long At(string instant) =>
        DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds();

    private static UsageHistoryKey Key(string instant, string account = "claude:k1", string model = "m") =>
        new(UsageHistory.BucketStartOf(DateTimeOffset.FromUnixTimeSeconds(At(instant))), UsageProviderKind.Claude, account, model);

    private static UsageHistoryBucket Bucket(long output, long requests = 1) =>
        new(new UsageTokens { UncachedInputTokens = 10, OutputTokens = output }, requests);

    private static readonly UsageHistoryAccount Work = new("claude:k1", UsageProviderKind.Claude, "Work");

    // -- Store ---------------------------------------------------------------

    [Fact]
    public void Merged_buckets_round_trip_across_month_files()
    {
        var store = new UsageHistoryStore(HistoryDir);
        var scanned = new Dictionary<UsageHistoryKey, UsageHistoryBucket>
        {
            [Key("2026-08-31T23:50:00Z")] = Bucket(5, 2),
            [Key("2026-09-01T00:05:00Z")] = Bucket(7, 3)
        };

        Assert.Equal(2, store.Merge(scanned, [Work], cacheRecovered: false));

        Assert.True(File.Exists(Path.Combine(HistoryDir, "usage-2026-08.json")));
        Assert.True(File.Exists(Path.Combine(HistoryDir, "usage-2026-09.json")));
        var read = store.Read();
        Assert.Equal(scanned, read.Buckets);
        Assert.Equal("Work", read.Accounts["claude:k1"].Name);
        Assert.Null(read.DeletedBefore);
    }

    [Fact]
    public void A_smaller_scan_never_shrinks_stored_history_and_a_larger_one_replaces_it()
    {
        var store = new UsageHistoryStore(HistoryDir);
        var key = Key("2026-08-21T12:00:00Z");
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(100, 4) }, [Work], false);

        Assert.Equal(0, store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(10, 1) }, [Work], false));
        Assert.Equal(100, store.Read().Buckets[key].Tokens.OutputTokens);

        Assert.Equal(1, store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(150, 5) }, [Work], false));
        Assert.Equal(5, store.Read().Buckets[key].Requests);
    }

    [Fact]
    public void A_damaged_month_falls_back_to_its_backup_and_is_set_aside_on_the_next_write()
    {
        var store = new UsageHistoryStore(HistoryDir);
        var first = Key("2026-08-21T12:00:00Z");
        var second = Key("2026-08-22T12:00:00Z");
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [first] = Bucket(10) }, [Work], false);
        // The second write turns the first version into usage-2026-08.json.bak.
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [second] = Bucket(20) }, [Work], false);
        var main = Path.Combine(HistoryDir, "usage-2026-08.json");
        File.WriteAllText(main, "{ not json");

        var read = store.Read();
        Assert.True(read.Buckets.ContainsKey(first));
        Assert.False(read.Buckets.ContainsKey(second));

        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [second] = Bucket(20) }, [Work], false);

        Assert.Single(Directory.GetFiles(HistoryDir, "usage-2026-08.corrupt-*.json"));
        Assert.Equal(2, store.Read().Buckets.Count);
    }

    [Fact]
    public void Prune_deletes_old_months_and_trims_the_month_the_cutoff_falls_in()
    {
        var store = new UsageHistoryStore(HistoryDir);
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket>
        {
            [Key("2026-07-10T12:00:00Z")] = Bucket(1),
            [Key("2026-08-05T12:00:00Z")] = Bucket(2),
            [Key("2026-08-25T12:00:00Z")] = Bucket(3),
            [Key("2026-09-02T12:00:00Z")] = Bucket(4)
        }, [Work], false);

        var removed = store.Prune(DateTimeOffset.FromUnixTimeSeconds(At("2026-08-20T00:00:00Z")));

        Assert.Equal(2, removed);
        Assert.False(File.Exists(Path.Combine(HistoryDir, "usage-2026-07.json")));
        Assert.Equal(
            [Key("2026-08-25T12:00:00Z"), Key("2026-09-02T12:00:00Z")],
            store.Read().Buckets.Keys.OrderBy(key => key.BucketStart));
    }

    [Fact]
    public void Delete_all_removes_every_month_and_the_deletion_point_never_moves_back()
    {
        var store = new UsageHistoryStore(HistoryDir);
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [Key("2026-08-21T12:00:00Z")] = Bucket(1) }, [Work], false);
        var now = DateTimeOffset.FromUnixTimeSeconds(At("2026-10-27T10:00:00Z"));

        Assert.True(store.DeleteAll(now));
        Assert.True(store.DeleteAll(now.AddDays(-30)));

        var read = store.Read();
        Assert.Empty(read.Buckets);
        Assert.Equal(now.AddSeconds(1), read.DeletedBefore);
        Assert.Empty(Directory.GetFiles(HistoryDir, "usage-*"));
    }

    [Fact]
    public void A_lost_state_file_is_repaired_without_bringing_deleted_usage_back()
    {
        var store = new UsageHistoryStore(HistoryDir);
        Assert.True(store.NeedsCacheRecovery());
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [Key("2026-08-21T12:07:00Z")] = Bucket(1) }, [Work], cacheRecovered: true);
        Assert.False(store.NeedsCacheRecovery());

        File.Delete(Path.Combine(HistoryDir, "state.json"));
        File.Delete(Path.Combine(HistoryDir, "state.json.bak"));

        var read = store.Read();
        // Everything before the oldest surviving bucket is treated as deleted,
        // and the cache recovery is never run again.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(At("2026-08-21T12:00:00Z")), read.DeletedBefore);
        Assert.False(store.NeedsCacheRecovery());
    }

    [Fact]
    public void A_damaged_state_file_after_a_delete_cannot_undo_it()
    {
        var store = new UsageHistoryStore(HistoryDir);
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [Key("2026-08-21T12:00:00Z")] = Bucket(1) }, [Work], false);
        var now = DateTimeOffset.FromUnixTimeSeconds(At("2026-10-27T10:00:00Z"));
        store.DeleteAll(now);

        File.WriteAllText(Path.Combine(HistoryDir, "state.json"), "{ damaged");

        Assert.Equal(now.AddSeconds(1), store.Read().DeletedBefore);
    }

    [Fact]
    public void A_month_file_a_delete_could_not_remove_stays_hidden()
    {
        var store = new UsageHistoryStore(HistoryDir);
        // The delete point falls inside this bucket: 12:00 to 12:15.
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [Key("2026-08-21T12:00:00Z")] = Bucket(1) }, [Work], false);
        var month = Path.Combine(HistoryDir, "usage-2026-08.json");

        bool deleted;
        using (new FileStream(month, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            deleted = store.DeleteAll(DateTimeOffset.FromUnixTimeSeconds(At("2026-08-21T12:05:00Z")));
        }

        // A file time far in the future (a clock set back, or a copied file)
        // must not make the leftover look newer than the delete.
        File.SetLastWriteTimeUtc(month, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(deleted);
        Assert.True(File.Exists(month));
        Assert.Empty(store.Read().Buckets);
    }

    [Fact]
    public void A_repaired_state_never_lets_a_later_delete_miss_a_leftover_month()
    {
        var store = new UsageHistoryStore(HistoryDir);
        var key = Key("2026-08-21T12:00:00Z");
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(1) }, [Work], false);
        store.DeleteAll(DateTimeOffset.FromUnixTimeSeconds(At("2026-08-21T12:05:00Z")));
        // Usage at 12:07 is saved after the first delete, into the 12:00 bucket.
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(7) }, [Work], false);
        File.Delete(Path.Combine(HistoryDir, "state.json"));
        File.Delete(Path.Combine(HistoryDir, "state.json.bak"));

        var month = Path.Combine(HistoryDir, "usage-2026-08.json");
        using (new FileStream(month, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.DeleteAll(DateTimeOffset.FromUnixTimeSeconds(At("2026-08-21T12:10:00Z")));
        }

        Assert.True(File.Exists(month));
        Assert.Empty(store.Read().Buckets);
    }

    [Fact]
    public void Delete_history_also_removes_files_a_stopped_write_left_behind()
    {
        var store = new UsageHistoryStore(HistoryDir);
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [Key("2026-08-21T12:00:00Z")] = Bucket(1) }, [Work], false);
        File.WriteAllText(Path.Combine(HistoryDir, "usage-2026-09.json.tmp"), "{\"rows\":[[1,0,0,1,1,1,1,1,1,1]]}");
        File.WriteAllText(Path.Combine(HistoryDir, "usage-2026-07.corrupt-20260801000000.json"), "{ damaged");

        Assert.True(store.DeleteAll(DateTimeOffset.FromUnixTimeSeconds(At("2026-10-01T00:00:00Z"))));

        Assert.Empty(Directory.GetFiles(HistoryDir, "usage-*"));
    }

    [Fact]
    public void Usage_saved_in_the_same_bucket_right_after_a_delete_is_kept()
    {
        var store = new UsageHistoryStore(HistoryDir);
        var key = Key("2026-08-21T12:00:00Z");
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(1) }, [Work], false);
        store.DeleteAll(DateTimeOffset.FromUnixTimeSeconds(At("2026-08-21T12:05:00Z")));

        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(7) }, [Work], false);

        Assert.Equal(7, Assert.Single(store.Read().Buckets).Value.Tokens.OutputTokens);
    }

    [Fact]
    public void A_prune_stopped_between_its_two_writes_is_finished_by_the_next_prune()
    {
        var store = new UsageHistoryStore(HistoryDir);
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket>
        {
            [Key("2026-08-05T12:00:00Z")] = Bucket(2),
            [Key("2026-08-25T12:00:00Z")] = Bucket(3)
        }, [Work], false);
        var month = Path.Combine(HistoryDir, "usage-2026-08.json");
        var beforePrune = File.ReadAllText(month);
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(At("2026-08-20T00:00:00Z"));
        store.Prune(cutoff);
        // As if the app stopped after the first write: the backup still
        // holds the expired bucket.
        File.WriteAllText(month + ".bak", beforePrune);

        store.Prune(cutoff);
        File.WriteAllText(month, "{ damaged");

        Assert.Equal([Key("2026-08-25T12:00:00Z")], store.Read().Buckets.Keys);
    }

    [Fact]
    public void Prune_leaves_no_expired_usage_in_the_backup_copy()
    {
        var store = new UsageHistoryStore(HistoryDir);
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket>
        {
            [Key("2026-08-05T12:00:00Z")] = Bucket(2),
            [Key("2026-08-25T12:00:00Z")] = Bucket(3)
        }, [Work], false);

        store.Prune(DateTimeOffset.FromUnixTimeSeconds(At("2026-08-20T00:00:00Z")));
        File.WriteAllText(Path.Combine(HistoryDir, "usage-2026-08.json"), "{ damaged");

        Assert.Equal([Key("2026-08-25T12:00:00Z")], store.Read().Buckets.Keys);
    }

    [Fact]
    public void A_month_that_survives_only_as_a_backup_is_still_read()
    {
        var store = new UsageHistoryStore(HistoryDir);
        var first = Key("2026-08-21T12:00:00Z");
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [first] = Bucket(10) }, [Work], false);
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [Key("2026-08-22T12:00:00Z")] = Bucket(20) }, [Work], false);

        File.Delete(Path.Combine(HistoryDir, "usage-2026-08.json"));

        Assert.True(store.Read().Buckets.ContainsKey(first));
    }

    [Fact]
    public void Rows_with_impossible_timestamps_are_skipped_instead_of_failing_the_report()
    {
        Directory.CreateDirectory(HistoryDir);
        var valid = At("2026-08-21T12:00:00Z");
        File.WriteAllText(Path.Combine(HistoryDir, "usage-2026-08.json"),
            "{\"schema\":1,\"month\":\"2026-08\",\"accounts\":[{\"key\":\"claude:k1\",\"provider\":\"Claude\",\"name\":\"Work\"}]," +
            "\"models\":[\"m\"],\"rows\":[[253402300800,0,0,1,1,1,1,1,1,1],[" +
            valid.ToString(CultureInfo.InvariantCulture) + ",0,0,1,1,1,1,1,1,1]]}");

        var read = new UsageHistoryStore(HistoryDir).Read();

        Assert.Equal(valid, Assert.Single(read.Buckets).Key.BucketStart);
    }

    [Fact]
    public void Cache_recovery_is_not_marked_done_while_a_month_could_not_be_written()
    {
        var store = new UsageHistoryStore(HistoryDir);
        var key = Key("2026-08-21T12:00:00Z");
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(1) }, [Work], false);

        using (new FileStream(Path.Combine(HistoryDir, "usage-2026-08.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(5) }, [Work], cacheRecovered: true);
        }

        Assert.True(store.NeedsCacheRecovery());
    }

    [Fact]
    public void Inspect_reports_the_first_saved_bucket()
    {
        var store = new UsageHistoryStore(HistoryDir);
        Assert.Null(store.Inspect().FirstBucket);

        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket>
        {
            [Key("2026-09-02T12:00:00Z")] = Bucket(4),
            [Key("2026-08-05T12:00:00Z")] = Bucket(2)
        }, [Work], false);

        var info = store.Inspect();
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(At("2026-08-05T12:00:00Z")), info.FirstBucket);
        Assert.Equal(2, info.MonthCount);
        Assert.True(info.Bytes > 0);
    }

    // -- Through the analytics service, with real log files -------------------

    private const string ClaudeTemplate = """
        {"type":"assistant","timestamp":"@TS@","requestId":"@RID@","sessionId":"s1","uuid":"u1","message":{"id":"@MID@","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"redacted"}],"stop_reason":"end_turn","usage":{"input_tokens":@INPUT@,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":5,"service_tier":"standard"}}}
        """;

    private static string ClaudeLine(string id, string timestamp, long input = 10) =>
        ClaudeTemplate
            .Replace("@TS@", timestamp, StringComparison.Ordinal)
            .Replace("@RID@", "req_" + id, StringComparison.Ordinal)
            .Replace("@MID@", "msg_" + id, StringComparison.Ordinal)
            .Replace("@INPUT@", input.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private string WriteLog(string configDir, string name, params string[] lines)
    {
        var path = Path.Combine(_root, configDir, "projects", "p", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    private MonitoredAccountSettings Claude(string id, string configDir, string? name = null) => new()
    {
        Id = id,
        Type = MonitoredAccountSettings.ClaudeType,
        DisplayName = name ?? id,
        ConfigDir = Path.Combine(_root, configDir)
    };

    private UsageAnalyticsService Service(
        Func<IReadOnlyList<MonitoredAccountSettings>> accounts,
        UsageFileCache? cache = null,
        int retentionDays = 0) =>
        new(
            new UsageLogCollector(accounts, cache ?? UsageFileCache.Disabled),
            ModelPricingTable.Default,
            freshness: TimeSpan.Zero)
        {
            TimeZone = TimeZoneInfo.Utc,
            History = new UsageHistoryOptions(new UsageHistoryStore(HistoryDir), accounts, () => retentionDays)
        };

    [Fact]
    public async Task Usage_survives_its_log_being_deleted()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var log = WriteLog("c1", "s.jsonl",
            ClaudeLine("1", "2026-08-21T12:01:00Z"),
            ClaudeLine("2", "2026-08-21T12:07:00Z"),
            ClaudeLine("3", "2026-08-22T09:00:00Z"));
        var service = Service(() => accounts);
        var before = await service.GetReportAsync(UsageDateRange.All);

        await service.SyncHistoryAsync();
        File.Delete(log);
        var after = await service.GetReportAsync(UsageDateRange.All);

        Assert.Equal(3, after.Totals.RequestCount);
        Assert.Equal(before.Totals, after.Totals);
        Assert.Equal(before.Daily, after.Daily);
        Assert.Equal(["claude-1"], (await service.GetAccountsAsync()).Select(account => account.AccountId));
    }

    [Fact]
    public async Task Deleting_history_hides_usage_even_while_its_logs_are_still_on_disk()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        WriteLog("c1", "s.jsonl", ClaudeLine("1", "2026-08-21T12:01:00Z"));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        var raised = 0;
        service.HistoryChanged += (_, _) => raised++;

        Assert.True(await service.DeleteHistoryAsync());
        await service.SyncHistoryAsync();

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.Empty(new UsageHistoryStore(HistoryDir).Read().Buckets);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A_reused_account_id_does_not_inherit_the_removed_accounts_history()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1", "Old work")];
        var log = WriteLog("c1", "s.jsonl", ClaudeLine("1", "2026-08-21T12:01:00Z"));
        var service = Service(() => accounts);
        await service.SyncHistoryAsync();
        File.Delete(log);

        // The account is removed and a different folder gets the same id.
        accounts = [Claude("claude-1", "c2", "New work")];
        WriteLog("c2", "s.jsonl", ClaudeLine("9", "2026-09-01T08:00:00Z"));

        var mine = await service.GetReportAsync(UsageDateRange.All, ["claude-1"]);
        Assert.Equal(1, mine.Totals.RequestCount);
        Assert.Equal(new DateOnly(2026, 9, 1), Assert.Single(mine.Daily).Day);

        var removed = Assert.Single(await service.GetAccountsAsync(), account => account.AccountId != "claude-1");
        Assert.StartsWith(UsageHistory.ArchivedAccountIdPrefix, removed.AccountId);
        Assert.Equal("Old work (removed)", removed.DisplayName);
        Assert.Equal(2, (await service.GetReportAsync(UsageDateRange.All)).Totals.RequestCount);
    }

    [Fact]
    public async Task Older_claude_usage_is_recovered_from_the_parse_cache_once_without_double_counting()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        // An old transcript, parsed into the cache by an earlier build, then
        // deleted by Claude Code. One of its requests was also copied into a
        // resumed session that still exists.
        var old = WriteLog("c1", "old.jsonl",
            ClaudeLine("1", "2026-07-01T10:00:00Z"),
            ClaudeLine("2", "2026-07-01T11:00:00Z"));
        await new UsageLogCollector(() => accounts, cache).CollectAsync();
        File.Delete(old);
        WriteLog("c1", "new.jsonl",
            ClaudeLine("2", "2026-07-01T11:00:00Z"),
            ClaudeLine("3", "2026-08-21T12:00:00Z"));

        var service = Service(() => accounts, cache);
        await service.SyncHistoryAsync();
        await service.SyncHistoryAsync();

        var all = await service.GetReportAsync(UsageDateRange.All);
        Assert.Equal(3, all.Totals.RequestCount);
        var recovered = Assert.Single(await service.GetAccountsAsync(), account => account.AccountId != "claude-1");
        Assert.Equal(UsageHistory.UnknownClaudeAccountName, recovered.DisplayName);
        Assert.Equal(1, (await service.GetReportAsync(UsageDateRange.All, [recovered.AccountId])).Totals.RequestCount);
    }

    [Fact]
    public async Task Recovery_also_takes_deleted_logs_newer_than_the_oldest_remaining_log()
    {
        // Two accounts: the first still has an old log, the second's newer log
        // was deleted (Claude Code cleans each folder on its own schedule).
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        WriteLog("c1", "kept.jsonl", ClaudeLine("1", "2026-09-01T10:00:00Z"));
        var deleted = WriteLog("c2", "gone.jsonl", ClaudeLine("2", "2026-09-15T10:00:00Z"));
        await new UsageLogCollector(() => accounts, cache).CollectAsync();
        File.Delete(deleted);

        var service = Service(() => accounts, cache);
        await service.SyncHistoryAsync();

        Assert.Equal(2, (await service.GetReportAsync(UsageDateRange.All)).Totals.RequestCount);
    }

    [Fact]
    public async Task Recovery_waits_while_a_configured_claude_folder_is_missing()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        var log = WriteLog("c1", "s.jsonl", ClaudeLine("1", "2026-08-21T12:00:00Z"));
        await new UsageLogCollector(() => accounts, cache).CollectAsync();
        File.Delete(log);
        IReadOnlyList<MonitoredAccountSettings> withMissing = [.. accounts, Claude("claude-2", "unplugged-drive")];

        var service = Service(() => withMissing, cache);
        await service.SyncHistoryAsync();

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.True(new UsageHistoryStore(HistoryDir).NeedsCacheRecovery());
    }

    [Fact]
    public async Task A_postponed_recovery_never_counts_usage_already_saved_under_its_account()
    {
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "c2")];
        var log = WriteLog("c1", "s.jsonl", ClaudeLine("1", "2026-08-21T12:00:00Z"));
        // c2 does not exist yet, so the first sync postpones the cache
        // recovery and, with it, saving anything at all.
        var service = Service(() => accounts, cache);
        await service.SyncHistoryAsync();
        Assert.True(new UsageHistoryStore(HistoryDir).NeedsCacheRecovery());
        Assert.Empty(new UsageHistoryStore(HistoryDir).Read().Buckets);

        File.Delete(log);
        Directory.CreateDirectory(Path.Combine(_root, "c2"));
        await service.SyncHistoryAsync();

        Assert.False(new UsageHistoryStore(HistoryDir).NeedsCacheRecovery());
        Assert.Equal(1, (await service.GetReportAsync(UsageDateRange.All)).Totals.RequestCount);
    }

    [Fact]
    public async Task A_saved_month_that_is_locked_during_recovery_is_not_recovered_again()
    {
        var account = Claude("claude-1", "c1");
        IReadOnlyList<MonitoredAccountSettings> accounts = [account];
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        var august = WriteLog("c1", "aug.jsonl", ClaudeLine("1", "2026-08-21T12:00:00Z"));
        await new UsageLogCollector(() => accounts, cache).CollectAsync();

        // A history still waiting for its recovery that already saved August
        // under the real account; then the August log is deleted.
        var store = new UsageHistoryStore(HistoryDir);
        store.RecordPostponedCacheRecovery();
        var key = new UsageHistoryKey(
            UsageHistory.BucketStartOf(DateTimeOffset.FromUnixTimeSeconds(At("2026-08-21T12:00:00Z"))),
            UsageProviderKind.Claude,
            UsageLogRootResolver.HistoryKeyFor(account),
            "claude-opus-5");
        store.Merge(new Dictionary<UsageHistoryKey, UsageHistoryBucket> { [key] = Bucket(5) }, [], cacheRecovered: false);
        File.Delete(august);

        var service = Service(() => accounts, cache);
        using (new FileStream(Path.Combine(HistoryDir, "usage-2026-08.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await service.SyncHistoryAsync();
        }

        Assert.False(store.NeedsCacheRecovery());
        Assert.DoesNotContain(store.Read().Buckets.Keys, bucket => bucket.AccountKey == UsageHistory.UnknownClaudeAccountKey);
    }

    [Fact]
    public async Task A_recovery_that_keeps_failing_is_given_up_and_saving_starts()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1"), Claude("claude-2", "missing")];
        WriteLog("c1", "s.jsonl", ClaudeLine("1", "2026-08-21T12:00:00Z"));
        var service = Service(() => accounts);

        for (var attempt = 1; attempt < UsageAnalyticsService.MaxRecoveryAttempts; attempt++)
        {
            await service.SyncHistoryAsync();
        }

        Assert.Empty(new UsageHistoryStore(HistoryDir).Read().Buckets);

        await service.SyncHistoryAsync();

        Assert.False(new UsageHistoryStore(HistoryDir).NeedsCacheRecovery());
        Assert.Single(new UsageHistoryStore(HistoryDir).Read().Buckets);
    }

    [Fact]
    public async Task A_folder_changed_in_settings_during_a_sync_does_not_move_old_usage_to_it()
    {
        var before = Claude("claude-1", "c1");
        var after = Claude("claude-1", "c2");
        WriteLog("c1", "s.jsonl", ClaudeLine("1", "2026-08-21T12:00:00Z"));
        var reads = 0;
        // The first read sees the old folder, every later read the new one,
        // as if the folder was changed in Settings while the sync ran.
        IReadOnlyList<MonitoredAccountSettings> Accounts() => Interlocked.Increment(ref reads) == 1 ? [before] : [after];

        await Service(Accounts).SyncHistoryAsync();

        var saved = Assert.Single(new UsageHistoryStore(HistoryDir).Read().Buckets).Key;
        Assert.Equal(UsageLogRootResolver.HistoryKeyFor(before), saved.AccountKey);
    }

    [Fact]
    public async Task Retention_hides_usage_older_than_the_setting()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var recent = DateTimeOffset.UtcNow.AddDays(-10).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var ancient = DateTimeOffset.UtcNow.AddDays(-500).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        WriteLog("c1", "s.jsonl", ClaudeLine("1", ancient), ClaudeLine("2", recent));

        Assert.Equal(1, (await Service(() => accounts, retentionDays: 365).GetReportAsync(UsageDateRange.All)).Totals.RequestCount);
        Assert.Equal(2, (await Service(() => accounts, retentionDays: 0).GetReportAsync(UsageDateRange.All)).Totals.RequestCount);
    }

    [Fact]
    public async Task Expiry_removes_saved_history_older_than_the_setting()
    {
        IReadOnlyList<MonitoredAccountSettings> accounts = [Claude("claude-1", "c1")];
        var recent = DateTimeOffset.UtcNow.AddDays(-10).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var older = DateTimeOffset.UtcNow.AddDays(-400).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        WriteLog("c1", "s.jsonl", ClaudeLine("1", older), ClaudeLine("2", recent));
        await Service(() => accounts, retentionDays: 0).SyncHistoryAsync();
        Assert.Equal(2, new UsageHistoryStore(HistoryDir).Read().Buckets.Count);

        await Service(() => accounts, retentionDays: 365).SyncHistoryAsync();

        Assert.Single(new UsageHistoryStore(HistoryDir).Read().Buckets);
    }
}
