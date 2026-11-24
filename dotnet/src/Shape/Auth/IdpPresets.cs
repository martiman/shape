namespace Shape.Auth;

/// <summary>
/// Expand an <see cref="OidcConfig.Preset"/> name (entra/auth0/okta/google/cognito)
/// into the boilerplate fields every operator types verbatim. User-supplied values
/// always win — the preset only fills <em>missing</em> fields.
///
/// Required preset inputs:
/// <list type="bullet">
///   <item><c>entra</c>/<c>azure</c>/<c>azuread</c>: <see cref="OidcConfig.TenantId"/></item>
///   <item><c>cognito</c>: <see cref="OidcConfig.Region"/> + <see cref="OidcConfig.UserPoolId"/></item>
///   <item><c>auth0</c>: <see cref="OidcConfig.Domain"/></item>
///   <item><c>okta</c>: <see cref="OidcConfig.Domain"/></item>
///   <item><c>google</c>: none — issuer/jwks_uri are static</item>
/// </list>
/// </summary>
public static class IdpPresets
{
    /// <summary>
    /// Returns a copy of <paramref name="config"/> with preset defaults applied
    /// to fields the caller didn't set. Unknown preset names are a no-op so the
    /// rest of the OIDC config still loads as-is.
    /// </summary>
    public static OidcConfig Apply(OidcConfig config)
    {
        if (string.IsNullOrEmpty(config.Preset))
            return config;

        var preset = config.Preset.Trim().ToLowerInvariant();

        switch (preset)
        {
            case "entra":
            case "azure":
            case "azuread":
            {
                var tenant = config.TenantId ?? "<tenant_id>";
                config.JwksUri    ??= $"https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys";
                config.Issuer     ??= $"https://login.microsoftonline.com/{tenant}/v2.0";
                config.Algorithms ??= new() { "RS256" };
                FillIfDefaultedToSub(config, "oid");
                FillIfDefaultedToEmail(config, "preferred_username");
                FillIfDefaultedToRole(config, "roles");
                break;
            }
            case "auth0":
            {
                var domain = config.Domain ?? "<domain>";
                config.JwksUri    ??= $"https://{domain}/.well-known/jwks.json";
                config.Issuer     ??= $"https://{domain}/";
                config.Algorithms ??= new() { "RS256" };
                // Auth0 typically namespaces role claims; the operator must
                // override RoleClaim to match their tenant.
                break;
            }
            case "okta":
            {
                var domain = config.Domain ?? "<domain>";
                config.JwksUri    ??= $"https://{domain}/oauth2/default/v1/keys";
                config.Issuer     ??= $"https://{domain}/oauth2/default";
                config.Algorithms ??= new() { "RS256" };
                FillIfDefaultedToRole(config, "groups");
                break;
            }
            case "google":
            {
                config.JwksUri    ??= "https://www.googleapis.com/oauth2/v3/certs";
                config.Issuer     ??= "https://accounts.google.com";
                config.Algorithms ??= new() { "RS256" };
                break;
            }
            case "cognito":
            {
                var region = config.Region ?? "<region>";
                var pool = config.UserPoolId ?? "<user_pool_id>";
                config.JwksUri    ??= $"https://cognito-idp.{region}.amazonaws.com/{pool}/.well-known/jwks.json";
                config.Issuer     ??= $"https://cognito-idp.{region}.amazonaws.com/{pool}";
                config.Algorithms ??= new() { "RS256" };
                FillIfDefaultedToRole(config, "cognito:groups");
                break;
            }
        }

        return config;
    }

    // Default values on OidcConfig are "sub" / "email" / "role". Only override
    // if the field is still at the default — otherwise the operator picked
    // something specific and the preset must respect that.

    private static void FillIfDefaultedToSub(OidcConfig config, string newClaim)
    {
        if (config.IdClaim == "sub")
            config.IdClaim = newClaim;
    }

    private static void FillIfDefaultedToEmail(OidcConfig config, string newClaim)
    {
        if (config.EmailClaim == "email")
            config.EmailClaim = newClaim;
    }

    private static void FillIfDefaultedToRole(OidcConfig config, string newClaim)
    {
        if (config.RoleClaim == "role")
            config.RoleClaim = newClaim;
    }
}
