using Testcontainers.Azurite;

namespace Authagonal.Tests.Infrastructure;

public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("Azurite")]
public class AzuriteCollection : ICollectionFixture<AzuriteFixture> { }
