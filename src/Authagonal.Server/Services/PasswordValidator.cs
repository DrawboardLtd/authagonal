namespace Authagonal.Server.Services;

public static class PasswordValidator
{
    private const int MinLength = 8;
    private const int MinUniqueChars = 2;

    public static (bool IsValid, string? Error) Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return (false, "Password is required.");

        if (password.Length < MinLength)
            return (false, $"Password must be at least {MinLength} characters.");

        if (!password.Any(char.IsUpper))
            return (false, "Password must contain at least one uppercase letter.");

        if (!password.Any(char.IsLower))
            return (false, "Password must contain at least one lowercase letter.");

        if (!password.Any(char.IsDigit))
            return (false, "Password must contain at least one digit.");

        if (!password.Any(c => !char.IsLetterOrDigit(c)))
            return (false, "Password must contain at least one non-alphanumeric character.");

        if (password.Distinct().Count() < MinUniqueChars)
            return (false, $"Password must contain at least {MinUniqueChars} unique characters.");

        return (true, null);
    }
}
