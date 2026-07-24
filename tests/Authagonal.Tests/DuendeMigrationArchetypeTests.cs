using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.AzureProvider.Stores;
using Authagonal.Migration;
using Authagonal.Server.Services; // PasswordHasher, TotpService, RecoveryCodeService, PlaintextSecretProvider
using Authagonal.Protocol.Services; // IClientSecretVerifier
using Azure.Data.Tables;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Azurite;
using Testcontainers.MsSql;

namespace Authagonal.Tests;

/// <summary>
/// End-to-end proof that migrated users can actually authenticate: seeds a Duende-shaped SQL DB with
/// the prod archetypes (bcrypt / ASP.NET-Identity-v3 / null-hash SSO / pipe-id / 64-hex legacy-id /
/// TOTP+recovery / Google login / api_auth SHA512 secret), runs the REAL migration into Table storage,
/// then asserts the login-critical invariants hold against the Authagonal stores.
/// </summary>
public sealed class DuendeMigrationArchetypeFixture : IAsyncLifetime
{
    public MsSqlContainer Sql { get; } = new MsSqlBuilder().Build();
    public AzuriteContainer Azurite { get; } =
        new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Sql.StartAsync(), Azurite.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await Sql.DisposeAsync().AsTask();
        await Azurite.DisposeAsync().AsTask();
    }
}

