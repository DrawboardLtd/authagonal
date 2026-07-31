using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;

namespace Authagonal.Core.Authority;

/// <summary>What a redeemed capability ticket hands back: the token it was bound to and the
/// authority narrowing recorded at mint (null = the token's own authority applies as-is).</summary>
public sealed record CapabilityTicket(
    string BoundToken,
    string ClientId,
    string? SubjectId,
    AuthoritySet? Narrowing);

/// <summary>
/// Short-lived, single-use, opaque handles that stand in for a real token — the BFF ws-ticket
/// pattern generalized into a first-class broker primitive. Mint one bound to a (typically
/// delegated, already-attenuated) token and hand the handle to an edge that can't be trusted
/// with the token itself; whoever terminates the call redeems it server-side exactly once.
/// </summary>
public interface ICapabilityTicketService
{
    /// <param name="narrowing">Optional extra attenuation recorded on the ticket for the
    /// redeeming host to enforce alongside the token's own authorization_details claim.</param>
    /// <param name="ttl">Default 30 seconds — a ticket bridges one connect, not a session.</param>
    Task<string> MintAsync(
        string boundToken,
        string clientId,
        string? subjectId = null,
        AuthoritySet? narrowing = null,
        TimeSpan? ttl = null,
        CancellationToken ct = default);

    /// <summary>Null when the handle is unknown, expired, or already redeemed. Exactly one
    /// concurrent caller can win a given handle.</summary>
    Task<CapabilityTicket?> TryRedeemAsync(string handle, CancellationToken ct = default);
}

/// <summary>
/// Grant-store-backed implementation: durable (pod-restart-safe) and atomically single-use via
/// <see cref="IGrantStore.TryConsumeAsync"/> — the ETag-conditional delete closes the
/// get-then-remove replay window a plain distributed cache leaves open.
/// </summary>
public sealed class GrantStoreCapabilityTicketService(
    IGrantStore grantStore,
    IEnumerable<IAuthHook> authHooks) : ICapabilityTicketService
{
    public const string GrantType = "capability_ticket";
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    public static string Key(string handle) => $"capability_ticket:{handle}";

    public async Task<string> MintAsync(
        string boundToken,
        string clientId,
        string? subjectId = null,
        AuthoritySet? narrowing = null,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(boundToken);
        ArgumentException.ThrowIfNullOrEmpty(clientId);

        var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        var data = new JsonObject { ["token"] = boundToken };
        if (narrowing is not null)
            data["narrowing"] = AuthorityJson.ToNode(narrowing);

        await grantStore.StoreAsync(new PersistedGrant
        {
            Key = Key(handle),
            Type = GrantType,
            SubjectId = subjectId,
            ClientId = clientId,
            Data = data.ToJsonString(),
            CreatedAt = now,
            ExpiresAt = now + (ttl ?? DefaultTtl),
        }, ct);

        return handle;
    }

    public async Task<CapabilityTicket?> TryRedeemAsync(string handle, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(handle))
            return null;

        var key = Key(handle);
        var grant = await grantStore.GetAsync(key, ct);
        if (grant is null || grant.Type != GrantType || grant.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;

        // Atomic single-use: only the caller that wins the conditional delete redeems.
        if (!await grantStore.TryConsumeAsync(key, ct))
            return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(grant.Data);
        }
        catch (JsonException)
        {
            return null;
        }
        if (node is not JsonObject obj ||
            obj["token"] is not JsonValue tokenValue ||
            tokenValue.GetValueKind() != JsonValueKind.String)
            return null;

        // Present-but-unreadable must not widen. Null narrowing means "the token's own authority
        // applies as-is", so letting a garbled member fall through to null turned a corrupt ticket
        // into an UNattenuated one — the opposite of every other authority read in the tree
        // (AuthorityEvaluator.ParseOrDeny, ProtocolTokenService.ReadAuthorityClaim, AgentProfileEntity
        // .ToModel), which all fall back to Empty. Absent is fine; unparseable fails the redemption.
        AuthoritySet? narrowing = null;
        if (obj["narrowing"] is { } narrowingNode)
        {
            if (!AuthorityJson.TryParse(narrowingNode, out var parsed))
                return null;
            narrowing = parsed;
        }

        await authHooks.RunOnCapabilityTicketRedeemedAsync(handle, grant.SubjectId, grant.ClientId, ct);

        return new CapabilityTicket(tokenValue.GetValue<string>(), grant.ClientId, grant.SubjectId, narrowing);
    }
}
