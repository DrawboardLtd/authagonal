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
/// <para>
/// Skipping is also why the set of rules here has to stay narrow. A refusal is invisible unless someone reads
/// Error-level logs, so every rule added here is a way for a documented, previously working configuration to
/// stop being applied while the host comes up healthy. That is how the administrative-scope refusal became a
/// silent admin lockout — see <see cref="Reject"/>.
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
    /// <remarks>
    /// The administrative scope is NOT refused here, and that is deliberate. It is reserved on the three
    /// paths an unprivileged caller can reach — the admin API, dynamic registration, and
    /// <c>POST /api/v1/token</c> — but configuration seeding is the trust root, not a caller: whoever writes
    /// the <c>Clients:</c> section can already set <c>AdminApi:Scope</c>, replace the signing keys, or point
    /// the host at another store, so refusing them the scope buys nothing and costs the product its only
    /// bootstrap.
    /// <para>
    /// Refusing it here locked every deployment out of its own admin API. <c>docs/admin-api.md</c>
    /// ("Bootstrapping the first admin token") documents a config-seeded <c>client_credentials</c> client
    /// carrying this scope as THE way to obtain an admin token, and the <c>IdentityAdmin</c> policy admits
    /// only a token whose <c>scope</c> claim holds it — issued solely by <c>/connect/token</c> against
    /// <c>AllowedScopes</c>. With the seeders refusing the descriptor, a fresh install could never reach
    /// <c>/api/v1/*</c> at all. Worse for an existing one: the seeders log at Error and SKIP, so rotating a
    /// suspected-compromised admin secret ("a config change + restart", per the same doc) silently wrote no
    /// new hash and the old secret kept authenticating — a rotation that reports success everywhere the
    /// operator looks, because the admin API keeps answering to the credential they believe they revoked.
    /// </para>
    /// <para>
    /// No test caught it: the only coverage of the admin surface writes its client straight into
    /// <c>IClientStore</c>, bypassing the seeder, so the documented bootstrap path was never exercised.
    /// </para>
    /// </remarks>
    public static string? Reject(IReadOnlyList<string>? scopes, IReadOnlyList<string>? audiences)
    {
        // A scope entry containing whitespace expands into several scopes downstream. Still refused, and
        // still on its own merit — an entry that is one string here and two scopes on the wire means the
        // stored client does not say what it appears to say. Refused rather than normalised: intent is
        // ambiguous.
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
