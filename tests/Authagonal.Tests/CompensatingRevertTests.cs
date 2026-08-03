using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Authagonal.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// The compensating revert after a failed email claim does not undo somebody else's write.
/// </summary>
/// <remarks>
/// <c>UpdateAsync</c> goes to considerable lengths to make the profile write safe — a caller-revision check
/// plus a version/ETag-conditional replace, with a retry budget — and then, when the new email claim loses,
/// undid the committed profile row with an UNCONDITIONAL full-row write of the snapshot it had read.
/// <para>
/// Two consequences, both of which this project has already treated as defects elsewhere. Any write landing
/// between the profile replace and the revert was silently reverted: <c>IsActive</c> (undoing a SCIM
/// deprovision), <c>SecurityStamp</c> (undoing a reset's session invalidation), <c>RolesJson</c> (undoing a
/// role revocation), the password hash, an active lockout. That is verbatim the defect fixed for
/// <c>RecordSuccessfulLoginAsync</c>. And because Upsert/Put CREATES the row, an account deleted during the
/// window — SCIM deprovision, admin delete, GDPR erasure — was resurrected with its password hash, roles and
/// MFA flag intact.
/// </para>
/// <para>
/// Exercised against the SQL store, whose <c>PutIfVersionAsync</c> is update-only and version-conditional so
/// both halves are observable without a live Azure Table. The Azure sibling takes the same shape with the
/// ETag the profile write returned.
/// </para>
/// </remarks>
public sealed class CompensatingRevertTests
{
    /// <summary>A fresh SQLite-backed table, per test, so versions start from a known place.</summary>
    private static async Task<SqlTable> NewTableAsync()
    {
        var source = SqlTestSource.Sqlite();
        var name = "Users" + Guid.NewGuid().ToString("N")[..8];
        await source.EnsureTableAsync(name);
        return new SqlTable(source, name);
    }

    /// <summary>
    /// A revert cannot resurrect a user deleted while the update was in flight.
    /// </summary>
    /// <remarks>
    /// The strongest of the two: the old code's write would have re-created the row, so an account someone
    /// had just erased came back holding its password hash and roles.
    /// </remarks>
    [Fact]
    public async Task ARevertDoesNotResurrectADeletedUser()
    {
        var users = await NewTableAsync();

        var row = new SqlRow(EnvPartitioner.Live.PK("user-1"), "profile");
        row.PutS("data", "{}");
        await users.PutAsync(row);

        var stored = await users.GetAsync(row.Pk, row.Sk);
        Assert.NotNull(stored);

        // The row is deleted while our update is in flight.
        await users.DeleteAsync(row.Pk, row.Sk);

        // The revert is conditional and update-only, so it writes nothing.
        var reverted = await users.PutIfVersionAsync(row, stored.Version + 1);

        Assert.False(reverted);
        Assert.Null(await users.GetAsync(row.Pk, row.Sk));
    }

    /// <summary>A revert cannot undo a write that landed after ours.</summary>
    [Fact]
    public async Task ARevertDoesNotUndoALaterWrite()
    {
        var users = await NewTableAsync();

        var original = new SqlRow(EnvPartitioner.Live.PK("user-2"), "profile");
        original.PutS("data", """{"isActive":true}""");
        await users.PutAsync(original);
        var atRead = (await users.GetAsync(original.Pk, original.Sk))!.Version;

        // Our own profile write.
        var ours = new SqlRow(original.Pk, original.Sk);
        ours.PutS("data", """{"isActive":true,"email":"new@acme.test"}""");
        Assert.True(await users.PutIfVersionAsync(ours, atRead));
        var afterOurs = atRead + 1;

        // Somebody deactivates the account — a SCIM deprovision — between our write and our revert.
        var deactivated = new SqlRow(original.Pk, original.Sk);
        deactivated.PutS("data", """{"isActive":false}""");
        Assert.True(await users.PutIfVersionAsync(deactivated, afterOurs));

        // Our revert, conditional on the version WE wrote, must lose.
        var revert = new SqlRow(original.Pk, original.Sk);
        revert.PutS("data", """{"isActive":true}""");
        Assert.False(await users.PutIfVersionAsync(revert, afterOurs));

        // The deactivation stands.
        var final = await users.GetAsync(original.Pk, original.Sk);
        Assert.Contains("\"isActive\":false", final!.GetStr("data"));
    }

    /// <summary>The control: with nothing else touching the row, the revert applies.</summary>
    /// <remarks>
    /// Without this, a revert that never wrote anything would satisfy both tests above — and the whole point
    /// of the compensation is that a lost email claim does not leave the profile asserting an address whose
    /// index row names a different user.
    /// </remarks>
    [Fact]
    public async Task ARevertStillAppliesWhenNobodyElseHasWritten()
    {
        var users = await NewTableAsync();

        var original = new SqlRow(EnvPartitioner.Live.PK("user-3"), "profile");
        original.PutS("data", """{"email":"old@acme.test"}""");
        await users.PutAsync(original);
        var atRead = (await users.GetAsync(original.Pk, original.Sk))!.Version;

        var ours = new SqlRow(original.Pk, original.Sk);
        ours.PutS("data", """{"email":"new@acme.test"}""");
        Assert.True(await users.PutIfVersionAsync(ours, atRead));

        var revert = new SqlRow(original.Pk, original.Sk);
        revert.PutS("data", """{"email":"old@acme.test"}""");
        Assert.True(await users.PutIfVersionAsync(revert, atRead + 1));

        var final = await users.GetAsync(original.Pk, original.Sk);
        Assert.Contains("old@acme.test", final!.GetStr("data"));
    }
}
