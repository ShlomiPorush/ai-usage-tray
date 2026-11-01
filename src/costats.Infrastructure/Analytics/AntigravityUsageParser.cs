using System.Text;
using costats.Core.Analytics;
using Microsoft.Data.Sqlite;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// Reads per-call token usage out of an Antigravity conversation database
/// (<c>conversations/&lt;id&gt;.db</c>).
/// </summary>
/// <remarks>
/// <para>
/// Each model call is one row of the <c>gen_metadata</c> table, a protobuf
/// record. The schema is private and undocumented; the field paths below were
/// worked out from real records and hold on every record checked:
/// <list type="bullet">
/// <item><c>1.4.2</c> uncached input, <c>1.4.5</c> cache read, <c>1.4.3</c>
/// output, <c>1.4.9</c> thinking (part of output; <c>1.4.9 + 1.4.10 = 1.4.3</c>).</item>
/// <item><c>1.9.4.1</c> the call's Unix time in seconds.</item>
/// <item><c>1.21</c> the model id, <c>1.20</c> key and value pairs that include
/// <c>request_id</c>.</item>
/// </list>
/// A record that does not have this shape is skipped and counted, never guessed at.
/// </para>
/// <para>
/// Privacy: the same records also carry prompt text, tool output and
/// environment details. Only the fields above are decoded; everything else is
/// skipped over without being turned into a string, and nothing from a record
/// is logged or cached except counts, the model id, the time and a hash of the
/// request id.
/// </para>
/// <para>
/// The database is opened read-only with pooling off and only the
/// <c>gen_metadata</c> table is queried. SQLite may still create or map the
/// <c>-shm</c> sidecar, as any reader of a database in WAL mode does; when it
/// cannot, the open fails and the file counts as failed, never as empty.
/// </para>
/// </remarks>
public static class AntigravityUsageParser
{
    /// <summary>
    /// Bump when the parse output shape or semantics change, so stale cache
    /// entries written by an older build are ignored.
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>Records larger than this are skipped without being decoded.</summary>
    public const int MaxRecordBytes = 8 * 1024 * 1024;

    /// <summary>A file with more records than this is treated as unreadable rather than cut short.</summary>
    public const int MaxRecords = 200_000;

    /// <summary>Longest model id or request id accepted.</summary>
    public const int MaxStringLength = 256;

    /// <summary>Largest token count accepted for one field of one call.</summary>
    public const long MaxTokensPerField = 100_000_000;

    private const string Table = "gen_metadata";
    private const string RequestIdKey = "request_id";

    // Earliest and latest call times accepted: 2020-01-01 and 2100-01-01.
    private const long MinUnixSeconds = 1_577_836_800;
    private const long MaxUnixSeconds = 4_102_444_800;

