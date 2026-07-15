using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Testcontainers.DynamoDb;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Shared DynamoDB Local container for the AWS-provider suites — the Dynamo counterpart to
/// <see cref="AzuriteFixture"/>. Tests isolate themselves by table-name prefix, same convention as
/// the Azurite suites.
/// </summary>
public sealed class DynamoFixture : IAsyncLifetime
{
    private readonly DynamoDbContainer _container = new DynamoDbBuilder("amazon/dynamodb-local:latest")
        .Build();

    /// <summary>A client against the local endpoint (credentials are ignored by DynamoDB Local).</summary>
    public IAmazonDynamoDB CreateClient() => new AmazonDynamoDBClient(
        new BasicAWSCredentials("local", "local"),
        new AmazonDynamoDBConfig { ServiceURL = _container.GetConnectionString() });

    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("Dynamo")]
public class DynamoCollection : ICollectionFixture<DynamoFixture> { }
