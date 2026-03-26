namespace Authagonal.Migration;

internal sealed class MigrationStats
{
    public int UsersCreated;
    public int UsersUpdated;
    public int LoginsCreated;
    public int LoginsSkipped;
    public int SamlProvidersCreated;
    public int OidcProvidersCreated;
    public int SsoDomainsCreated;
    public int ClientsCreated;
    public int RefreshTokensCreated;
    public int RefreshTokensSkipped;
}
