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

    // F38 — Okta's deprovisioning shape: id in a value-path filter, no value array.
    [Fact]
    public void ApplyToGroup_RemoveMember_ByPathFilter_Okta()
    {
        var group = CreateTestGroup();
        group.MemberUserIds = ["user1", "user2", "user3"];

        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("remove", "members[value eq \"user2\"]", Value: null)
        };

        ScimPatchApplier.ApplyToGroup(group, ops);
        Assert.DoesNotContain("user2", group.MemberUserIds);
        Assert.Contains("user1", group.MemberUserIds);
        Assert.Contains("user3", group.MemberUserIds);
    }

    // F38 — "remove members" with no value = remove ALL members.
    [Fact]
    public void ApplyToGroup_RemoveAllMembers_NoValue()
    {
        var group = CreateTestGroup();
        group.MemberUserIds = ["user1", "user2"];

        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("remove", "members", Value: null)
        };

        ScimPatchApplier.ApplyToGroup(group, ops);
        Assert.Empty(group.MemberUserIds);
    }

    // F38 — replace members = full set replacement (drop existing, add supplied).
    [Fact]
    public void ApplyToGroup_ReplaceMembers_SetsExactMembership()
    {
        var group = CreateTestGroup();
        group.MemberUserIds = ["user1", "user2"];

        var json = """[{"value": "user3"}]""";
        var ops = new List<ScimPatchApplier.PatchOperation>
        {
            new("replace", "members", JsonDocument.Parse(json).RootElement)
        };

        ScimPatchApplier.ApplyToGroup(group, ops);
        Assert.Equal(["user3"], group.MemberUserIds);
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

    // -----------------------------------------------------------------------
    // remove, and honest failure (#44)
    // -----------------------------------------------------------------------

    /// <summary>
    /// `remove` fell through the `replace or add` test and was silently discarded, while the response still
    /// said 200 and ServiceProviderConfig advertised patch.supported = true. A directory therefore believed
    /// it had cleared an attribute when it had not, and would never retry.
    /// </summary>
    [Theory]
    [InlineData("name.givenName")]
    [InlineData("name.familyName")]
    [InlineData("externalId")]
    [InlineData("preferredLanguage")]
    public void Remove_ClearsTheAttribute(string path)
    {
        var user = new Authagonal.Core.Models.AuthUser
        {
            Id = "u1",
            Email = "u@example.com",
            NormalizedEmail = "U@EXAMPLE.COM",
            FirstName = "Given",
            LastName = "Family",
            ExternalId = "ext-1",
            Locale = "en",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var unsupported = ScimPatchApplier.ApplyToUser(user,
            [new ScimPatchApplier.PatchOperation("remove", path, null)]);

        Assert.Empty(unsupported);
        switch (path)
        {
            case "name.givenName": Assert.Null(user.FirstName); break;
            case "name.familyName": Assert.Null(user.LastName); break;
            case "externalId": Assert.Null(user.ExternalId); break;
            case "preferredLanguage": Assert.Null(user.Locale); break;
        }
    }

    /// <summary>An unrecognised path must be REPORTED, not silently ignored.</summary>
    [Fact]
    public void UnknownPath_IsReportedRatherThanSilentlyIgnored()
    {
        var user = NewUser();

        var replaceUnknown = ScimPatchApplier.ApplyToUser(user,
            [new ScimPatchApplier.PatchOperation("replace", "nickname",
                System.Text.Json.JsonDocument.Parse("\"Nick\"").RootElement)]);
        Assert.NotEmpty(replaceUnknown);

        var removeUnknown = ScimPatchApplier.ApplyToUser(user,
            [new ScimPatchApplier.PatchOperation("remove", "nickname", null)]);
        Assert.NotEmpty(removeUnknown);
    }

    /// <summary>
    /// Clearing userName or active would leave the resource unusable, so those are refused rather than
    /// silently ignored — the caller learns the operation did not apply.
    /// </summary>
    [Theory]
    [InlineData("userName")]
    [InlineData("active")]
    public void Remove_OfRequiredAttributes_IsRefused(string path)
    {
        var unsupported = ScimPatchApplier.ApplyToUser(NewUser(),
            [new ScimPatchApplier.PatchOperation("remove", path, null)]);
        Assert.NotEmpty(unsupported);
    }

    /// <summary>An unknown operation verb must be reported.</summary>
    [Fact]
    public void UnknownOperation_IsReported()
    {
        var unsupported = ScimPatchApplier.ApplyToUser(NewUser(),
            [new ScimPatchApplier.PatchOperation("frobnicate", "active", null)]);
        Assert.NotEmpty(unsupported);
    }

    /// <summary>A supported replace must still report nothing — the guard must not over-report.</summary>
    [Fact]
    public void SupportedReplace_ReportsNothing()
    {
        var unsupported = ScimPatchApplier.ApplyToUser(NewUser(),
            [new ScimPatchApplier.PatchOperation("replace", "name.givenName",
                System.Text.Json.JsonDocument.Parse("\"Updated\"").RootElement)]);
        Assert.Empty(unsupported);
    }

    private static Authagonal.Core.Models.AuthUser NewUser() => new()
    {
        Id = "u1",
        Email = "u@example.com",
        NormalizedEmail = "U@EXAMPLE.COM",
        FirstName = "Given",
        LastName = "Family",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
