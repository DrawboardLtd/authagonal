namespace Authagonal.Core.Models;

public sealed class PersistedGrant
{
    public required string Key { get; set; }
    public required string Type { get; set; }
    public string? SubjectId { get; set; }
    public required string ClientId { get; set; }
    public required string Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>
    /// The sign-in session this grant was minted under, when it was minted under one. Null for a grant with
    /// no interactive session behind it (<c>client_credentials</c>, a token exchange, a device-code grant
    /// approved before the session id was carried) and for rows written before this field existed.
    /// </summary>
    /// <remarks>
    /// First-class rather than dug out of <see cref="Data"/>, because it is what
    /// <see cref="Authagonal.Core.Stores.IGrantStore.RemoveBySessionAsync"/> selects on and a storage
    /// provider must not have to understand a protocol payload to answer that query. Opaque to the stores,
    /// exactly like <see cref="ClientId"/> and <see cref="Type"/>.
    /// <para>
    /// Without it, "Log out other devices" could end the OP cookie for a session and nothing else: the
    /// refresh token the relying party on that device already held kept rotating, because grant removal
    /// could only be expressed as subject-wide or subject-and-client-wide — and revoking every session
    /// would have logged the user out of the device they chose to keep.
    /// </para>
    /// A null <c>SessionId</c> is never matched by a session-scoped removal. That is deliberate: a grant
    /// that cannot be attributed to the session being ended must not be destroyed by ending it.
    /// </remarks>
    public string? SessionId { get; set; }
}
