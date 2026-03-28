using Authagonal.Core.Models;
using Authagonal.Server.Services;

namespace Authagonal.Tests;

public class PasswordValidatorTests
{
    private static readonly PasswordPolicy DefaultPolicy = new();

    [Fact]
    public void Validate_ValidPassword_Succeeds()
    {
        var (isValid, error) = PasswordValidator.Validate("Str0ng!Pass", DefaultPolicy);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_NullOrWhitespace_Fails(string? password)
    {
        var (isValid, error) = PasswordValidator.Validate(password!, DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("required", error!);
    }

    [Fact]
    public void Validate_TooShort_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("Ab1!xyz", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("8 characters", error!);
    }

    [Fact]
    public void Validate_NoUppercase_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("lowercase1!", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("uppercase", error!);
    }

    [Fact]
    public void Validate_NoLowercase_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("UPPERCASE1!", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("lowercase", error!);
    }

    [Fact]
    public void Validate_NoDigit_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("NoDigits!!", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("digit", error!);
    }

    [Fact]
    public void Validate_NoSpecialChar_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("NoSpecial1A", DefaultPolicy);
        Assert.False(isValid);
        Assert.Contains("non-alphanumeric", error!);
    }

    [Fact]
    public void Validate_InsufficientUniqueChars_Fails()
    {
        var (isValid, _) = PasswordValidator.Validate("!!!!!!!!", DefaultPolicy); // only 1 unique char
        Assert.False(isValid);
    }

    [Fact]
    public void Validate_ExactlyMinLength_Succeeds()
    {
        var (isValid, _) = PasswordValidator.Validate("Abcde1!x", DefaultPolicy); // exactly 8 chars
        Assert.True(isValid);
    }

    [Fact]
    public void Validate_CustomMinLength_Respected()
    {
        var policy = new PasswordPolicy { MinLength = 12 };
        var (isValid, error) = PasswordValidator.Validate("Ab1!xyzabc", policy); // 10 chars, needs 12
        Assert.False(isValid);
        Assert.Contains("12 characters", error!);
    }

    [Fact]
    public void Validate_DisabledUppercase_Allows()
    {
        var policy = new PasswordPolicy { RequireUppercase = false };
        var (isValid, _) = PasswordValidator.Validate("alllower1!", policy);
        Assert.True(isValid);
    }

    [Fact]
    public void Validate_DisabledSpecialChar_Allows()
    {
        var policy = new PasswordPolicy { RequireSpecialChar = false };
        var (isValid, _) = PasswordValidator.Validate("NoSpecial1A", policy);
        Assert.True(isValid);
    }
}
