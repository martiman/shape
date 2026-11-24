using Shape.Auth;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class LocalLoginDisabledTests
{
    private static LoginService Build(AuthConfig auth)
    {
        var issuer = new JwtIssuer(auth);
        return new LoginService("Server=unused;Database=x;User=u;Password=p", auth, issuer);
    }

    [Fact]
    public void Login_throws_AUTH_NOT_CONFIGURED_when_LocalLoginDisabled()
    {
        var svc = Build(new AuthConfig
        {
            Driver = "jwt",
            JwtSecret = "x",
            LocalLoginDisabled = true
        });

        var ex = Assert.Throws<ApiException>(() => svc.Login(
            new Dictionary<string, object?> { ["email"] = "a@b.c", ["password"] = "12345678" }));

        Assert.Equal("AUTH_NOT_CONFIGURED", ex.ApiCode);
        Assert.NotNull(ex.Details);
    }

    [Fact]
    public void ChangePassword_throws_AUTH_NOT_CONFIGURED_when_LocalLoginDisabled()
    {
        var svc = Build(new AuthConfig
        {
            Driver = "jwt",
            JwtSecret = "x",
            LocalLoginDisabled = true
        });

        var ex = Assert.Throws<ApiException>(() => svc.ChangePassword(
            new Dictionary<string, object?> { ["email"] = "a@b.c", ["new_password"] = "12345678" }));

        Assert.Equal("AUTH_NOT_CONFIGURED", ex.ApiCode);
    }

    [Fact]
    public void Hint_mentions_OIDC_when_OIDC_is_configured()
    {
        var svc = Build(new AuthConfig
        {
            Driver = "jwt",
            JwtSecret = "x",
            LocalLoginDisabled = true,
            Oidc = new OidcConfig { JwksUri = "https://idp.example/jwks" }
        });

        var ex = Assert.Throws<ApiException>(() => svc.Login(
            new Dictionary<string, object?> { ["email"] = "a@b.c", ["password"] = "12345678" }));

        Assert.Contains("OIDC", (string)ex.Details!["hint"]!);
    }

    [Fact]
    public void Hint_warns_when_LocalLoginDisabled_but_no_OIDC_configured()
    {
        var svc = Build(new AuthConfig
        {
            Driver = "jwt",
            JwtSecret = "x",
            LocalLoginDisabled = true
        });

        var ex = Assert.Throws<ApiException>(() => svc.Login(
            new Dictionary<string, object?> { ["email"] = "a@b.c", ["password"] = "12345678" }));

        Assert.Contains("no AuthConfig.Oidc is configured", (string)ex.Details!["hint"]!);
    }
}
