using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Server.Services;

namespace Authagonal.Tests;

public sealed class ScimPatchApplierTests
{
    [Fact]
    public void ApplyToUser_ReplaceActive_SetsActive()
    {
        var user = CreateTestUser();
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("replace", "active", JsonDocument.Parse("false").RootElement)
        };

        ScimPatchApplier.ApplyToUser(user, ops);
        Assert.False(user.IsActive);
    }

    [Fact]
    public void ApplyToUser_ReplaceGivenName_SetsFirstName()
    {
        var user = CreateTestUser();
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("replace", "name.givenName", JsonDocument.Parse("\"Alice\"").RootElement)
        };

        ScimPatchApplier.ApplyToUser(user, ops);
        Assert.Equal("Alice", user.FirstName);
    }

    [Fact]
    public void ApplyToUser_ReplaceFamilyName_SetsLastName()
    {
        var user = CreateTestUser();
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("replace", "name.familyName", JsonDocument.Parse("\"Smith\"").RootElement)
        };

        ScimPatchApplier.ApplyToUser(user, ops);
        Assert.Equal("Smith", user.LastName);
    }

    [Fact]
    public void ApplyToUser_ReplaceUserName_SetsEmail()
    {
        var user = CreateTestUser();
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("replace", "userName", JsonDocument.Parse("\"new@example.com\"").RootElement)
        };

        ScimPatchApplier.ApplyToUser(user, ops);
        Assert.Equal("new@example.com", user.Email);
        Assert.Equal("NEW@EXAMPLE.COM", user.NormalizedEmail);
    }

    [Fact]
    public void ApplyToUser_ReplaceWithObjectValue_AppliesFields()
    {
        var user = CreateTestUser();
        var json = """{"active": false, "userName": "updated@example.com"}""";
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("replace", null, JsonDocument.Parse(json).RootElement)
        };

        ScimPatchApplier.ApplyToUser(user, ops);
        Assert.False(user.IsActive);
        Assert.Equal("updated@example.com", user.Email);
    }

    [Fact]
    public void ApplyToUser_ReplaceExternalId_SetsExternalId()
    {
        var user = CreateTestUser();
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("replace", "externalId", JsonDocument.Parse("\"ext-new\"").RootElement)
        };

        ScimPatchApplier.ApplyToUser(user, ops);
        Assert.Equal("ext-new", user.ExternalId);
    }

    [Fact]
    public void ApplyToGroup_ReplaceDisplayName()
    {
        var group = CreateTestGroup();
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("replace", "displayName", JsonDocument.Parse("\"New Name\"").RootElement)
        };

        ScimPatchApplier.ApplyToGroup(group, ops);
        Assert.Equal("New Name", group.DisplayName);
    }

    [Fact]
    public void ApplyToGroup_AddMembers()
    {
        var group = CreateTestGroup();
        var json = """[{"value": "user1"}, {"value": "user2"}]""";
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("add", "members", JsonDocument.Parse(json).RootElement)
        };

        ScimPatchApplier.ApplyToGroup(group, ops);
        Assert.Contains("user1", group.MemberUserIds);
        Assert.Contains("user2", group.MemberUserIds);
    }

    [Fact]
    public void ApplyToGroup_RemoveMembers()
    {
        var group = CreateTestGroup();
        group.MemberUserIds = ["user1", "user2", "user3"];

        var json = """[{"value": "user2"}]""";
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("remove", "members", JsonDocument.Parse(json).RootElement)
        };

        ScimPatchApplier.ApplyToGroup(group, ops);
        Assert.DoesNotContain("user2", group.MemberUserIds);
        Assert.Contains("user1", group.MemberUserIds);
        Assert.Contains("user3", group.MemberUserIds);
    }

    private static AuthUser CreateTestUser() => new()
    {
        Id = "test-id",
        Email = "test@example.com",
        NormalizedEmail = "TEST@EXAMPLE.COM",
        FirstName = "Test",
        LastName = "User",
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static ScimGroup CreateTestGroup() => new()
    {
        Id = "group-id",
        DisplayName = "Test Group",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
