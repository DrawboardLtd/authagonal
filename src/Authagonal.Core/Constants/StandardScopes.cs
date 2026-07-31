namespace Authagonal.Core.Constants;

public static class StandardScopes
{
    public const string OpenId = "openid";
    public const string Profile = "profile";
    public const string Email = "email";
    public const string OfflineAccess = "offline_access";

    /// <summary>
    /// OIDC Core §5.4 binds <c>phone_number</c> and <c>phone_number_verified</c> to this scope.
    /// </summary>
    /// <remarks>
    /// The phone claims were previously released under <see cref="Profile"/>, and <c>phone</c> was
    /// not advertised at all — so there was no scope a client could request to obtain them
    /// legitimately and none it could decline to avoid them, while <c>phone_number</c> was listed in
    /// <c>claims_supported</c> regardless.
    /// </remarks>
    public const string Phone = "phone";

    /// <summary>
    /// Releases the <c>roles</c> claim. Not an OIDC standard scope — this product treats role
    /// membership as a claim the end-user consents to disclose, and it was previously released to
    /// every client with no scope gate at all.
    /// </summary>
    public const string Roles = "roles";

    /// <summary>
    /// Releases the <c>groups</c> claim (SCIM group membership). As <see cref="Roles"/>: not an OIDC
    /// standard scope, and previously ungated.
    /// </summary>
    public const string Groups = "groups";
}
