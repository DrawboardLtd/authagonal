using Authagonal.Server.Services;
using Microsoft.Extensions.Logging;

namespace Authagonal.Tests;

/// <summary>
/// Vault Transit signing was advertised in five places and implemented in none.
/// </summary>
/// <remarks>
/// <c>docs/extensibility.md</c> gave a complete DI snippet under "Authagonal can delegate JWT signing to
/// HashiCorp Vault's Transit secrets engine. Private keys never leave Vault", and the README,
/// <c>docs/index.md</c>, <c>docs/configuration.md</c> and <c>docs/backup-restore.md</c> all repeated the
/// capability — the last of them telling operators their backups contain no private key.
/// <para>
/// Nothing implemented it. So an operator with an HSM compliance requirement followed the documentation, saw
/// ES256 tokens verify against JWKS, and concluded Vault was signing them — while the private key was generated
/// locally on first boot and persisted to the primary data store, in plaintext unless an <c>IFieldCipher</c>
/// happened to be registered. There is no symptom, which is exactly why the belief survives.
/// </para>
/// <para>
/// Wiring it for real needs <c>ISigningKeyStore</c> to represent a key with no local material, a seam in
/// <c>BuildSigningCredentials</c>, JWKS assembly reading the public key back from Vault, and rotation creating
/// Transit key versions. That is a feature, not a fix. The claim is withdrawn and the trap is made loud.
/// </para>
/// </remarks>
public class VaultTransitSigningClaimTests
{
    [Fact]
    public async Task RegisteringTheCryptoProvider_IsReportedAsNotEnablingRemoteSigning()
    {
        var logger = new CapturingLogger<VaultTransitSigningWarning>();
        var check = new VaultTransitSigningWarning(
            new StubServices(new VaultTransitCryptoProvider()), logger);

        await check.StartAsync(CancellationToken.None);

        var message = Assert.Single(logger.Errors);
        Assert.Contains("NOT delegated to Vault Transit", message, StringComparison.Ordinal);
        Assert.Contains("ISigningKeyStore", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatNeverRegisteredIt_IsNotNagged()
    {
        var logger = new CapturingLogger<VaultTransitSigningWarning>();
        var check = new VaultTransitSigningWarning(new StubServices(null), logger);

        await check.StartAsync(CancellationToken.None);

        Assert.Empty(logger.Errors);
    }

    /// <summary>
    /// No document may claim remote signing while it is unimplemented — that combination is the defect.
    /// </summary>
    /// <remarks>
    /// A source check because the claim lived in five files and its cost was entirely in being believed.
    /// </remarks>
    [Fact]
    public void NoDocumentClaimsVaultSigningWithoutSayingItIsNotWired()
    {
        string[] docs =
        [
            "README.md",
            "docs/index.md",
            "docs/configuration.md",
            "docs/extensibility.md",
            "docs/backup-restore.md",
        ];

        foreach (var doc in docs)
        {
            var path = Path.Combine(RepositoryRoot(), doc.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"expected {path}");

            var text = File.ReadAllText(path);
            if (!text.Contains("Vault Transit", StringComparison.OrdinalIgnoreCase)) continue;

            Assert.True(
                text.Contains("not wired", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not delegated", StringComparison.OrdinalIgnoreCase),
                $"{doc} mentions Vault Transit without stating that JWT signing is not delegated to it");
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Authagonal.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class StubServices(object? resolved) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(VaultTransitCryptoProvider) ? resolved : null;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }
}
