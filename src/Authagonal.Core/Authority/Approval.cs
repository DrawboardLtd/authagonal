using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Authagonal.Core.Authority;

public enum ApprovalStatus { Pending = 0, Approved = 1, Denied = 2 }

/// <summary>
/// A just-in-time approval: the runtime "ask me" a delegated exchange parks on when the
/// effective authority contains ask-policy actions. Stored as a <c>PersistedGrant</c> of type
/// <see cref="Approval.GrantType"/>; the agent polls the token endpoint with
/// <c>approval_id</c> (device-flow semantics: <c>authorization_pending</c> / <c>slow_down</c> /
/// <c>access_denied</c> / <c>expired_token</c>) while the user resolves it out-of-band. An
/// approval is bound to the exact request shape it was minted for via <see cref="RequestHash"/>
/// and is single-use: the winning mint consumes it atomically.
/// </summary>
public sealed class ApprovalData
{
    /// <summary>The approval handle, duplicated inside the payload because grants read back
    /// from storage carry no key.</summary>
    public required string Id { get; set; }

    public required string ClientId { get; set; }
    public required string SubjectId { get; set; }

    /// <summary>The effective authority slice awaiting approval — exactly what the mint will
    /// carry if approved (re-intersected with the live ceiling/consent at mint time).</summary>
    public AuthoritySet Slice { get; set; } = AuthoritySet.Empty;

    /// <summary>The (type, action) pairs whose ask policy triggered this approval — what the
    /// resolving UI should put in front of the user.</summary>
    public IReadOnlyList<string> PendingActions { get; set; } = [];

    /// <summary>Binds the approval to the triggering request's shape; a retry with different
    /// scopes/authority/audiences must not be able to spend it.</summary>
    public required string RequestHash { get; set; }

    /// <summary>
    /// The host extension parameters the exchange carried — the context the approved authority will
    /// be bound to.
    /// </summary>
    /// <remarks>
    /// Recorded so the resolving human can SEE it. The approval screen shows the client, the pending
    /// type:action pairs and the authority slice; a context-bound exchange scopes the resulting token
    /// to a tenant, project or workspace through these parameters, and none of that reached the
    /// person being asked to approve it. They are also part of the request hash now, so the
    /// displayed context is the context the approval can be redeemed against.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Context { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Who resolved it (normally the delegating subject; recorded for audit).</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>
    /// No longer written. The slow_down throttle rides IRateLimiter keyed on the approval handle,
    /// mirroring the device flow: persisting the marker meant rewriting the whole payload — Status
    /// included — from a copy read moments earlier, so a poll racing the user's answer could write
    /// `Pending` back over their approve or DENY. Kept so an approval serialized by an earlier
    /// version still deserializes.
    /// </summary>
    public DateTimeOffset? LastPolledAt { get; set; }
}

public static class Approval
{
    public const string GrantType = "approval";
    public const int PollIntervalSeconds = 5;

    public static string Key(string approvalId) => $"approval:{approvalId}";

    public static string Serialize(ApprovalData data)
    {
        var node = new JsonObject
        {
            ["id"] = data.Id,
            ["client_id"] = data.ClientId,
            ["subject_id"] = data.SubjectId,
            ["slice"] = AuthorityJson.ToNode(data.Slice),
            ["pending_actions"] = ToStringArray(data.PendingActions),
            ["request_hash"] = data.RequestHash,
            ["status"] = data.Status switch
            {
                ApprovalStatus.Approved => "approved",
                ApprovalStatus.Denied => "denied",
                _ => "pending",
            },
            ["created_at"] = Iso(data.CreatedAt),
        };
        if (data.Context.Count > 0)
        {
            var context = new JsonObject();
            foreach (var (name, value) in data.Context.OrderBy(p => p.Key, StringComparer.Ordinal))
                context[name] = value;
            node["context"] = context;
        }
        if (data.ResolvedAt is { } resolvedAt) node["resolved_at"] = Iso(resolvedAt);
        if (data.ResolvedBy is not null) node["resolved_by"] = data.ResolvedBy;
        if (data.LastPolledAt is { } polledAt) node["last_polled_at"] = Iso(polledAt);
        return node.ToJsonString();
    }

    public static ApprovalData? Parse(string data)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }
        if (node is not JsonObject obj) return null;

        var id = GetString(obj, "id");
        var clientId = GetString(obj, "client_id");
        var subjectId = GetString(obj, "subject_id");
        var requestHash = GetString(obj, "request_hash");
        if (id is null || clientId is null || subjectId is null || requestHash is null) return null;
        if (!AuthorityJson.TryParse(obj["slice"], out var slice)) return null;

        var context = new Dictionary<string, string>(StringComparer.Ordinal);
        if (obj["context"] is JsonObject contextObj)
        {
            foreach (var (name, value) in contextObj)
                if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                    context[name] = v.GetValue<string>();
        }

        return new ApprovalData
        {
            Id = id,
            ClientId = clientId,
            SubjectId = subjectId,
            Context = context,
            Slice = slice,
            PendingActions = ReadStringArray(obj["pending_actions"]),
            RequestHash = requestHash,
            Status = GetString(obj, "status") switch
            {
                "approved" => ApprovalStatus.Approved,
                "denied" => ApprovalStatus.Denied,
                _ => ApprovalStatus.Pending,
            },
            CreatedAt = GetDate(obj, "created_at") ?? default,
            ResolvedAt = GetDate(obj, "resolved_at"),
            ResolvedBy = GetString(obj, "resolved_by"),
            LastPolledAt = GetDate(obj, "last_polled_at"),
        };
    }

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static DateTimeOffset? GetDate(JsonObject obj, string name) =>
        GetString(obj, name) is { } s &&
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d : null;

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static JsonArray ToStringArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add((JsonNode)JsonValue.Create(value));
        return array;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array) return [];
        var values = new List<string>(array.Count);
        foreach (var element in array)
        {
            if (element is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                values.Add(v.GetValue<string>());
        }
        return values;
    }
}
