using Authagonal.Server.Services;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Hashers built at a deliberately trivial work factor.
/// </summary>
/// <remarks>
/// The production default is OWASP's 600,000 PBKDF2 iterations, which is the point — but a unit test
/// that mints ten recovery codes then spends three seconds in the KDF is measuring the KDF, not the
/// behaviour under test. The floor lives at configuration binding rather than in the hasher so this
/// is possible; tests that actually assert on cost construct their own.
/// </remarks>
internal static class CheapHasher
{
    public const int Iterations = 1_000;

    public static PasswordHasher Password() =>
        new(Options.Create(new AuthOptions { Pbkdf2Iterations = Iterations }));

    public static RecoveryCodeService RecoveryCodes() => new(Password());
}
