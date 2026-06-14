using Authagonal.Core.Clustering;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace Authagonal.Storage.Clustering;

/// <summary>
/// Leader-election lease backed by an Azure Blob lease. One blob per resource under
/// <c>leases/{resource}</c>; the holder renews before expiry. Azure guarantees at most one active
/// lease, giving single-leader semantics across ACA replicas / AKS pods without multicast or
/// per-replica addressing. Works on any platform with access to the storage account.
/// </summary>
public sealed class BlobLeaseProvider : ILeaseProvider
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobLeaseProvider> _logger;
    private readonly Dictionary<string, string> _leaseIds = new();
    private readonly object _lock = new();
    private volatile bool _containerReady;

    public BlobLeaseProvider(BlobServiceClient blobServiceClient, string containerName, ILogger<BlobLeaseProvider> logger)
    {
        _container = blobServiceClient.GetBlobContainerClient(containerName);
        _logger = logger;
    }

    public async Task<bool> TryAcquireOrRenewAsync(string resource, string nodeId, TimeSpan ttl, CancellationToken ct = default)
    {
        // Azure blob leases must be 15-60s (or infinite); clamp to that range.
        var duration = TimeSpan.FromSeconds(Math.Clamp(ttl.TotalSeconds, 15, 60));
        var blob = await EnsureLeaseBlobAsync(resource, ct).ConfigureAwait(false);

        string? currentLeaseId;
        lock (_lock) _leaseIds.TryGetValue(resource, out currentLeaseId);

        // Renew if we believe we hold it.
        if (currentLeaseId is not null)
        {
            try
            {
                await blob.GetBlobLeaseClient(currentLeaseId).RenewAsync(cancellationToken: ct).ConfigureAwait(false);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412 or 404)
            {
                // Lost it (expired/taken/recreated) — drop and try to re-acquire below.
                lock (_lock) _leaseIds.Remove(resource);
            }
        }

        // Acquire.
        try
        {
            var resp = await blob.GetBlobLeaseClient().AcquireAsync(duration, cancellationToken: ct).ConfigureAwait(false);
            lock (_lock) _leaseIds[resource] = resp.Value.LeaseId;
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409)
        {
            // Held by another node.
            return false;
        }
    }

    public async Task ReleaseAsync(string resource, string nodeId, CancellationToken ct = default)
    {
        string? leaseId;
        lock (_lock)
        {
            _leaseIds.TryGetValue(resource, out leaseId);
            _leaseIds.Remove(resource);
        }
        if (leaseId is null) return;

        try
        {
            var blob = _container.GetBlobClient(BlobName(resource));
            await blob.GetBlobLeaseClient(leaseId).ReleaseAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException)
        {
            // best-effort
        }
    }

    private async Task<BlobClient> EnsureLeaseBlobAsync(string resource, CancellationToken ct)
    {
        if (!_containerReady)
        {
            try { await _container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false); }
            catch (RequestFailedException ex) { _logger.LogDebug(ex, "Lease container ensure failed"); }
            _containerReady = true;
        }

        var blob = _container.GetBlobClient(BlobName(resource));
        try
        {
            if (!await blob.ExistsAsync(ct).ConfigureAwait(false))
            {
                using var empty = new MemoryStream();
                await blob.UploadAsync(empty, overwrite: false, cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Created concurrently or currently leased — both fine.
        }
        return blob;
    }

    private static string BlobName(string resource) => $"leases/{resource}";
}
