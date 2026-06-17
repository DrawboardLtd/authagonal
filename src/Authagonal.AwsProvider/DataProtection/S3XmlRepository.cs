using System.Xml.Linq;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Authagonal.AwsProvider.DataProtection;

/// <summary>
/// Persists the ASP.NET DataProtection key ring to S3 (one object per key element under a prefix), so
/// the ring is shared across pods — the AWS counterpart to <c>PersistKeysToAzureBlobStorage</c>.
/// <see cref="IXmlRepository"/> is synchronous, so the (infrequent: startup + rotation) S3 calls block.
/// </summary>
public sealed class S3XmlRepository(IAmazonS3 s3, string bucket, string prefix) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements() => GetAllAsync().GetAwaiter().GetResult();

    private async Task<IReadOnlyCollection<XElement>> GetAllAsync()
    {
        var elements = new List<XElement>();
        string? token = null;
        do
        {
            var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }).ConfigureAwait(false);

            foreach (var obj in resp.S3Objects ?? [])
            {
                if (!obj.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                using var o = await s3.GetObjectAsync(bucket, obj.Key).ConfigureAwait(false);
                using var reader = new StreamReader(o.ResponseStream);
                elements.Add(XElement.Parse(await reader.ReadToEndAsync().ConfigureAwait(false)));
            }

            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        }
        while (token is not null);

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        var name = string.IsNullOrWhiteSpace(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName;
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = $"{prefix}{name}.xml",
            ContentBody = element.ToString(),
        }).GetAwaiter().GetResult();
    }
}
