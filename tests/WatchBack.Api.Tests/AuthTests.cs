using FluentAssertions;

using WatchBack.Api.Endpoints;

using Xunit;

namespace WatchBack.Api.Tests;

public class AuthTests
{
    [Fact]
    public void GeneratePassword_IsAlways24Characters()
    {
        var password = AuthEndpoints.GeneratePassword();
        password.Length.Should().Be(24);
    }

    [Fact]
    public void GeneratePassword_ContainsUppercaseLetter()
    {
        var password = AuthEndpoints.GeneratePassword();
        password.Any(char.IsUpper).Should().BeTrue();
    }

    [Fact]
    public void GeneratePassword_ContainsLowercaseLetter()
    {
        var password = AuthEndpoints.GeneratePassword();
        password.Any(char.IsLower).Should().BeTrue();
    }

    [Fact]
    public void GeneratePassword_ContainsDigit()
    {
        var password = AuthEndpoints.GeneratePassword();
        password.Any(char.IsDigit).Should().BeTrue();
    }

    [Fact]
    public void GeneratePassword_ContainsSpecialCharacter()
    {
        const string special = "!@#$%^&*-_+=";
        var password = AuthEndpoints.GeneratePassword();
        password.Any(c => special.Contains(c)).Should().BeTrue();
    }

    [Fact]
    public void GeneratePassword_OnlyUsesPermittedCharacters()
    {
        const string permitted = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*-_+=";
        for (var i = 0; i < 20; i++)
        {
            var password = AuthEndpoints.GeneratePassword();
            password.ToCharArray().Should().OnlyContain(c => permitted.Contains(c));
        }
    }

    [Fact]
    public void GeneratePassword_ProducesDifferentPasswordsEachCall()
    {
        var passwords = Enumerable.Range(0, 20).Select(_ => AuthEndpoints.GeneratePassword()).ToList();
        passwords.Distinct().Count().Should().BeGreaterThan(1);
    }

}
