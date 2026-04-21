using Authagonal.Core.Models;
using Authagonal.Storage.Entities;

namespace Authagonal.Tests;

public class EntityRoundtripTests
{
    [Fact]
    public void UserEntity_Roundtrip_PreservesAllFields()
    {
        var user = new AuthUser
        {
            Id = "user-42",
            Email = "alice@example.com",
            NormalizedEmail = "ALICE@EXAMPLE.COM",
            PasswordHash = "hash123",
            EmailConfirmed = true,
            FirstName = "Alice",
            LastName = "Smith",
            CompanyName = "Acme",
            Phone = "+1234567890",
            OrganizationId = "org-1",
            AccessFailedCount = 3,
            LockoutEnabled = true,
            LockoutEnd = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            SecurityStamp = "stamp-abc",
            CreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var entity = UserEntity.FromModel(user);
        var result = entity.ToModel();

        Assert.Equal(user.Id, result.Id);
        Assert.Equal(user.Email, result.Email);
        Assert.Equal(user.NormalizedEmail, result.NormalizedEmail);
        Assert.Equal(user.PasswordHash, result.PasswordHash);
        Assert.Equal(user.EmailConfirmed, result.EmailConfirmed);
        Assert.Equal(user.FirstName, result.FirstName);
        Assert.Equal(user.LastName, result.LastName);
        Assert.Equal(user.CompanyName, result.CompanyName);
        Assert.Equal(user.Phone, result.Phone);
        Assert.Equal(user.OrganizationId, result.OrganizationId);
        Assert.Equal(user.AccessFailedCount, result.AccessFailedCount);
        Assert.Equal(user.LockoutEnabled, result.LockoutEnabled);
        Assert.Equal(user.LockoutEnd, result.LockoutEnd);
        Assert.Equal(user.SecurityStamp, result.SecurityStamp);
        Assert.Equal(user.CreatedAt, result.CreatedAt);
        Assert.Equal(user.UpdatedAt, result.UpdatedAt);
    }

    [Fact]
    public void UserEntity_FromModel_SetsCorrectKeys()
    {
        var user = new AuthUser
        {
            Id = "user-42",
            Email = "a@b.com",
            NormalizedEmail = "A@B.COM",
        };

        var entity = UserEntity.FromModel(user);

        Assert.Equal("user-42", entity.PartitionKey);
        Assert.Equal(UserEntity.ProfileRowKey, entity.RowKey);
    }

    [Fact]
    public void ClientEntity_Roundtrip_PreservesAllFields()
    {
        var client = new OAuthClient
        {
            ClientId = "my-spa",
            ClientName = "My SPA",
            ClientSecretHashes = ["hash1", "hash2"],
            AllowedGrantTypes = ["authorization_code"],
            RedirectUris = ["https://app.example.com/callback"],
            PostLogoutRedirectUris = ["https://app.example.com/logout"],
            AllowedScopes = ["openid", "profile", "email"],
            AllowedCorsOrigins = ["https://app.example.com"],
            RequirePkce = true,
            AllowOfflineAccess = true,
            RequireClientSecret = false,
            AlwaysIncludeUserClaimsInIdToken = true,
            AccessTokenLifetimeSeconds = 3600,
            IdentityTokenLifetimeSeconds = 600,
            AuthorizationCodeLifetimeSeconds = 120,
            AbsoluteRefreshTokenLifetimeSeconds = 86400,
            SlidingRefreshTokenLifetimeSeconds = 43200,
            DeviceCodeLifetimeSeconds = 600,
            RequirePushedAuthorizationRequests = true,
            RefreshTokenUsage = RefreshTokenUsage.OneTime,
        };

        var entity = ClientEntity.FromModel(client);
        var result = entity.ToModel();

        Assert.Equal(client.ClientId, result.ClientId);
        Assert.Equal(client.ClientName, result.ClientName);
        Assert.Equal(client.ClientSecretHashes, result.ClientSecretHashes);
        Assert.Equal(client.AllowedGrantTypes, result.AllowedGrantTypes);
        Assert.Equal(client.RedirectUris, result.RedirectUris);
        Assert.Equal(client.PostLogoutRedirectUris, result.PostLogoutRedirectUris);
        Assert.Equal(client.AllowedScopes, result.AllowedScopes);
        Assert.Equal(client.AllowedCorsOrigins, result.AllowedCorsOrigins);
        Assert.Equal(client.RequirePkce, result.RequirePkce);
        Assert.Equal(client.AllowOfflineAccess, result.AllowOfflineAccess);
        Assert.Equal(client.RequireClientSecret, result.RequireClientSecret);
        Assert.Equal(client.AlwaysIncludeUserClaimsInIdToken, result.AlwaysIncludeUserClaimsInIdToken);
        Assert.Equal(client.AccessTokenLifetimeSeconds, result.AccessTokenLifetimeSeconds);
        Assert.Equal(client.IdentityTokenLifetimeSeconds, result.IdentityTokenLifetimeSeconds);
        Assert.Equal(client.AuthorizationCodeLifetimeSeconds, result.AuthorizationCodeLifetimeSeconds);
        Assert.Equal(client.AbsoluteRefreshTokenLifetimeSeconds, result.AbsoluteRefreshTokenLifetimeSeconds);
        Assert.Equal(client.SlidingRefreshTokenLifetimeSeconds, result.SlidingRefreshTokenLifetimeSeconds);
        Assert.Equal(client.DeviceCodeLifetimeSeconds, result.DeviceCodeLifetimeSeconds);
        Assert.Equal(client.RequirePushedAuthorizationRequests, result.RequirePushedAuthorizationRequests);
        Assert.Equal(client.RefreshTokenUsage, result.RefreshTokenUsage);
    }

    [Fact]
    public void ClientEntity_FromModel_SetsCorrectKeys()
    {
        var client = new OAuthClient
        {
            ClientId = "my-spa",
            ClientName = "My SPA",
        };

        var entity = ClientEntity.FromModel(client);

        Assert.Equal("my-spa", entity.PartitionKey);
        Assert.Equal(ClientEntity.ConfigRowKey, entity.RowKey);
    }

    [Fact]
    public void ClientEntity_Roundtrip_EmptyLists_RoundtripAsEmpty()
    {
        var client = new OAuthClient
        {
            ClientId = "empty-client",
            ClientName = "Empty",
        };

        var entity = ClientEntity.FromModel(client);
        var result = entity.ToModel();

        Assert.Empty(result.ClientSecretHashes);
        Assert.Empty(result.AllowedCorsOrigins);
    }

    [Fact]
    public void GrantEntity_Roundtrip_PreservesAllFields()
    {
        var grant = new PersistedGrant
        {
            Key = "original-key",
            Type = "authorization_code",
            SubjectId = "user-1",
            ClientId = "spa-client",
            Data = "{\"scopes\":[\"openid\"]}",
            CreatedAt = new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2025, 3, 1, 12, 5, 0, TimeSpan.Zero),
            ConsumedAt = new DateTimeOffset(2025, 3, 1, 12, 1, 0, TimeSpan.Zero),
        };

        var entity = GrantEntity.FromModel(grant, "hashed-abc");
        var result = entity.ToModel();

        Assert.Equal(grant.Key, result.Key);
        Assert.Equal(grant.Type, result.Type);
        Assert.Equal(grant.SubjectId, result.SubjectId);
        Assert.Equal(grant.ClientId, result.ClientId);
        Assert.Equal(grant.Data, result.Data);
        Assert.Equal(grant.CreatedAt, result.CreatedAt);
        Assert.Equal(grant.ExpiresAt, result.ExpiresAt);
        Assert.Equal(grant.ConsumedAt, result.ConsumedAt);
    }

