using System.Security.Cryptography;
using System.Text;
using costats.Core.RemoteView;
using Xunit;

namespace costats.Core.Tests.RemoteView;

public sealed class RemoteViewIdsTests
{
    /// <summary>The authoritative vector from remote/worker/README.md.</summary>
    private const string WriteId = "0123456789abcdef0123456789abcdef";
    private const string ReadId = "3eb1bd439947eb762998e566ccc2e099";

    [Fact]
    public void Derive_read_id_matches_the_worker_test_vector()
    {
        Assert.Equal(ReadId, RemoteViewIds.DeriveReadId(WriteId));
    }

    [Fact]
    public void Derive_read_id_hashes_the_hex_characters_not_the_bytes_they_encode()
    {
        // Hashing the decoded 16 bytes is the obvious way to get this wrong, and
        // it produces a link that resolves to nothing.
        var decoded = Convert.ToHexStringLower(
            SHA256.HashData(Convert.FromHexString(WriteId)))[..32];

        Assert.NotEqual(decoded, RemoteViewIds.DeriveReadId(WriteId));
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(WriteId)))[..32],
            RemoteViewIds.DeriveReadId(WriteId));
    }

    [Fact]
    public void Derive_read_id_is_stable()
    {
        Assert.Equal(RemoteViewIds.DeriveReadId(WriteId), RemoteViewIds.DeriveReadId(WriteId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcde ")]
    public void Invalid_ids_are_rejected(string? id)
    {
        Assert.False(RemoteViewIds.IsValidId(id));
        Assert.Null(RemoteViewIds.TryDeriveReadId(id));
        Assert.Throws<ArgumentException>(() => RemoteViewIds.DeriveReadId(id!));
    }

    [Fact]
    public void A_minted_write_id_is_32_lowercase_hex_characters()
    {
        var writeId = RemoteViewIds.MintWriteId();

        Assert.Equal(32, writeId.Length);
        Assert.True(RemoteViewIds.IsValidId(writeId));
        Assert.Equal(writeId.ToLowerInvariant(), writeId);
    }

    [Fact]
    public void Minted_write_ids_do_not_repeat()
    {
        var minted = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(minted.Add(RemoteViewIds.MintWriteId()));
        }
    }

    [Fact]
    public void A_minted_write_id_derives_a_valid_read_id()
    {
        var readId = RemoteViewIds.DeriveReadId(RemoteViewIds.MintWriteId());

        Assert.True(RemoteViewIds.IsValidId(readId));
    }

    [Fact]
    public void Different_write_ids_derive_different_read_ids()
    {
        var first = RemoteViewIds.MintWriteId();
        var second = RemoteViewIds.MintWriteId();

        Assert.NotEqual(RemoteViewIds.DeriveReadId(first), RemoteViewIds.DeriveReadId(second));
    }
}
