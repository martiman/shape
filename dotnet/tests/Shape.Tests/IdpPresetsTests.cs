using Shape.Auth;
using Xunit;

namespace Shape.Tests;

public sealed class IdpPresetsTests
{
    [Fact]
    public void Entra_preset_fills_jwks_uri_issuer_and_claim_names()
    {
        var config = IdpPresets.Apply(new OidcConfig
        {
            Preset = "entra",
            TenantId = "00000000-0000-0000-0000-000000000abc"
        });

        Assert.Equal("https://login.microsoftonline.com/00000000-0000-0000-0000-000000000abc/discovery/v2.0/keys", config.JwksUri);
        Assert.Equal("https://login.microsoftonline.com/00000000-0000-0000-0000-000000000abc/v2.0", config.Issuer);
        Assert.Equal("oid", config.IdClaim);
        Assert.Equal("preferred_username", config.EmailClaim);
        Assert.Equal("roles", config.RoleClaim);
    }

    [Fact]
    public void Azure_alias_resolves_same_as_entra()
    {
        var entra = IdpPresets.Apply(new OidcConfig { Preset = "entra", TenantId = "T" });
        var azure = IdpPresets.Apply(new OidcConfig { Preset = "azure", TenantId = "T" });
        var azuread = IdpPresets.Apply(new OidcConfig { Preset = "azuread", TenantId = "T" });

        Assert.Equal(entra.JwksUri, azure.JwksUri);
        Assert.Equal(entra.JwksUri, azuread.JwksUri);
    }

    [Fact]
    public void Auth0_preset_fills_jwks_and_issuer_keeps_default_claims()
    {
        var config = IdpPresets.Apply(new OidcConfig { Preset = "auth0", Domain = "tenant.auth0.com" });

        Assert.Equal("https://tenant.auth0.com/.well-known/jwks.json", config.JwksUri);
        Assert.Equal("https://tenant.auth0.com/", config.Issuer);
        Assert.Equal("sub", config.IdClaim);
        Assert.Equal("email", config.EmailClaim);
    }

    [Fact]
    public void Okta_preset_fills_groups_role_claim()
    {
        var config = IdpPresets.Apply(new OidcConfig { Preset = "okta", Domain = "company.okta.com" });

        Assert.Equal("https://company.okta.com/oauth2/default/v1/keys", config.JwksUri);
        Assert.Equal("groups", config.RoleClaim);
    }

    [Fact]
    public void Google_preset_uses_static_endpoints()
    {
        var config = IdpPresets.Apply(new OidcConfig { Preset = "google" });

        Assert.Equal("https://www.googleapis.com/oauth2/v3/certs", config.JwksUri);
        Assert.Equal("https://accounts.google.com", config.Issuer);
    }

    [Fact]
    public void Cognito_preset_uses_region_and_pool()
    {
        var config = IdpPresets.Apply(new OidcConfig
        {
            Preset = "cognito",
            Region = "eu-west-1",
            UserPoolId = "eu-west-1_AbCdEfGhI"
        });

        Assert.Equal(
            "https://cognito-idp.eu-west-1.amazonaws.com/eu-west-1_AbCdEfGhI/.well-known/jwks.json",
            config.JwksUri);
        Assert.Equal("cognito:groups", config.RoleClaim);
    }

    [Fact]
    public void Operator_supplied_jwks_uri_wins_over_preset_default()
    {
        var config = IdpPresets.Apply(new OidcConfig
        {
            Preset = "google",
            JwksUri = "https://my-proxy.example.com/jwks"
        });

        Assert.Equal("https://my-proxy.example.com/jwks", config.JwksUri);
    }

    [Fact]
    public void Operator_supplied_id_claim_wins_over_preset_override()
    {
        var config = IdpPresets.Apply(new OidcConfig
        {
            Preset = "entra",
            TenantId = "T",
            IdClaim = "custom_id"
        });

        Assert.Equal("custom_id", config.IdClaim);
    }

    [Fact]
    public void Empty_preset_is_a_noop()
    {
        var config = IdpPresets.Apply(new OidcConfig());
        Assert.Null(config.JwksUri);
        Assert.Null(config.Issuer);
    }

    [Fact]
    public void Unknown_preset_is_a_noop()
    {
        var config = IdpPresets.Apply(new OidcConfig { Preset = "facebook" });
        Assert.Null(config.JwksUri);
    }
}
