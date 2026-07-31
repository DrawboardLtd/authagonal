using Authagonal.Backup;
using Authagonal.Bff;
using Authagonal.Server.Services;

namespace Authagonal.Tests;

/// <summary>
/// Boundary properties for the BFF proxy, the SAML redirect binding, the session cap and the backup
/// chain of custody.
/// </summary>
public class BffAndInfraHardeningTests
{
    // -----------------------------------------------------------------------
    // F26 — the proxy must not be steerable off the configured upstream
    // -----------------------------------------------------------------------

    [Fact]
    public void ProxyTarget_WithSlashPrefixStripped_StaysOnTheConfiguredHost()
    {
        // BffUpstream.Prefix defaults to "/", and PrefixMatches accepts any prefix ending in '/'. So
        // StripPrefix on a default-prefix upstream sliced off the LEADING slash, leaving a relative
        // string that string concatenation fused onto the host label — and the first path segment
        // after {BasePath}/api is fully caller-controlled. "evil.example/x" became the host, and the
        // session's bearer token went with the request.
        var ok = BffProxy.TryComposeTarget("https://api.internal", "evil.example/steal", "", out var target);

        Assert.True(ok);
        Assert.StartsWith("https://api.internal/", target);
        Assert.Contains("evil.example", target); // as a PATH segment
        Assert.Equal("api.internal", new Uri(target).Host);
    }

    [Fact]
    public void ProxyTarget_NormalPathIsUnchanged()
    {
        Assert.True(BffProxy.TryComposeTarget("https://api.internal", "/orders/123", "?q=1", out var target));
        Assert.Equal("https://api.internal/orders/123?q=1", target);
    }

    [Fact]
    public void ProxyTarget_BaseWithTrailingPath_IsPreserved()
    {
        Assert.True(BffProxy.TryComposeTarget("https://api.internal/v2/", "/orders", "", out var target));
        Assert.Equal("https://api.internal/v2/orders", target);
    }

    [Fact]
    public void ProxyTarget_NonAbsoluteBase_IsRefused()
    {
        Assert.False(BffProxy.TryComposeTarget("not-a-url", "/orders", "", out _));
    }

    // -----------------------------------------------------------------------
    // F336 — the SLO redirect binding runs before any signature check
    // -----------------------------------------------------------------------

    [Fact]
    public void SamlInflate_RefusesADecompressionBomb()
    {
        // ~1 MB of zeros deflates to a couple of hundred bytes. The endpoint is anonymous and inflates
        // BEFORE validating anything, so an unbounded ReadToEnd here is a memory-exhaustion primitive
        // that costs the attacker a short query string.
        var bomb = new byte[4 * 1024 * 1024];
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(
                   compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(bomb, 0, bomb.Length);
        }

        var encoded = Convert.ToBase64String(compressed.ToArray());

        Assert.Throws<InvalidOperationException>(
            () => Authagonal.Server.Services.Saml.SamlRedirectBinding.Inflate(encoded));
    }

    [Fact]
    public void SamlInflate_StillHandlesARealMessage()
    {
        const string message = "<samlp:LogoutRequest xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" ID=\"_1\"/>";
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(
                   compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            deflate.Write(bytes, 0, bytes.Length);
        }

        Assert.Equal(message, Authagonal.Server.Services.Saml.SamlRedirectBinding.Inflate(
            Convert.ToBase64String(compressed.ToArray())));
    }

    // -----------------------------------------------------------------------
    // F70 — the absolute session cap must survive sliding renewal
    // -----------------------------------------------------------------------

    [Fact]
    public void SessionStart_IsReadFromAPropertyRenewalDoesNotTouch()
    {
        // The cap used to be measured against Properties.IssuedUtc, which sliding renewal rewrites on
        // every refresh — and the handler requests a refresh after every security-stamp revalidation,
        // so the clock reset every 30 minutes of activity and 7 days was unreachable.
        var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties();
        var started = DateTimeOffset.UtcNow.AddDays(-8);
        properties.SetString(
            CookieSignInHelper.SessionStartedProperty, started.ToUnixTimeSeconds().ToString());

        // Renewal rewrites IssuedUtc; the session-start stamp is unaffected.
        properties.IssuedUtc = DateTimeOffset.UtcNow;

        var read = CookieSignInHelper.SessionStartedAt(properties);
        Assert.NotNull(read);
        Assert.True(DateTimeOffset.UtcNow - read!.Value > TimeSpan.FromDays(7));
    }

    [Fact]
    public void SessionStart_AbsentOnLegacySessions_ReadsAsNull()
    {
        // Sessions established before this stamp existed must not be rejected wholesale.
        Assert.Null(CookieSignInHelper.SessionStartedAt(
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties()));
    }

    // -----------------------------------------------------------------------
    // F143 — the manifest must authenticate, not merely describe
    // -----------------------------------------------------------------------

    [Fact]
    public void BackupManifest_TamperedAfterSigning_FailsVerification()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);

        var manifest = new BackupManifest();
        manifest.FileHashes["Users.jsonl.gz"] = "abc123";
        ManifestAuthentication.Sign(manifest, key);
        Assert.True(ManifestAuthentication.Verify(manifest, key));

        // Whoever can rewrite a backup file can rewrite its recorded hash — which is why verifying
        // files against an unsigned manifest detects corruption but not tampering.
        manifest.FileHashes["Users.jsonl.gz"] = "rewritten-to-match-my-file";
        Assert.False(ManifestAuthentication.Verify(manifest, key));
    }

    [Fact]
    public void BackupManifest_WithNoMac_DoesNotVerify()
    {
        var key = new byte[32];
        Assert.False(ManifestAuthentication.Verify(new BackupManifest(), key));
    }

    [Fact]
    public void BackupManifest_MacDoesNotVerifyUnderADifferentKey()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        var other = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(other);

        var manifest = new BackupManifest();
        ManifestAuthentication.Sign(manifest, key);

        Assert.False(ManifestAuthentication.Verify(manifest, other));
    }
}
