using Authagonal.Core.Services;

namespace Authagonal.Tests;

/// <summary>
/// Two distinct secret names never become one storage key.
/// </summary>
/// <remarks>
/// <c>ISecretProvider.ProtectAsync</c>'s contract says the name is the storage key and MUST be unique per
/// distinct value, because reusing one silently overwrites the earlier secret and every reference then
/// resolves to whichever was written last. Both bundled providers broke it three ways: disallowed characters
/// all folded to <c>'-'</c>, the input was truncated to the length budget BEFORE sanitising, and the result
/// was <c>Trim('-')</c>'d.
/// <para>
/// Reachable, not theoretical. Names are built from the user id — <c>mfa-totp-{userId}</c>,
/// <c>mfa-recovery-{userId}-{credentialId}</c> — and a federated connection with
/// <c>UseUpstreamSubjectAsUserId</c> takes the user id straight from the upstream <c>sub</c>, which is
/// whatever the external IdP puts there. Two users whose subs differ only in a character that folds would
/// have shared one stored TOTP seed, the later enrolment silently overwriting the earlier. Same class as the
/// 0.20.0 recovery-code defect: one name across ten codes, "nine of every ten were dead, and the tenth
/// worked ten times".
/// </para>
/// </remarks>
public sealed class SecretNameSanitizerTests
{
    private const int KeyVaultMax = 127;
    private const string KeyVaultAllowed = "-";

    /// <summary>Names that differ only in a character the old sanitiser folded to a hyphen.</summary>
    [Theory]
    [InlineData("mfa-totp-a b", "mfa-totp-a-b")]
    [InlineData("mfa-totp-user@acme.com", "mfa-totp-user-acme-com")]
    [InlineData("mfa-totp-a|b", "mfa-totp-a:b")]
    public void FoldedCharactersDoNotCollide(string first, string second)
    {
        Assert.NotEqual(
            SecretNameSanitizer.Sanitize(first, KeyVaultMax, KeyVaultAllowed),
            SecretNameSanitizer.Sanitize(second, KeyVaultMax, KeyVaultAllowed));
    }

    /// <summary>Leading and trailing hyphens were trimmed, so these three were one key.</summary>
    [Fact]
    public void TrimmedHyphensDoNotCollide()
    {
        var names = new[] { "x", "-x", "x-", "--x--" };
        var keys = names.Select(n => SecretNameSanitizer.Sanitize(n, KeyVaultMax, KeyVaultAllowed)).ToList();

        Assert.Equal(names.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Two long names sharing the first 127 characters were the same key, because truncation happened first.
    /// </summary>
    /// <remarks>
    /// This is the path an upstream <c>sub</c> reaches: an IdP that issues long scoped identifiers with a
    /// common tenant prefix produces exactly this shape.
    /// </remarks>
    [Fact]
    public void LongNamesSharingAPrefixDoNotCollide()
    {
        var prefix = new string('a', 200);
        var first = SecretNameSanitizer.Sanitize($"{prefix}-one", KeyVaultMax, KeyVaultAllowed);
        var second = SecretNameSanitizer.Sanitize($"{prefix}-two", KeyVaultMax, KeyVaultAllowed);

        Assert.NotEqual(first, second);
        Assert.True(first.Length <= KeyVaultMax, $"'{first}' exceeds the Key Vault budget");
        Assert.True(second.Length <= KeyVaultMax, $"'{second}' exceeds the Key Vault budget");
    }

    /// <summary>
    /// A name that needs no rewriting is returned unchanged, so secrets already stored keep resolving.
    /// </summary>
    /// <remarks>
    /// The load-bearing half of the fix. Appending a hash unconditionally would have been injective too, and
    /// would have orphaned every secret in every existing deployment — the reference stored on the row names
    /// the old key, and nothing rewrites those.
    /// </remarks>
    [Theory]
    [InlineData("mfa-totp-0f8fad5bd9cb469fa16570867728950e")]
    [InlineData("saml-3b1e4c2a-sp-key")]
    [InlineData("oidc-entra-client-secret")]
    public void AlreadyValidNamesAreUntouched(string name)
        => Assert.Equal(name, SecretNameSanitizer.Sanitize(name, KeyVaultMax, KeyVaultAllowed));

    /// <summary>Output always satisfies the store's rules, whatever went in.</summary>
    [Theory]
    [InlineData("a b c")]
    [InlineData("user@example.com")]
    [InlineData("!!!")]
    [InlineData("-")]
    [InlineData("ünïcødé-sub")]
    public void OutputIsAlwaysAcceptableToKeyVault(string name)
    {
        var key = SecretNameSanitizer.Sanitize(name, KeyVaultMax, KeyVaultAllowed);

        Assert.InRange(key.Length, 1, KeyVaultMax);
        Assert.All(key, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '-', $"'{c}' is not allowed"));
        // Key Vault rejects a name that is nothing but separators.
        Assert.Contains(key, c => char.IsAsciiLetterOrDigit(c));
    }

    /// <summary>The Secrets Manager allow-list is wider, and its own characters must survive.</summary>
    /// <remarks>
    /// Sharing one implementation is only safe if the allow-list is genuinely a parameter — otherwise the AWS
    /// provider would start hashing names the Key Vault rules reject but Secrets Manager accepts, changing
    /// every stored key on that backend.
    /// </remarks>
    [Fact]
    public void SecretsManagerKeepsItsWiderAllowList()
    {
        const string name = "mfa/recovery_v2+user=1.2@acme-corp";

        Assert.Equal(name, SecretNameSanitizer.Sanitize(name, 512, "/_+=.@-"));
        // ...and the same name is rewritten under Key Vault's narrower rules.
        Assert.NotEqual(name, SecretNameSanitizer.Sanitize(name, KeyVaultMax, KeyVaultAllowed));
    }

    /// <summary>The real call-site shapes, swept for collisions in one pass.</summary>
    [Fact]
    public void TheRealNameShapesAreAllDistinct()
    {
        // Upstream subs an external IdP could plausibly issue, including pairs that folded together.
        string[] subs =
        [
            "a b", "a-b", "a_b", "a|b", "user@acme.com", "user-acme-com", "-leading", "trailing-",
            new string('x', 130) + "one", new string('x', 130) + "two",
        ];

        var keys = subs
            .SelectMany(s => new[] { $"mfa-totp-{s}", $"mfa-recovery-{s}-cred1", $"mfa-recovery-{s}-cred2" })
            .Select(n => SecretNameSanitizer.Sanitize(n, KeyVaultMax, KeyVaultAllowed))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }
}
