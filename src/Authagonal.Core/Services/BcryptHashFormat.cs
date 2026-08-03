namespace Authagonal.Core.Services;

/// <summary>
/// What a bcrypt hash this server is willing to verify looks like — one answer, for every verifier.
/// </summary>
/// <remarks>
/// It lives in Core because there are two bcrypt verifiers and they cannot see each other:
/// <c>BCryptClientSecretVerifier</c> is the Protocol package's default (embedded without
/// <c>Authagonal.Server</c>), and <c>PasswordHasher</c> is the Server host's. Both had the identical
/// defect, and a fix in one would have left the other — which is the pattern this whole review keeps finding.
/// <para>
/// Two failure modes reached a stored record because recognition was a four-character prefix test:
/// </para>
/// <list type="bullet">
/// <item><b>Unbounded cost.</b> A stored hash is an instruction about how much CPU to spend on the next
/// ANONYMOUS <c>/connect/token</c> call for that client. Cost is a base-2 exponent and BCrypt.Net-Next accepts
/// up to 31, so <c>$2a$31$…</c> is 2^31 key expansions — an uncancellable multi-day burn pinning one
/// thread-pool thread per request. A few dozen requests take the provider down for every tenant, from a caller
/// who only needs to know a client_id. Every other imported-cost lever was already bounded.</item>
/// <item><b>Malformed body.</b> Against the pinned library, <c>$2a$1</c> and <c>$2b$</c> throw
/// <c>IndexOutOfRangeException</c>, <c>$2a$xy$…</c> throws <c>FormatException</c>, and <c>$2a$11$short</c>
/// throws <c>ArgumentOutOfRangeException</c>. Neither verifier caught anything but <c>SaltParseException</c>, so
/// each became an unhandled 500 on every authentication for that principal — permanently, and with a stack
/// trace to an anonymous caller wherever developer exception pages are on.</item>
/// </list>
/// </remarks>
public static class BcryptHashFormat
{
    /// <summary>The prefixes the library emits and accepts.</summary>
    public static readonly string[] Prefixes = ["$2a$", "$2b$", "$2x$", "$2y$"];

    /// <summary>Lowest cost factor the algorithm defines.</summary>
    public const int MinCost = 4;

    /// <summary>
    /// Highest cost factor this server will verify. Each step doubles the work; 15 is around a second on
    /// current hardware, which is already far beyond what a login path should spend.
    /// </summary>
    public const int MaxCost = 15;

    /// <summary>Total length of a bcrypt hash: <c>$2a$NN$</c> + 22 salt chars + 31 digest chars.</summary>
    private const int HashLength = 60;

    /// <summary>True when <paramref name="hash"/> carries a bcrypt prefix, whatever else is wrong with it.</summary>
    /// <remarks>
    /// Used to decide which verifier a stored blob belongs to. Kept separate from <see cref="IsValid"/> so that
    /// a malformed bcrypt hash is recognised as bcrypt's problem and refused, rather than falling through to
    /// the unprefixed ASP.NET Identity path where the cost parameters come from the blob itself.
    /// </remarks>
    public static bool HasBcryptPrefix(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;

        foreach (var prefix in Prefixes)
            if (hash.StartsWith(prefix, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>
    /// True when <paramref name="hash"/> is a structurally valid bcrypt hash whose cost is within
    /// <see cref="MaxCost"/>.
    /// </summary>
    public static bool IsValid(string? hash)
    {
        if (!HasBcryptPrefix(hash)) return false;
        if (hash!.Length != HashLength) return false;
        if (hash[6] != '$') return false;
        if (!char.IsAsciiDigit(hash[4]) || !char.IsAsciiDigit(hash[5])) return false;

        var cost = (hash[4] - '0') * 10 + (hash[5] - '0');
        return cost >= MinCost && cost <= MaxCost;
    }
}
