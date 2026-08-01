namespace Authagonal.Server.Services;

/// <summary>
/// Derives the DataProtection key-ring blob URI from the Table Storage service URI, for the
/// managed-identity configuration that supplies only <c>Storage:TableServiceUri</c>.
/// </summary>
/// <remarks>
/// The documented production configuration is managed identity plus <c>Storage:TableServiceUri</c>,
/// and on that path nothing attached a key repository: persistence was wired only for an explicit
/// <c>DataProtection:BlobUri</c> or a (non-Azurite) <c>Storage:ConnectionString</c>. So the
/// recommended configuration ran on the per-machine file store — a key ring that is destroyed on
/// restart and never shared between pods. Every restart signed every user out, and no two replicas
/// accepted each other's cookies, which on more than one pod is a login loop rather than a warning.
/// <para>
/// A storage account exposes its services under sibling endpoints on the same account, so the blob
/// endpoint follows from the table endpoint by name and the identity that was granted Table Data
/// Contributor is the natural holder of Blob Data Contributor as well. Derivation is skipped rather
/// than guessed for anything that is not a recognisable Azure table endpoint (Azurite, a custom or
/// path-style host): a wrong URI would be a startup failure on a path that works today, and
/// <see cref="KeyRingStartupCheck"/> still reports the ephemeral ring loudly.
/// </para>
/// </remarks>
internal static class DataProtectionBlobUri
{
    /// <summary>Container the derived key ring lives in — the same name the connection-string path uses.</summary>
    internal const string ContainerName = "dataprotection";

    /// <summary>Blob the derived key ring lives in — the same name the connection-string path uses.</summary>
    internal const string BlobName = "keys.xml";

    /// <summary>
    /// The blob service endpoint for the account named by <paramref name="tableServiceUri"/>, or null
    /// when the URI is not a recognisable Azure table endpoint.
    /// </summary>
    internal static Uri? BlobServiceUriFor(string? tableServiceUri)
    {
        if (string.IsNullOrWhiteSpace(tableServiceUri)) return null;
        if (!Uri.TryCreate(tableServiceUri, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;

        // "{account}.table.{suffix}" — the account-scoped DNS form every Azure cloud uses. Azurite and
        // path-style emulators are "{host}/{account}", which carry no service name to swap, so they fall
        // out here and keep today's behaviour.
        var host = uri.Host;
        var marker = host.IndexOf(".table.", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0) return null;

        var blobHost = string.Concat(host.AsSpan(0, marker), ".blob.", host.AsSpan(marker + ".table.".Length));
        var builder = new UriBuilder(uri) { Host = blobHost, Path = "/", Query = "", Fragment = "" };
        return builder.Uri;
    }
}
