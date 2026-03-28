using Authagonal.Core.Models;
using Authagonal.Storage.Entities;

namespace Authagonal.Tests;

public class UserLoginEntityTests
{
    [Fact]
    public void FromModelForward_SetsCorrectKeys()
    {
        var login = new ExternalLoginInfo
        {
            UserId = "user-1",
            Provider = "google",
            ProviderKey = "google-uid-123",
            DisplayName = "alice@gmail.com",
        };

        var entity = UserLoginEntity.FromModelForward(login);

        Assert.Equal("google|google-uid-123", entity.PartitionKey);
        Assert.Equal(UserLoginEntity.LookupRowKey, entity.RowKey);
    }

    [Fact]
    public void FromModelReverse_SetsCorrectKeys()
    {
        var login = new ExternalLoginInfo
        {
            UserId = "user-1",
            Provider = "microsoft",
            ProviderKey = "ms-oid-456",
            DisplayName = "bob@outlook.com",
        };

        var entity = UserLoginEntity.FromModelReverse(login);

        Assert.Equal("user-1", entity.PartitionKey);
        Assert.Equal("login|microsoft|ms-oid-456", entity.RowKey);
    }

    [Fact]
    public void Forward_ToModel_PreservesAllFields()
    {
        var login = new ExternalLoginInfo
        {
            UserId = "user-1",
            Provider = "saml",
            ProviderKey = "saml-nameid-789",
            DisplayName = "Carol",
        };

        var result = UserLoginEntity.FromModelForward(login).ToModel();

        Assert.Equal(login.UserId, result.UserId);
        Assert.Equal(login.Provider, result.Provider);
        Assert.Equal(login.ProviderKey, result.ProviderKey);
        Assert.Equal(login.DisplayName, result.DisplayName);
    }

    [Fact]
    public void Reverse_ToModel_PreservesAllFields()
    {
        var login = new ExternalLoginInfo
        {
            UserId = "user-2",
            Provider = "oidc",
            ProviderKey = "oidc-sub-abc",
            DisplayName = null,
        };

        var result = UserLoginEntity.FromModelReverse(login).ToModel();

        Assert.Equal(login.UserId, result.UserId);
        Assert.Equal(login.Provider, result.Provider);
        Assert.Equal(login.ProviderKey, result.ProviderKey);
        Assert.Null(result.DisplayName);
    }

    [Fact]
    public void ReverseRowKey_StartsWithLoginPrefix()
    {
        var login = new ExternalLoginInfo
        {
            UserId = "u", Provider = "p", ProviderKey = "k",
        };

        var entity = UserLoginEntity.FromModelReverse(login);

        Assert.StartsWith(UserLoginEntity.LoginRowKeyPrefix, entity.RowKey);
    }
}
