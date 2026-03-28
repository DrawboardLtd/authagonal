using Authagonal.Server.Services;

namespace Authagonal.Tests;

public class PasswordValidatorTests
{
    [Fact]
    public void Validate_ValidPassword_Succeeds()
    {
        var (isValid, error) = PasswordValidator.Validate("Str0ng!Pass");
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_NullOrWhitespace_Fails(string? password)
    {
        var (isValid, error) = PasswordValidator.Validate(password!);
        Assert.False(isValid);
        Assert.Contains("required", error!);
    }

    [Fact]
    public void Validate_TooShort_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("Ab1!xyz");
        Assert.False(isValid);
        Assert.Contains("8 characters", error!);
    }

    [Fact]
    public void Validate_NoUppercase_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("lowercase1!");
        Assert.False(isValid);
        Assert.Contains("uppercase", error!);
    }

    [Fact]
    public void Validate_NoLowercase_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("UPPERCASE1!");
        Assert.False(isValid);
        Assert.Contains("lowercase", error!);
    }

    [Fact]
    public void Validate_NoDigit_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("NoDigits!!");
        Assert.False(isValid);
        Assert.Contains("digit", error!);
    }

    [Fact]
    public void Validate_NoSpecialChar_Fails()
    {
        var (isValid, error) = PasswordValidator.Validate("NoSpecial1A");
        Assert.False(isValid);
        Assert.Contains("non-alphanumeric", error!);
    }

    [Fact]
    public void Validate_InsufficientUniqueChars_Fails()
    {
        var (isValid, _) = PasswordValidator.Validate("!!!!!!!!"); // only 1 unique char
        Assert.False(isValid);
    }

    [Fact]
    public void Validate_ExactlyMinLength_Succeeds()
    {
        var (isValid, _) = PasswordValidator.Validate("Abcde1!x"); // exactly 8 chars
        Assert.True(isValid);
    }
}
