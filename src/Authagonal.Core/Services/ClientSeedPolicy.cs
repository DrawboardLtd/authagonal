namespace Authagonal.Core.Services;

/// <summary>
/// What configuration is allowed to seed onto a client — decided once, for both seeders.
/// </summary>
/// <remarks>
/// There are two of them and there has to be: <c>ProtocolSeedService</c> ships in
/// <c>Authagonal.Protocol</c>, which a consumer embeds without <c>Authagonal.Server</c>, and
/// <c>ClientSeedService</c> is the Server host's own. Merging the classes would mean the protocol package
/// depending on the server package, which is the wrong direction. So the CLASSES stay two and the POLICY
/// becomes one, which is where the drift actually lived.
/// <para>
/// The drift was not hypothetical. The audience validation and the declared-audiences flag existed in
/// neither, then in one, and the Server host's seeder had no audiences field at all — while a comment in the
/// authorize path asserted that "every surface that creates a client (dynamic registration, the admin API,
/// seed configuration) does accept audiences". Configuration was the one write path that could still put an
/// unbounded or non-absolute value into a signed token's <c>aud</c>, and a config-seeded client kept the
/// permissive legacy reading with nothing able to tighten it.
/// </para>
/// <para>
/// One function, one refusal reason, and both callers handle a refusal the same way: log at Error and skip
/// that descriptor. Skipping rather than throwing follows what both seeders already did for the two scope
/// rules — a single bad descriptor should not take the host down, and an operator who reads Error is told
/// exactly which client did not come up and why.
/// </para>
/// </remarks>
public static class ClientSeedPolicy
{
    /// <summary>
    /// Why this descriptor may not be seeded, or null when it may.
    /// </summary>
    /// <param name="scopes">The scope list that will actually be WRITTEN, after any preserve-existing merge.</param>
    /// <param name="audiences">
    /// The audiences that will be written, or null/empty when the descriptor names none — in which case there
    /// is nothing to validate and nothing to declare.
    /// </param>
    /// <param name="adminScope">
    /// The deployment's administrative scope name. No client may hold it, however it is being created: a
    /// <c>client_credentials</c> client that did could mint admin tokens indefinitely, and the admin API and
    /// dynamic registration both already refuse it.
    /// </param>
    public static string? Reject(
        IReadOnlyList<string>? scopes, IReadOnlyList<string>? audiences, string adminScope)
    {
        if (AdminScopeReservation.Grants(scopes, adminScope))
            return $"it requests the reserved administrative scope '{adminScope}'. No client may hold it — "
                + "a client_credentials client that did could mint admin tokens indefinitely";

        // A scope entry containing whitespace expands into several scopes downstream, which is how the
        // reservation above came to be bypassed. Refused rather than normalised: the intent is ambiguous.
        if (AdminScopeReservation.FindMalformedScope(scopes) is { } malformed)
            return $"scope entry '{malformed}' is not a single scope token. Scope names cannot contain "
                + "whitespace — list each scope separately";

        if (audiences is { Count: > 0 } && ResourceAudiencePolicy.RejectAudiences(audiences) is { } why)
            return $"{why}. An audience becomes the `aud` of a signed token, so it must be an absolute URI "
                + "within the documented caps";

        return null;
    }

    /// <summary>
    /// True when a descriptor's audience list should mark the client as having DECLARED its audiences.
    /// </summary>
    /// <remarks>
    /// Naming any is declaring, which is the rule the admin API applies. An empty list is indistinguishable
    /// from a descriptor that never mentioned audiences, so it must NOT declare — doing so would silently
    /// forbid every seeded client from naming a resource, a behaviour change on upgrade for deployments that
    /// work today.
    /// </remarks>
    public static bool Declares(IReadOnlyList<string>? audiences) => audiences is { Count: > 0 };
}
