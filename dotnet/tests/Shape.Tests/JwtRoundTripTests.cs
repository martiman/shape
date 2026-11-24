using Shape.Auth;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class JwtRoundTripTests
{
    private static AuthConfig Config(string secret = "test-secret-must-be-long-enough") => new()
    {
        Driver = "jwt",
        JwtSecret = secret,
        JwtIssuer = "shape-tests",
        JwtAudience = "shape-tests-clients"
    };

    [Fact]
    public void Issue_then_authorize_round_trips_claims()
    {
        var config = Config();
        var token = new JwtIssuer(config).Issue(new() { ["sub"] = "user-1", ["role"] = "admin" }, 3600);

        var guard = new JwtGuard(config);
        var claims = guard.Authorize(new Dictionary<string, string>
        {
            ["authorization"] = $"Bearer {token}"
        });

        Assert.Equal("user-1", claims["sub"]);
        Assert.Equal("admin", claims["role"]);
        Assert.Equal("shape-tests", claims["iss"]);
        Assert.Equal("shape-tests-clients", claims["aud"]);
    }

    [Fact]
    public void Authorize_rejects_missing_bearer()
    {
        var ex = Assert.Throws<ApiException>(() =>
            new JwtGuard(Config()).Authorize(new Dictionary<string, string>()));

        Assert.Equal("AUTH_REQUIRED", ex.ApiCode);
    }

    [Fact]
    public void Authorize_rejects_tampered_signature()
    {
        var token = new JwtIssuer(Config()).Issue(new() { ["sub"] = "u" }, 3600);
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.AAAA{parts[2][4..]}";

        var ex = Assert.Throws<ApiException>(() =>
            new JwtGuard(Config()).Authorize(new Dictionary<string, string>
            {
                ["authorization"] = $"Bearer {tampered}"
            }));

        Assert.Equal("AUTH_INVALID", ex.ApiCode);
    }

    [Fact]
    public void Authorize_rejects_wrong_issuer()
    {
        var issuerConfig = Config();
        var token = new JwtIssuer(issuerConfig).Issue(new() { ["sub"] = "u" }, 3600);

        var verifierConfig = Config();
        verifierConfig.JwtIssuer = "different-issuer";

        var ex = Assert.Throws<ApiException>(() =>
            new JwtGuard(verifierConfig).Authorize(new Dictionary<string, string>
            {
                ["authorization"] = $"Bearer {token}"
            }));

        Assert.Equal("AUTH_INVALID", ex.ApiCode);
    }

    [Fact]
    public void NullGuard_returns_empty_context()
    {
        var guard = new NullGuard();
        var claims = guard.Authorize(new Dictionary<string, string>());

        Assert.Empty(claims);
    }
}
