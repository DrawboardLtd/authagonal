using Authagonal.Core.Models;
using Microsoft.Extensions.Localization;

namespace Authagonal.Server.Services;

public class PasswordValidator(IStringLocalizer<SharedMessages> localizer)
{
    public (bool IsValid, string? Error) Validate(string password, PasswordPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(password))
            return (false, localizer["Password_Required"].Value);

        if (password.Length < policy.MinLength)
            return (false, string.Format(localizer["Password_MinLength"].Value, policy.MinLength));

        if (policy.RequireUppercase && !password.Any(char.IsUpper))
            return (false, localizer["Password_RequireUppercase"].Value);

        if (policy.RequireLowercase && !password.Any(char.IsLower))
            return (false, localizer["Password_RequireLowercase"].Value);

        if (policy.RequireDigit && !password.Any(char.IsDigit))
            return (false, localizer["Password_RequireDigit"].Value);

        if (policy.RequireSpecialChar && !password.Any(c => !char.IsLetterOrDigit(c)))
            return (false, localizer["Password_RequireSpecialChar"].Value);

        if (password.Distinct().Count() < policy.MinUniqueChars)
            return (false, string.Format(localizer["Password_MinUniqueChars"].Value, policy.MinUniqueChars));

        return (true, null);
    }
}
