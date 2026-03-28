namespace Authagonal.Core.Models;

public sealed class PasswordPolicy
{
    public int MinLength { get; set; } = 8;
    public int MinUniqueChars { get; set; } = 2;
    public bool RequireUppercase { get; set; } = true;
    public bool RequireLowercase { get; set; } = true;
    public bool RequireDigit { get; set; } = true;
    public bool RequireSpecialChar { get; set; } = true;
}
