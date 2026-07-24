using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Authagonal.Core.Authority;

/// <summary>
/// Wire format for a user's standing agent consent — the per-(user, agent) floor, stored as a
/// <c>PersistedGrant</c> of type <see cref="GrantType"/>. Lives in Core (JsonNode-based, no
/// source-gen) because both the Server consent endpoints (write) and the Protocol exchange
/// path (read, at every mint) share it. The stored floor is a snapshot of what the user
/// granted; it is re-intersected with the LIVE ceiling at every mint, so an admin narrowing
/// takes effect immediately without consent migration.
/// </summary>
public static class AgentConsent
{
    public const string GrantType = "agent_consent";

    public static string Key(string subjectId, string clientId) =>
        $"agent_consent:{subjectId}:{clientId}";

    public static string Serialize(AuthoritySet floor, DateTimeOffset consentedAt)
    {
        var node = new JsonObject
        {
            ["authority"] = AuthorityJson.ToNode(floor),
            ["consented_at"] = consentedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };
        return node.ToJsonString();
    }

    public static bool TryParse(string data, out AuthoritySet floor, out DateTimeOffset consentedAt)
    {
        floor = AuthoritySet.Empty;
        consentedAt = default;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject obj) return false;
        if (!AuthorityJson.TryParse(obj["authority"], out floor)) return false;

        if (obj["consented_at"] is JsonValue at && at.GetValueKind() == JsonValueKind.String &&
            DateTimeOffset.TryParse(at.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            consentedAt = parsed;
        }
        return true;
    }
}
