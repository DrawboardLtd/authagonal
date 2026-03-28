using Authagonal.Core.Models;
using Authagonal.Server;
using Authagonal.Server.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Authagonal.Tests;

public class PasswordValidatorTests
{
    private static readonly PasswordPolicy DefaultPolicy = new();
    private static readonly PasswordValidator Validator = CreateValidator();

    private static PasswordValidator CreateValidator()
    {
        var options = Options.Create(new LocalizationOptions());
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        var localizer = new StringLocalizer<SharedMessages>(factory);
        return new PasswordValidator(localizer);
    }

    [Fact]
    public void Validate_ValidPassword_Succeeds()
    {
        var (isValid, error) = Validator.Validate("Str0ng!Pass", DefaultPolicy);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_NullOrWhitespace_Fails(string? password)
    {
        var (isValid, error) = Validator.Validate(password!, DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("required", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_TooShort_Fails()
    {
        var (isValid, error) = Validator.Validate("Ab1!xyz", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("8", error!);
    }

    [Fact]
    public void Validate_NoUppercase_Fails()
    {
        var (isValid, error) = Validator.Validate("lowercase1!", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("uppercase", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NoLowercase_Fails()
    {
        var (isValid, error) = Validator.Validate("UPPERCASE1!", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("lowercase", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NoDigit_Fails()
    {
        var (isValid, error) = Validator.Validate("NoDigits!!", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("digit", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NoSpecialChar_Fails()
    {
        var (isValid, error) = Validator.Validate("NoSpecial1A", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("non-alphanumeric", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_InsufficientUniqueChars_Fails()
    {
        var (isValid, _) = Validator.Validate("!!!!!!!!", DefaultPolicy); // only 1 unique char
        Assert.False(isValid);
    }

    [Fact]
    public void Validate_ExactlyMinLength_Succeeds()
    {
        var (isValid, _) = Validator.Validate("Abcde1!x", DefaultPolicy); // exactly 8 chars
        Assert.True(isValid);
    }

    [Fact]
    public void Validate_CustomMinLength_Respected()
    {
        var policy = new PasswordPolicy { MinLength = 12 };
        var (isValid, error) = Validator.Validate("Ab1!xyzabc", policy); // 10 chars, needs 12
        Assert.False(isValid);
        Assert.Contains("12", error!);
    }

    [Fact]
    public void Validate_DisabledUppercase_Allows()
    {
        var policy = new PasswordPolicy { RequireUppercase = false };
        var (isValid, _) = Validator.Validate("alllower1!", policy);
        Assert.True(isValid);
    }

    [Fact]
    public void Validate_DisabledSpecialChar_Allows()
    {
        var policy = new PasswordPolicy { RequireSpecialChar = false };
        var (isValid, _) = Validator.Validate("NoSpecial1A", policy);
        Assert.True(isValid);
    }
}
