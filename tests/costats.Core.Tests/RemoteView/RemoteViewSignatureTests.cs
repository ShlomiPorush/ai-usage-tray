using System.Text.Json;
using costats.Application.Settings;
using costats.Core.RemoteView;
using Xunit;

namespace costats.Core.Tests.RemoteView;

public sealed class RemoteViewSignatureTests
{
    // The authoritative cross-implementation vector. The same numbers are
    // asserted by remote/server/request-signing.test.mjs and documented in
    // remote/server/README.md, so the desktop client and the relay cannot drift
    // apart silently. Changing anything here is a protocol change.
    private const long Timestamp = 1767225600;
    private const string Method = "PUT";
    private const string Path = "/u/0123456789abcdef0123456789abcdef";
    private const string Body = """{"version":2,"generatedAt":"2026-08-27T12:00:00Z","accounts":[]}""";
    private const string BodySha256 = "f7d294d301e5c845b8ff9f6d4da1888a94e90bde065fbd3d4ab33b6c74eead9d";
    private const string Canonical = "v1\n1767225600\nPUT\n" + Path + "\n" + BodySha256;
    private const string Signature = "550e42d03d30c657c7a483a2a7b7c91e63e2f0aeb49e7a8bf1feb9366b915cb0";

    [Fact]
    public void Default_key_is_the_documented_public_constant()
    {
        Assert.Equal("ai-usage-tray-public-default-key-v1", RemoteViewSignature.DefaultSigningKey);
        Assert.Equal("X-Costats-Timestamp", RemoteViewSignature.TimestampHeader);
        Assert.Equal("X-Costats-Signature", RemoteViewSignature.SignatureHeader);
    }

    [Fact]
    public void Canonical_string_matches_the_shared_vector()
    {
        Assert.Equal(
            Canonical,
            RemoteViewSignature.CanonicalString(Timestamp, Method, Path, Body));
    }

    [Fact]
    public void Signature_matches_the_shared_vector()
    {
        Assert.Equal(
            Signature,
            RemoteViewSignature.Sign(RemoteViewSignature.DefaultSigningKey, Timestamp, Method, Path, Body));
    }

    [Fact]
    public void Method_is_canonicalised_to_uppercase()
    {
        Assert.Equal(
            Signature,
            RemoteViewSignature.Sign(RemoteViewSignature.DefaultSigningKey, Timestamp, "put", Path, Body));
    }

    [Fact]
    public void A_delete_signs_over_the_empty_body_digest()
    {
        Assert.Equal(
            "0a58e18aa3ae706773c9d1745fca602d978371232cb787317a19c2a2acc6660c",
            RemoteViewSignature.Sign(RemoteViewSignature.DefaultSigningKey, Timestamp, "DELETE", Path, string.Empty));
    }

    [Theory]
    [InlineData("another-key", Timestamp, Method, Path, Body)]
    [InlineData(RemoteViewSignature.DefaultSigningKey, Timestamp + 1, Method, Path, Body)]
    [InlineData(RemoteViewSignature.DefaultSigningKey, Timestamp, "DELETE", Path, Body)]
    [InlineData(RemoteViewSignature.DefaultSigningKey, Timestamp, Method, "/u/ffffffffffffffffffffffffffffffff", Body)]
    [InlineData(RemoteViewSignature.DefaultSigningKey, Timestamp, Method, Path, Body + " ")]
    public void Every_signed_field_changes_the_signature(
        string key, long timestamp, string method, string path, string body)
    {
        Assert.NotEqual(Signature, RemoteViewSignature.Sign(key, timestamp, method, path, body));
    }

    [Fact]
    public void Timestamp_is_unix_seconds()
    {
        Assert.Equal(
            Timestamp,
            RemoteViewSignature.Timestamp(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_configured_key_falls_back_to_the_public_default(string? configured)
    {
        Assert.Equal(RemoteViewSignature.DefaultSigningKey, RemoteViewSignature.ResolveKey(configured));
        Assert.Equal(
            RemoteViewSignature.DefaultSigningKey,
            new AppSettings { DefaultRemoteViewSigningKey = configured }.EffectiveRemoteViewSigningKey);
    }

    [Fact]
    public void A_configured_key_wins_and_is_never_written_to_user_settings()
    {
        var settings = new AppSettings { DefaultRemoteViewSigningKey = "  operator-key  " };
        Assert.Equal("operator-key", settings.EffectiveRemoteViewSigningKey);

        // The key is an app default from appsettings.json, so a later release can
        // change it. It must never be captured in the user's settings.json.
        var serialized = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("operator-key", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("SigningKey", serialized, StringComparison.OrdinalIgnoreCase);
    }
}