    [Fact]
    public void GrantEntity_FromModel_SetsCorrectKeys()
    {
        var grant = new PersistedGrant
        {
            Key = "k", Type = "t", ClientId = "c", Data = "d",
        };

        var entity = GrantEntity.FromModel(grant, "hashed-key");

        Assert.Equal("hashed-key", entity.PartitionKey);
        Assert.Equal(GrantEntity.GrantRowKey, entity.RowKey);
    }

    [Fact]
    public void GrantBySubjectEntity_Roundtrip_PreservesAllFields()
    {
        var grant = new PersistedGrant
        {
            Key = "refresh-key",
            Type = "refresh_token",
            SubjectId = "user-99",
            ClientId = "api-client",
            Data = "encrypted-data",
            CreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var entity = GrantBySubjectEntity.FromModel(grant, "hashed-xyz");
        var result = entity.ToModel();

        Assert.Equal(grant.Key, result.Key);
        Assert.Equal(grant.Type, result.Type);
        Assert.Equal(grant.SubjectId, result.SubjectId);
        Assert.Equal(grant.ClientId, result.ClientId);
        Assert.Equal(grant.Data, result.Data);
        Assert.Equal(grant.CreatedAt, result.CreatedAt);
        Assert.Equal(grant.ExpiresAt, result.ExpiresAt);
    }

    [Fact]
    public void GrantBySubjectEntity_FromModel_SetsCorrectKeys()
    {
        var grant = new PersistedGrant
        {
            Key = "k", Type = "refresh_token", SubjectId = "user-1",
            ClientId = "c", Data = "d",
        };

        var entity = GrantBySubjectEntity.FromModel(grant, "abc123");

        Assert.Equal("user-1", entity.PartitionKey);
        Assert.Equal("refresh_token|abc123", entity.RowKey);
    }

    [Fact]
    public void GrantBySubjectEntity_NullSubjectId_UsesEmptyString()
    {
        var grant = new PersistedGrant
        {
            Key = "k", Type = "t", SubjectId = null,
            ClientId = "c", Data = "d",
        };

        var entity = GrantBySubjectEntity.FromModel(grant, "hash");

        Assert.Equal(string.Empty, entity.PartitionKey);
    }

    [Fact]
    public void SigningKeyEntity_Roundtrip_PreservesAllFields()
    {
        var key = new SigningKeyInfo
        {
            KeyId = "key-2025",
            Algorithm = "RS256",
            RsaParametersJson = "{\"n\":\"abc\",\"e\":\"AQAB\"}",
            IsActive = true,
            CreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var entity = SigningKeyEntity.FromModel(key);
        var result = entity.ToModel();

        Assert.Equal(key.KeyId, result.KeyId);
        Assert.Equal(key.Algorithm, result.Algorithm);
        Assert.Equal(key.RsaParametersJson, result.RsaParametersJson);
        Assert.Equal(key.IsActive, result.IsActive);
        Assert.Equal(key.CreatedAt, result.CreatedAt);
        Assert.Equal(key.ExpiresAt, result.ExpiresAt);
    }

    [Fact]
    public void SigningKeyEntity_FromModel_SetsCorrectKeys()
    {
        var key = new SigningKeyInfo
        {
            KeyId = "key-2025",
            Algorithm = "RS256",
            RsaParametersJson = "{}",
        };

        var entity = SigningKeyEntity.FromModel(key);

        Assert.Equal(SigningKeyEntity.SigningPartitionKey, entity.PartitionKey);
        Assert.Equal("key-2025", entity.RowKey);
    }

    [Fact]
    public void SsoDomainEntity_Roundtrip_PreservesAllFields()
    {
        var domain = new SsoDomain
        {
            Domain = "Contoso.com",
            ProviderType = "saml",
            ConnectionId = "conn-1",
            Scheme = "saml-contoso",
        };

        var entity = SsoDomainEntity.FromModel(domain);
        var result = entity.ToModel();

        Assert.Equal(domain.Domain, result.Domain);
        Assert.Equal(domain.ProviderType, result.ProviderType);
        Assert.Equal(domain.ConnectionId, result.ConnectionId);
        Assert.Equal(domain.Scheme, result.Scheme);
    }

    [Fact]
    public void SsoDomainEntity_FromModel_NormalizesPartitionKey()
    {
        var domain = new SsoDomain
        {
            Domain = "Contoso.COM",
            ProviderType = "oidc",
            ConnectionId = "c1",
            Scheme = "s1",
        };

        var entity = SsoDomainEntity.FromModel(domain);

        Assert.Equal("contoso.com", entity.PartitionKey);
        Assert.Equal(SsoDomainEntity.MappingRowKey, entity.RowKey);
    }

    [Fact]
    public void SamlProviderEntity_Roundtrip_PreservesAllFields()
    {
        var config = new SamlProviderConfig
        {
            ConnectionId = "saml-conn-1",
            ConnectionName = "Contoso SAML",
            EntityId = "https://idp.contoso.com",
            MetadataLocation = "https://idp.contoso.com/metadata.xml",
            AllowedDomains = ["contoso.com", "contoso.org"],
            CreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var entity = SamlProviderEntity.FromModel(config);
        var result = entity.ToModel();

        Assert.Equal(config.ConnectionId, result.ConnectionId);
        Assert.Equal(config.ConnectionName, result.ConnectionName);
        Assert.Equal(config.EntityId, result.EntityId);
        Assert.Equal(config.MetadataLocation, result.MetadataLocation);
        Assert.Equal(config.AllowedDomains, result.AllowedDomains);
        Assert.Equal(config.CreatedAt, result.CreatedAt);
        Assert.Equal(config.UpdatedAt, result.UpdatedAt);
    }

    [Fact]
    public void SamlProviderEntity_FromModel_SetsCorrectKeys()
    {
        var config = new SamlProviderConfig
        {
            ConnectionId = "saml-conn-1",
            ConnectionName = "n",
            EntityId = "e",
            MetadataLocation = "m",
        };

        var entity = SamlProviderEntity.FromModel(config);

        Assert.Equal("saml-conn-1", entity.PartitionKey);
        Assert.Equal(SamlProviderEntity.ConfigRowKey, entity.RowKey);
    }

    [Fact]
    public void OidcProviderEntity_Roundtrip_PreservesAllFields()
    {
        var config = new OidcProviderConfig
        {
            ConnectionId = "oidc-conn-1",
            ConnectionName = "Azure AD",
            MetadataLocation = "https://login.microsoftonline.com/tenant/.well-known/openid-configuration",
            ClientId = "client-123",
            ClientSecret = "kv:oidc-conn-1-secret",
            RedirectUrl = "https://auth.example.com/oidc/callback",
            AllowedDomains = ["example.com"],
            CreatedAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var entity = OidcProviderEntity.FromModel(config);
        var result = entity.ToModel();

        Assert.Equal(config.ConnectionId, result.ConnectionId);
        Assert.Equal(config.ConnectionName, result.ConnectionName);
        Assert.Equal(config.MetadataLocation, result.MetadataLocation);
        Assert.Equal(config.ClientId, result.ClientId);
        Assert.Equal(config.ClientSecret, result.ClientSecret);
        Assert.Equal(config.RedirectUrl, result.RedirectUrl);
        Assert.Equal(config.AllowedDomains, result.AllowedDomains);
        Assert.Equal(config.CreatedAt, result.CreatedAt);
        Assert.Equal(config.UpdatedAt, result.UpdatedAt);
    }

    [Fact]
    public void OidcProviderEntity_FromModel_SetsCorrectKeys()
    {
        var config = new OidcProviderConfig
        {
            ConnectionId = "oidc-1",
            ConnectionName = "n",
            MetadataLocation = "m",
            ClientId = "c",
            ClientSecret = "s",
            RedirectUrl = "r",
        };

        var entity = OidcProviderEntity.FromModel(config);

        Assert.Equal("oidc-1", entity.PartitionKey);
        Assert.Equal(OidcProviderEntity.ConfigRowKey, entity.RowKey);
    }
}