    /// <summary>
    /// Parses one conversation database.
    /// </summary>
    /// <exception cref="SqliteException">The database cannot be opened or read.</exception>
    /// <exception cref="InvalidDataException">The file has more than <see cref="MaxRecords"/> records.</exception>
    public static ParsedUsageFile ParseFile(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "select count(*) from sqlite_master where type = 'table' and name = $name";
            exists.Parameters.AddWithValue("$name", Table);
            if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                // A conversation that has not made a model call yet.
                return ParsedUsageFile.Empty;
            }
        }

        // One read transaction, so the count and the rows see the same snapshot.
        using var transaction = connection.BeginTransaction(deferred: true);

        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "select count(*) from " + Table;
            var records = Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (records > MaxRecords)
            {
                throw new InvalidDataException("The conversation has more records than the usage scan reads.");
            }
        }

        var entries = new List<RawUsageEntry>();
        var skipped = 0;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select length(data), data from " + Table + " order by idx";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                skipped++;
                continue;
            }

            var length = reader.GetInt64(0);
            if (length <= 0 || length > MaxRecordBytes)
            {
                skipped++;
                continue;
            }

            var data = reader.GetFieldValue<byte[]>(1);
            if (TryParseRecord(data, out var entry))
            {
                entries.Add(entry);
            }
            else
            {
                skipped++;
            }
        }

        return new ParsedUsageFile(entries, skipped);
    }

    /// <summary>
    /// Decodes one <c>gen_metadata</c> record. False when it lacks a model, a
    /// time, a request id or any usage, or when any of them is out of range.
    /// </summary>
    internal static bool TryParseRecord(ReadOnlySpan<byte> record, out RawUsageEntry entry)
    {
        entry = default;

        // The generation message is field 1 of the record.
        if (!TryFindLastMessage(record, 1, out var generation))
        {
            return false;
        }

        string? model = null;
        string? requestId = null;
        long? seconds = null;
        UsageFields usage = default;
        var hasUsage = false;

        var position = 0;
        while (position < generation.Length)
        {
            if (!ProtoWire.TryReadField(generation, ref position, out var field))
            {
                return false;
            }

            switch (field.Number)
            {
                case 4 when field.WireType == ProtoWire.LengthDelimited:
                    if (!TryReadUsage(field.Bytes, out usage))
                    {
                        return false;
                    }

                    hasUsage = true;
                    break;

                case 9 when field.WireType == ProtoWire.LengthDelimited:
                    if (TryFindLastMessage(field.Bytes, 4, out var time) &&
                        TryFindLastVarint(time, 1, out var value))
                    {
                        seconds = value > long.MaxValue ? null : (long)value;
                    }

                    break;

                case 20 when field.WireType == ProtoWire.LengthDelimited:
                    if (TryReadPair(field.Bytes, out var key, out var pairValue) &&
                        key == RequestIdKey)
                    {
                        requestId = pairValue;
                    }

                    break;

                case 21 when field.WireType == ProtoWire.LengthDelimited:
                    model = ReadBoundedString(field.Bytes);
                    break;
            }
        }

        if (!hasUsage ||
            string.IsNullOrWhiteSpace(model) ||
            string.IsNullOrEmpty(requestId) ||
            seconds is not { } unixSeconds ||
            unixSeconds < MinUnixSeconds || unixSeconds > MaxUnixSeconds)
        {
            return false;
        }

        entry = new RawUsageEntry(
            UsageLogParser.StableKey("antigravity", requestId),
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds),
            model,
            new UsageTokens
            {
                UncachedInputTokens = usage.Uncached,
                CacheReadInputTokens = usage.CacheRead,
                OutputTokens = usage.Output,
                ReasoningOutputTokens = Math.Min(usage.Thinking, usage.Output)
            });
        return true;
    }

    private static bool TryReadUsage(ReadOnlySpan<byte> message, out UsageFields usage)
    {
        usage = default;
        var position = 0;
        while (position < message.Length)
        {
            if (!ProtoWire.TryReadField(message, ref position, out var field))
            {
                return false;
            }

            // Only the token fields are range checked: other numbers in the
            // usage message (a duration, say) may legitimately be large.
            if (field.WireType != ProtoWire.Varint || field.Number is not (2 or 3 or 5 or 9))
            {
                continue;
            }

            if (field.Value > MaxTokensPerField)
            {
                return false;
            }

            var value = (long)field.Value;
            usage = field.Number switch
            {
                2 => usage with { Uncached = value },
                3 => usage with { Output = value },
                5 => usage with { CacheRead = value },
                _ => usage with { Thinking = value }
            };
        }

        return true;
    }

    private static bool TryReadPair(ReadOnlySpan<byte> message, out string? key, out string? value)
    {
        key = null;
        value = null;

        // Fields may come in any order, so the value is only located here and
        // decoded once the key says it is the request id: other values can
        // hold text that must never be turned into a string.
        ReadOnlySpan<byte> valueBytes = default;
        var hasValue = false;
        var position = 0;
        while (position < message.Length)
        {
            if (!ProtoWire.TryReadField(message, ref position, out var field))
            {
                return false;
            }

            if (field.WireType != ProtoWire.LengthDelimited)
            {
                continue;
            }

            if (field.Number == 1)
            {
                key = ReadBoundedString(field.Bytes);
            }
            else if (field.Number == 2)
            {
                valueBytes = field.Bytes;
                hasValue = true;
            }
        }

        if (key == RequestIdKey && hasValue)
        {
            value = ReadBoundedString(valueBytes);
        }

        return key is not null;
    }

    private static bool TryFindLastMessage(ReadOnlySpan<byte> message, int number, out ReadOnlySpan<byte> found)
    {
        found = default;
        var any = false;
        var position = 0;
        while (position < message.Length)
        {
            if (!ProtoWire.TryReadField(message, ref position, out var field))
            {
                return false;
            }

            if (field.Number == number && field.WireType == ProtoWire.LengthDelimited)
            {
                found = field.Bytes;
                any = true;
            }
        }

        return any;
    }

    private static bool TryFindLastVarint(ReadOnlySpan<byte> message, int number, out ulong value)
    {
        value = 0;
        var any = false;
        var position = 0;
        while (position < message.Length)
        {
            if (!ProtoWire.TryReadField(message, ref position, out var field))
            {
                return false;
            }

            if (field.Number == number && field.WireType == ProtoWire.Varint)
            {
                value = field.Value;
                any = true;
            }
        }

        return any;
    }

    /// <summary>A UTF-8 string of at most <see cref="MaxStringLength"/> bytes, or null.</summary>
    private static string? ReadBoundedString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxStringLength)
        {
            return null;
        }

        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return text.Any(char.IsControl) ? null : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private readonly record struct UsageFields(long Uncached, long CacheRead, long Output, long Thinking);

    /// <summary>
    /// The protobuf wire format, read only far enough to step over fields.
    /// Every wire type in current use is skipped correctly; groups and unknown
    /// wire types make the message unreadable.
    /// </summary>
    internal static class ProtoWire
    {
        public const int Varint = 0;
        public const int Fixed64 = 1;
        public const int LengthDelimited = 2;
        public const int Fixed32 = 5;

        public readonly ref struct Field
        {
            public Field(int number, int wireType, ulong value, ReadOnlySpan<byte> bytes)
            {
                Number = number;
                WireType = wireType;
                Value = value;
                Bytes = bytes;
            }

            public int Number { get; }

            public int WireType { get; }

            /// <summary>The value of a varint or fixed field.</summary>
            public ulong Value { get; }

            /// <summary>The payload of a length-delimited field.</summary>
            public ReadOnlySpan<byte> Bytes { get; }
        }

        public static bool TryReadField(ReadOnlySpan<byte> data, scoped ref int position, out Field field)
        {
            field = default;
            if (!TryReadVarint(data, ref position, out var tag))
            {
                return false;
            }

            var number = tag >> 3;
            var wireType = (int)(tag & 7);
            if (number is 0 or > int.MaxValue)
            {
                return false;
            }

            switch (wireType)
            {
                case Varint:
                    if (!TryReadVarint(data, ref position, out var value))
                    {
                        return false;
                    }

                    field = new Field((int)number, wireType, value, default);
                    return true;

                case Fixed64:
                    if (data.Length - position < 8)
                    {
                        return false;
                    }

                    field = new Field((int)number, wireType, BitConverter.ToUInt64(data.Slice(position, 8)), default);
                    position += 8;
                    return true;

                case Fixed32:
                    if (data.Length - position < 4)
                    {
                        return false;
                    }

                    field = new Field((int)number, wireType, BitConverter.ToUInt32(data.Slice(position, 4)), default);
                    position += 4;
                    return true;

                case LengthDelimited:
                    if (!TryReadVarint(data, ref position, out var length) ||
                        length > (ulong)(data.Length - position))
                    {
                        return false;
                    }

                    field = new Field((int)number, wireType, 0, data.Slice(position, (int)length));
                    position += (int)length;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int position, out ulong value)
        {
            value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (position >= data.Length)
                {
                    return false;
                }

                var current = data[position++];

                // The tenth byte holds only the 64th bit: anything more is an
                // overflow, not a number that happens to wrap to a small one.
                if (shift == 63 && current > 1)
                {
                    return false;
                }

                value |= (ulong)(current & 0x7F) << shift;
                if (current < 0x80)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