public class DuendeMigrationArchetypeTests(DuendeMigrationArchetypeFixture fixture)
    : IClassFixture<DuendeMigrationArchetypeFixture>
{
    // Known plaintext credentials for the seeded archetypes.
    private const string BcryptPassword = "Pw-bcrypt-9!";
    private const string V3Password = "Pw-v3-9!";
    private const string ApiAuthSecret = "api-auth-secret-value";

    private const string PipeId = "samlp|acme|joe@acme.com";
    private const string HexId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"; // 64 chars

    private static readonly byte[] TotpSecret = RandomNumberGenerator.GetBytes(20);

    [Fact]
    public async Task Migrates_prod_archetypes_and_login_invariants_hold()
    {
        await SeedDuendeAsync(fixture.Sql.GetConnectionString());

        var stores = BuildStores(fixture.Azurite.GetConnectionString(), out var tableClients);
        var engine = new DuendeMigrationEngine(
            stores, new PlaintextSecretProvider(), new RecoveryCodeService(),
            NullLogger<DuendeMigrationEngine>.Instance);

        var report = await engine.RunAsync(new DuendeMigrationOptions
        {
            Enabled = true,
            DryRun = false,
            UsersMode = UsersMode.CreateOnly,
            Source = new DuendeMigrationOptions.SourceOptions { ConnectionString = fixture.Sql.GetConnectionString() },
        });

        Assert.Empty(report.Errors);
        Assert.Equal(6, report.UsersCreated);
        Assert.Empty(report.InvalidUserIds);

        var hasher = new PasswordHasher();

        // 1. bcrypt archetype — hash migrated verbatim, verifies (and would rehash on login).
        var bcryptUser = await stores.Users.GetAsync("u-bcrypt");
        Assert.NotNull(bcryptUser);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, hasher.VerifyPassword(BcryptPassword, bcryptUser!.PasswordHash!));
        // claim folding
        Assert.Equal("Ada", bcryptUser.FirstName);
        Assert.Equal("Lovelace", bcryptUser.LastName);
        Assert.Equal("Analytical Engines", bcryptUser.CompanyName);
        Assert.Equal("org-42", bcryptUser.OrganizationId);
        Assert.DoesNotContain("email", bcryptUser.CustomAttributes.Keys);
        Assert.Contains("admin", bcryptUser.Roles);

        // 2. ASP.NET Identity v3 archetype.
        var v3User = await stores.Users.GetAsync("u-v3");
        Assert.NotNull(v3User);
        Assert.Equal(PasswordVerifyResult.SuccessRehashNeeded, hasher.VerifyPassword(V3Password, v3User!.PasswordHash!));

        // 3. null-hash external-SSO-only archetype + its Google login.
        var ssoUser = await stores.Users.GetAsync("u-sso");
        Assert.NotNull(ssoUser);
        Assert.Null(ssoUser!.PasswordHash);
        var logins = await stores.Users.GetLoginsAsync("u-sso");
        Assert.Contains(logins, l => l.Provider == "Google");

        // 4. + 5. opaque legacy ids preserved verbatim.
        Assert.NotNull(await stores.Users.GetAsync(PipeId));
        Assert.NotNull(await stores.Users.GetAsync(HexId));

        // 6. TOTP + recovery codes migrated; the TOTP secret round-trips so codes still validate.
        var creds = await stores.Mfa.GetCredentialsAsync("u-totp");
        var totp = Assert.Single(creds, c => c.Type == MfaCredentialType.Totp);
        Assert.Equal("duende-totp", totp.Id);
        Assert.Equal(2, creds.Count(c => c.Type == MfaCredentialType.RecoveryCode));
        // PlaintextSecretProvider stores the base64 secret as-is; decoding it recovers the raw key.
        var recovered = Convert.FromBase64String(totp.SecretProtected!);
        Assert.Equal(TotpSecret, recovered);
        var totpService = new TotpService();
        var code = totpService.GenerateCode(recovered);
        Assert.True(totpService.VerifyCode(recovered, code));

        // 7. api_auth client secret (SHA512, tagged on migration) authenticates.
        var apiClient = await stores.Clients.GetAsync("api_auth");
        Assert.NotNull(apiClient);
        Assert.All(apiClient!.ClientSecretHashes, h => Assert.StartsWith("SHA512$", h));
        var verifier = new PasswordHasherClientSecretVerifier(hasher);
        Assert.True(await verifier.VerifyAsync(apiClient, ApiAuthSecret));
        Assert.False(await verifier.VerifyAsync(apiClient, "wrong-secret"));

        // idempotent re-run writes nothing new.
        var second = await engine.RunAsync(new DuendeMigrationOptions
        {
            Enabled = true,
            UsersMode = UsersMode.CreateOnly,
            Source = new DuendeMigrationOptions.SourceOptions { ConnectionString = fixture.Sql.GetConnectionString() },
        });
        Assert.Equal(0, second.UsersCreated);
        Assert.Equal(6, second.UsersSkipped);

        GC.KeepAlive(tableClients);
    }

    // ---------------------------------------------------------------------------
    // Store wiring (mirrors the CLI StoreFactory / AzureProvider table names).
    // ---------------------------------------------------------------------------
    private static DuendeMigrationStores BuildStores(string connectionString, out object tableClients)
    {
        var serviceClient = new TableServiceClient(connectionString);
        var partitioner = EnvPartitioner.Live;
        var created = new List<TableClient>();

        TableClient T(string name)
        {
            var c = serviceClient.GetTableClient(name);
            c.CreateIfNotExists();
            created.Add(c);
            return c;
        }

        var stores = new DuendeMigrationStores
        {
            Users = new TableUserStore(T("Users"), T("UserEmails"), T("UserLogins"), T("UserExternalIds"),
                T("UserFirstNames"), T("UserLastNames"), partitioner),
            Roles = new TableRoleStore(T("Roles"), partitioner),
            Scopes = new TableScopeStore(T("Scopes"), partitioner),
            Clients = new TableClientStore(T("Clients"), partitioner),
            Mfa = new TableMfaStore(T("MfaCredentials"), T("MfaChallenges"), T("MfaWebAuthnIndex"), partitioner),
            SamlProviders = new TableSamlProviderStore(T("SamlProviders"), partitioner),
            OidcProviders = new TableOidcProviderStore(T("OidcProviders"), partitioner),
            SsoDomains = new TableSsoDomainStore(T("SsoDomains"), partitioner),
            Grants = new TableGrantStore(T("Grants"), T("GrantsBySubject"), T("GrantsByExpiry"),
                partitioner, NullLogger<TableGrantStore>.Instance),
        };
        tableClients = created;
        return stores;
    }

    // ---------------------------------------------------------------------------
    // Seed a minimal Duende-shaped schema + the archetype rows.
    // ---------------------------------------------------------------------------
    private static async Task SeedDuendeAsync(string connectionString)
    {
        var bcryptHash = BCrypt.Net.BCrypt.HashPassword(BcryptPassword);
        var v3Hash = BuildIdentityV3Hash(V3Password);
        var authenticatorKey = TotpService.Base32Encode(TotpSecret);
        var apiAuthSecretHash = Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(ApiAuthSecret)));

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await Exec(conn, """
            CREATE TABLE AspNetUsers (
                Id nvarchar(256) PRIMARY KEY, UserName nvarchar(256), Email nvarchar(256),
                NormalizedEmail nvarchar(256), EmailConfirmed bit, PasswordHash nvarchar(max),
                SecurityStamp nvarchar(max), PhoneNumber nvarchar(64), TwoFactorEnabled bit,
                LockoutEnd datetimeoffset NULL, LockoutEnabled bit, AccessFailedCount int);
            CREATE TABLE AspNetUserClaims (Id int IDENTITY PRIMARY KEY, UserId nvarchar(256), ClaimType nvarchar(256), ClaimValue nvarchar(max));
            CREATE TABLE AspNetUserLogins (LoginProvider nvarchar(128), ProviderKey nvarchar(256), ProviderDisplayName nvarchar(256), UserId nvarchar(256));
            CREATE TABLE AspNetRoles (Id nvarchar(256) PRIMARY KEY, Name nvarchar(256));
            CREATE TABLE AspNetUserRoles (UserId nvarchar(256), RoleId nvarchar(256));
            CREATE TABLE AspNetUserTokens (UserId nvarchar(256), LoginProvider nvarchar(128), Name nvarchar(128), Value nvarchar(max));
            CREATE TABLE Clients (
                Id int IDENTITY PRIMARY KEY, ClientId nvarchar(256), ClientName nvarchar(256), Description nvarchar(max),
                ClientUri nvarchar(max), LogoUri nvarchar(max), Enabled bit, RequirePkce bit, RequireClientSecret bit,
                AllowOfflineAccess bit, AlwaysIncludeUserClaimsInIdToken bit, RequireConsent bit,
                AccessTokenLifetime int, IdentityTokenLifetime int, AuthorizationCodeLifetime int,
                AbsoluteRefreshTokenLifetime int, SlidingRefreshTokenLifetime int, RefreshTokenUsage int,
                RefreshTokenExpiration int, FrontChannelLogoutUri nvarchar(max), FrontChannelLogoutSessionRequired bit,
                BackChannelLogoutUri nvarchar(max), BackChannelLogoutSessionRequired bit, DeviceCodeLifetime int);
            CREATE TABLE ClientSecrets (Id int IDENTITY PRIMARY KEY, ClientId int, Value nvarchar(max), Expiration datetime2 NULL);
            CREATE TABLE ClientGrantTypes (Id int IDENTITY PRIMARY KEY, ClientId int, GrantType nvarchar(256));
            """);

        // Users
        await AddUser(conn, "u-bcrypt", "bcrypt@test.com", bcryptHash, twoFactor: false);
        await AddUser(conn, "u-v3", "v3@test.com", v3Hash, twoFactor: false);
        await AddUser(conn, "u-sso", "sso@test.com", null, twoFactor: false);
        await AddUser(conn, PipeId, "pipe@acme.com", bcryptHash, twoFactor: false);
        await AddUser(conn, HexId, "hex@test.com", bcryptHash, twoFactor: false);
        await AddUser(conn, "u-totp", "totp@test.com", bcryptHash, twoFactor: true);

        // Claims for the bcrypt user (folded + one email claim that must be dropped).
        await Exec(conn, """
            INSERT INTO AspNetUserClaims (UserId, ClaimType, ClaimValue) VALUES
              ('u-bcrypt','given_name','Ada'),
              ('u-bcrypt','family_name','Lovelace'),
              ('u-bcrypt','company','Analytical Engines'),
              ('u-bcrypt','org_id','org-42'),
              ('u-bcrypt','email','bcrypt@test.com');
            """);

        // Google external login for the SSO user.
        await Exec(conn, "INSERT INTO AspNetUserLogins (LoginProvider, ProviderKey, ProviderDisplayName, UserId) VALUES ('Google','google-123','Google','u-sso');");

        // Role + membership.
        await Exec(conn, "INSERT INTO AspNetRoles (Id, Name) VALUES ('r1','admin'); INSERT INTO AspNetUserRoles (UserId, RoleId) VALUES ('u-bcrypt','r1');");

        // MFA tokens for the TOTP user.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO AspNetUserTokens (UserId, LoginProvider, Name, Value) VALUES
                  ('u-totp','[AspNetUserStore]','AuthenticatorKey', @key),
                  ('u-totp','[AspNetUserStore]','RecoveryCodes', 'ABCD-2345;WXYZ-6789');
                """;
            cmd.Parameters.AddWithValue("@key", authenticatorKey);
            await cmd.ExecuteNonQueryAsync();
        }

        // api_auth client with a SHA512 secret + a grant type.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO Clients (ClientId, ClientName, Enabled, RequirePkce, RequireClientSecret, AllowOfflineAccess,
                    AlwaysIncludeUserClaimsInIdToken, RequireConsent, AccessTokenLifetime, IdentityTokenLifetime,
                    AuthorizationCodeLifetime, AbsoluteRefreshTokenLifetime, SlidingRefreshTokenLifetime,
                    RefreshTokenUsage, RefreshTokenExpiration, FrontChannelLogoutSessionRequired,
                    BackChannelLogoutSessionRequired, DeviceCodeLifetime)
                VALUES ('api_auth','API Auth',1,0,1,0,0,0,3600,300,300,2592000,1296000,1,1,1,1,300);
                INSERT INTO ClientSecrets (ClientId, Value, Expiration)
                    VALUES ((SELECT Id FROM Clients WHERE ClientId='api_auth'), @secret, NULL);
                INSERT INTO ClientGrantTypes (ClientId, GrantType)
                    VALUES ((SELECT Id FROM Clients WHERE ClientId='api_auth'), 'client_credentials');
                """;
            cmd.Parameters.AddWithValue("@secret", apiAuthSecretHash);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task AddUser(SqlConnection conn, string id, string email, string? passwordHash, bool twoFactor)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AspNetUsers (Id, UserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash, SecurityStamp,
                PhoneNumber, TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount)
            VALUES (@id, @email, @email, @nemail, 1, @hash, @stamp, NULL, @tf, NULL, 1, 0);
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@email", email);
        cmd.Parameters.AddWithValue("@nemail", email.ToUpperInvariant());
        cmd.Parameters.AddWithValue("@hash", (object?)passwordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stamp", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@tf", twoFactor);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Exec(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Builds an ASP.NET Identity v3 password hash (marker 0x01, PRF=SHA256) for a known password.</summary>
    private static string BuildIdentityV3Hash(string password, int iterations = 100_000)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        var output = new byte[13 + salt.Length + subkey.Length];
        output[0] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(1), 1); // PRF = HMAC-SHA256
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(5), (uint)iterations);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(9), (uint)salt.Length);
        salt.CopyTo(output.AsSpan(13));
        subkey.CopyTo(output.AsSpan(13 + salt.Length));
        return Convert.ToBase64String(output);
    }
}
