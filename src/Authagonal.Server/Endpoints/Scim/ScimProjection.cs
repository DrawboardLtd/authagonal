using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Authagonal.Server.Endpoints.Scim;

/// <summary>
/// RFC 7644 §3.9 attribute projection: <c>?attributes=</c> narrows the response to the named
/// attributes, <c>?excludedAttributes=</c> removes them.
/// </summary>
/// <remarks>
/// Both were parsed off the query string by nobody and answered by nobody, so a client asking for
/// <c>?attributes=id,userName</c> got every attribute of every user in the page. That is a
/// disclosure the caller explicitly asked not to receive, and it is the parameter a well-behaved
/// provisioning connector uses precisely to avoid pulling PII it has no use for.
/// </remarks>
public sealed class ScimProjection
{
    /// <summary>
    /// Always returned regardless of what was asked for. RFC 7643 §7 gives <c>id</c> and
    /// <c>schemas</c> a returned characteristic of "always"; <c>meta</c> is kept with them because
    /// dropping it leaves a resource a client cannot address or version.
    /// </summary>
    private static readonly HashSet<string> AlwaysReturned =
        new(StringComparer.OrdinalIgnoreCase) { "schemas", "id", "meta" };

    private readonly HashSet<string> _top;
    private readonly Dictionary<string, HashSet<string>> _sub;
    private readonly bool _isExclusion;

    private ScimProjection(IEnumerable<string> paths, bool isExclusion)
    {
        _isExclusion = isExclusion;
        _top = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _sub = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths)
        {
            // A path may be URN-qualified ("urn:...:User:userName"). The last colon-separated chunk
            // is the attribute path; core-schema names carry no colon at all.
            var path = raw.Trim();
            var lastColon = path.LastIndexOf(':');
            if (lastColon >= 0 && lastColon < path.Length - 1)
                path = path[(lastColon + 1)..];

            var dot = path.IndexOf('.');
            if (dot <= 0)
            {
                _top.Add(path);
                continue;
            }

            var parent = path[..dot];
            var child = path[(dot + 1)..];
            _top.Add(parent);
            if (!_sub.TryGetValue(parent, out var children))
                _sub[parent] = children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            children.Add(child);
        }
    }

    /// <summary>
    /// Builds a projection from the query parameters, or reports why they cannot be honoured. A null
    /// projection with no error means "return everything", which is the default.
    /// </summary>
    public static bool TryCreate(
        string? attributes, string? excludedAttributes,
        out ScimProjection? projection, [NotNullWhen(false)] out string? error)
    {
        projection = null;
        error = null;

        var included = Split(attributes);
        var excluded = Split(excludedAttributes);

        // §3.9 makes them mutually exclusive. Silently preferring one would answer a question the
        // caller did not ask, which is the same failure this whole parameter set exists to avoid.
        if (included.Count > 0 && excluded.Count > 0)
        {
            error = "attributes and excludedAttributes are mutually exclusive.";
            return false;
        }

        if (included.Count > 0)
            projection = new ScimProjection(included, isExclusion: false);
        else if (excluded.Count > 0)
            projection = new ScimProjection(excluded, isExclusion: true);

        return true;
    }

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Applies <paramref name="projection"/> to a resource, or returns it untouched when
    /// there is nothing to apply.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "SCIM resources are already serialized reflectively by ScimResults")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "SCIM resources are already serialized reflectively by ScimResults")]
    public static object Apply(object resource, ScimProjection? projection)
    {
        if (projection is null) return resource;

        var node = JsonSerializer.SerializeToNode(resource, resource.GetType(), SerializerOptions);
        if (node is not JsonObject obj) return resource;

        projection.Prune(obj);
        return obj;
    }

    /// <summary>Applies the projection to each member of a list response's Resources.</summary>
    public static IReadOnlyList<object> ApplyAll(IEnumerable<object> resources, ScimProjection? projection) =>
        projection is null
            ? resources.ToList()
            : resources.Select(r => Apply(r, projection)).ToList();

    /// <summary>
    /// Property names are fixed by JsonPropertyName on the SCIM DTOs, so no naming policy is applied
    /// — a camelCasing pass here would rename members the specification pins.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = null };

    private void Prune(JsonObject obj)
    {
        foreach (var name in obj.Select(p => p.Key).ToList())
        {
            if (AlwaysReturned.Contains(name)) continue;

            var named = _top.Contains(name);

            if (_isExclusion)
            {
                // Naming a parent with sub-attributes excludes only those, not the whole complex
                // attribute — "excludedAttributes=name.formatted" must still return name.givenName.
                if (named && !_sub.ContainsKey(name)) obj.Remove(name);
                else if (named) PruneChildren(obj[name], _sub[name], remove: true);
                continue;
            }

            if (!named) { obj.Remove(name); continue; }
            if (_sub.TryGetValue(name, out var children)) PruneChildren(obj[name], children, remove: false);
        }
    }

    private static void PruneChildren(JsonNode? node, HashSet<string> children, bool remove)
    {
        switch (node)
        {
            case JsonObject child:
                foreach (var name in child.Select(p => p.Key).ToList())
                {
                    if (children.Contains(name) == remove) child.Remove(name);
                }
                break;
            case JsonArray array:
                foreach (var element in array) PruneChildren(element, children, remove);
                break;
        }
    }
}
