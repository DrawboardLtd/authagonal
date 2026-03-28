using Authagonal.Core.Models;

namespace Authagonal.Server.Services;

public static class PasswordValidator
{
    public static (bool IsValid, string? Error) Validate(string password, PasswordPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(password))
            return (false, "Password is required.");

        if (password.Length < policy.MinLength)
            return (false, $"Password must be at least {policy.MinLength} characters.");

        if (policy.RequireUppercase && !password.Any(char.IsUpper))
            return (false, "Password must contain at least one uppercase letter.");

        if (policy.RequireLowercase && !password.Any(char.IsLower))
            return (false, "Password must contain at least one lowercase letter.");

        if (policy.RequireDigit && !password.Any(char.IsDigit))
            return (false, "Password must contain at least one digit.");

        if (policy.RequireSpecialChar && !password.Any(c => !char.IsLetterOrDigit(c)))
            return (false, "Password must contain at least one non-alphanumeric character.");

        if (password.Distinct().Count() < policy.MinUniqueChars)
            return (false, $"Password must contain at least {policy.MinUniqueChars} unique characters.");

        return (true, null);
    }
}
