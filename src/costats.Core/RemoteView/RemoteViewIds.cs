using System.Security.Cryptography;
using System.Text;

namespace costats.Core.RemoteView;

/// <summary>
/// The two ids of the remote-view protocol (v2). The app holds a secret
/// <em>write id</em> that authorises PUT and DELETE; the share link carries a
/// <em>read id</em> derived from it, which can only read.
/// </summary>
/// <remarks>
/// The derivation is the one documented in <c>remote/server/README.md</c>:
/// SHA-256 over the UTF-8 bytes of the 32 hex <em>characters</em> of the write
/// id (not over the 16 bytes they encode), first 16 bytes of the digest as
/// lowercase hex. The server echoes the id it derived in the
/// <c>X-Read-Id</c> response header, so a mismatch is a client bug.
/// </remarks>
public static class RemoteViewIds
{
    /// <summary>Both ids are 32 lowercase hex characters.</summary>
    public const int IdLength = 32;

    /// <summary>True when <paramref name="id"/> is exactly 32 lowercase hex characters.</summary>
    public static bool IsValidId(string? id)
    {
        if (id is null || id.Length != IdLength)
        {
            return false;
        }

        foreach (var c in id)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A fresh write id: 128 bits from the cryptographic RNG as 32 lowercase
    /// hex characters. (A GUID would only carry 122 bits of entropy.)
    /// </summary>
    public static string MintWriteId()
    {
        Span<byte> bytes = stackalloc byte[IdLength / 2];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// The public read id for <paramref name="writeId"/>. Throws when the write
    /// id is not 32 lowercase hex characters, because hashing anything else
    /// would produce an id the server never stores under.
    /// </summary>
    public static string DeriveReadId(string writeId)
    {
        if (!IsValidId(writeId))
        {
            throw new ArgumentException(
                "A remote-view write id must be 32 lowercase hex characters.", nameof(writeId));
        }

        // The digest is over the ASCII characters of the id, not over the bytes
        // they encode. Getting this wrong yields a link that resolves to nothing.
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(writeId), digest);
        return Convert.ToHexStringLower(digest[..(IdLength / 2)]);
    }

    /// <summary>
    /// <see cref="DeriveReadId"/> for callers that would rather see null than an
    /// exception when the stored id was hand-edited into something invalid.
    /// </summary>
    public static string? TryDeriveReadId(string? writeId) =>
        IsValidId(writeId) ? DeriveReadId(writeId!) : null;
}
