using Amazon.S3;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Authagonal.AwsProvider;

/// <summary>S3-backed DataProtection key persistence — the AWS counterpart to the Azure blob path in
/// <c>AddAuthagonal</c>. Call after <c>AddAuthagonal</c> so the key ring survives restarts and is shared
/// across pods.</summary>
public static class AwsDataProtectionExtensions
{
    public static IServiceCollection PersistDataProtectionKeysToS3(
        this IServiceCollection services, IAmazonS3 s3, string bucket, string prefix = "dataprotection/")
    {
        services.Configure<KeyManagementOptions>(o =>
            o.XmlRepository = new DataProtection.S3XmlRepository(s3, bucket, prefix));
        return services;
    }
}
