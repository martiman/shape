using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Shape.Core;
using Shape.Metadata;

namespace Shape.Auth;

/// <summary>
/// Verifies RS256 / RS384 / RS512 JWTs against an OIDC provider's JWKS endpoint
/// (Entra ID / Azure AD, Auth0, Google, Okta, Cognito). Used as the secondary
/// verification path inside <see cref="JwtGuard"/>: when an inbound token's
/// <c>alg</c> matches one of the configured RSA algorithms, JwtGuard delegates
/// here. The HS256 path (Shape's local <see cref="LoginService"/>) is unchanged.
///
/// Security model:
/// <list type="bullet">
///   <item>Algorithm whitelist is config-driven; <c>alg: none</c> and unexpected
///   algorithms are rejected before any signature work.</item>
///   <item>Issuer (<c>iss</c>) and audience (<c>aud</c>) are always validated when configured.</item>
///   <item>JWKS is cached to disk with a TTL; a failed fetch falls back to the stale
///   cache so a momentary IdP outage doesn't take the API down.</item>
/// </list>
///
/// Unlike PHP's hand-rolled DER encoder, the .NET version uses <see cref="RSA.ImportParameters"/>
/// + <see cref="RSA.VerifyData(byte[],byte[],HashAlgorithmName,RSASignaturePadding)"/> directly.
/// </summary>
public sealed class OidcVerifier
{
    private static readonly Dictionary<string, HashAlgorithmName> SupportedAlgorithms = new()
    {
        ["RS256"] = HashAlgorithmName.SHA256,
        ["RS384"] = HashAlgorithmName.SHA384,
        ["RS512"] = HashAlgorithmName.SHA512
    };

    // One static HttpClient per process: DNS pinning is no longer the issue it
    // was on .NET Framework, and we want the connection-pool reuse.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private readonly OidcConfig _config;
    private readonly Database? _database;
    private readonly Dictionary<string, string>? _pkAssignment;
    private readonly TenancyConfig? _tenancy;
    private readonly Dictionary<string, ScopeConfig>? _scopes;

    /// <summary>
    /// <paramref name="database"/> is optional. When supplied, the verifier resolves
    /// the IdP-issued <c>oid</c> to the local <c>users.id</c> after signature/claim
    /// verification — so audit columns and audit_log.actor_id always store users.id,
    /// not the IdP's foreign identifier. <paramref name="pkAssignment"/> is
    /// <see cref="AuthConfig.PkAssignment"/> — auto-provision inserts honor a forced
    /// <c>manual</c> PK strategy for the users table.
    /// </summary>
    public OidcVerifier(
        OidcConfig config,
        Database? database = null,
        Dictionary<string, string>? pkAssignment = null,
        TenancyConfig? tenancy = null,
        Dictionary<string, ScopeConfig>? scopes = null)
    {
        _config = IdpPresets.Apply(config);
        _database = database;
        _pkAssignment = pkAssignment;
        _tenancy = tenancy;
        _scopes = scopes;
    }

    /// <summary>
    /// True when the token's alg is in the configured whitelist. Used by <see cref="JwtGuard"/>
    /// to short-circuit: if alg is one of ours, dispatch here; otherwise stay on HS256.
    /// </summary>
    public bool HandlesAlgorithm(string alg) => AllowedAlgorithms().Contains(alg);

    /// <summary>
    /// Verify the signature, validate claims, map IdP-specific fields into Shape's
    /// auth-context shape (<c>id</c>/<c>email</c>/<c>role</c>), and optionally
    /// resolve <c>oid</c> → local <c>users.id</c>.
    /// </summary>
    public Dictionary<string, object?> Verify(
        byte[] signingInput,
        byte[] signature,
        Dictionary<string, object?> header,
        Dictionary<string, object?> payload)
    {
        var alg = header.GetValueOrDefault("alg")?.ToString() ?? "";
        if (!AllowedAlgorithms().Contains(alg))
            throw new ApiException("AUTH_INVALID", $"Algorithm '{alg}' is not allowed for OIDC tokens.", 401);

        var kid = header.GetValueOrDefault("kid")?.ToString() ?? "";
        using var rsa = ResolvePublicKey(kid);

        var hashAlgo = SupportedAlgorithms[alg];
        var ok = rsa.VerifyData(signingInput, signature, hashAlgo, RSASignaturePadding.Pkcs1);
        if (!ok)
            throw new ApiException("AUTH_INVALID", "OIDC token signature mismatch.", 401);

        ValidateClaims(payload);

        var context = MapClaims(payload);

        if (_database != null)
            context = ResolveLocalUserId(context);
        else if (_tenancy != null)
            // SECURITY-REVIEW L9: under tenancy, the tenant is always derived
            // from the local user row — never from the IdP token. Without a
            // Database to resolve that row, a token-supplied `tenant_id` must not
            // be trusted; drop it so the scoped-role path fails closed (missing
            // tenant claim) instead of honoring an attacker-chosen tenant.
            context.Remove("tenant_id");

        return context;
    }

    // ── Algorithm whitelist ───────────────────────────────────────────

    private List<string> AllowedAlgorithms()
    {
        var configured = _config.Algorithms;
        if (configured == null || configured.Count == 0)
            return new List<string> { "RS256" };

        var result = new List<string>();
        foreach (var alg in configured)
        {
            if (!string.IsNullOrEmpty(alg) && SupportedAlgorithms.ContainsKey(alg))
                result.Add(alg);
        }
        return result.Count > 0 ? result : new List<string> { "RS256" };
    }

    // ── Public key resolution ─────────────────────────────────────────

    private RSA ResolvePublicKey(string kid)
    {
        var jwks = LoadJwks(forceFresh: false);
        var key = FindKey(jwks, kid);
        if (key == null)
        {
            // Cache might be stale — try a fresh fetch once before giving up.
            jwks = LoadJwks(forceFresh: true);
            key = FindKey(jwks, kid);
        }
        if (key == null)
            throw new ApiException("AUTH_INVALID", $"OIDC signing key '{kid}' not found in JWKS.", 401);

        return JwkToRsa(key);
    }

    /// <summary>
    /// Returns a JWKS payload (<c>{ keys: [...] }</c>) with caching. A failed
    /// fetch falls back to the stale cache so a brief IdP outage doesn't take
    /// the API down.
    /// </summary>
    internal Dictionary<string, object?> LoadJwks(bool forceFresh)
    {
        var uri = _config.JwksUri;
        if (string.IsNullOrEmpty(uri))
            throw new ApiException("AUTH_CONFIG_ERROR", "OIDC 'JwksUri' is not configured.", 500);

        var cacheDir = _config.JwksCacheDir ?? Path.Combine(Path.GetTempPath(), "shape_jwks");
        // SECURITY-REVIEW H2: the JWKS cache is trusted on read, so a
        // world-writable cache in a shared temp dir lets a local attacker
        // pre-plant a file with their own RSA key and forge OIDC tokens. Create
        // the dir 0700 and refuse to use it if it is group/other-writable (i.e.
        // not exclusively ours). Cache files inherit the same check on read.
        EnsureSecureCacheDir(cacheDir);
        var cacheFile = Path.Combine(cacheDir, Sha256Hex(uri) + ".json");
        var ttl = _config.JwksCacheTtl > 0 ? _config.JwksCacheTtl : 3600;

        if (!forceFresh && File.Exists(cacheFile) && CacheFileIsTrusted(cacheFile))
        {
            var ageSec = (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile)).TotalSeconds;
            if (ageSec < ttl)
            {
                var cached = TryReadJsonFile(cacheFile);
                if (cached != null) return cached;
            }
        }

        var fresh = FetchJwks(uri);
        if (fresh == null)
        {
            // Fall back to stale cache if available — but only within a bounded
            // window so a revoked signing key can't validate tokens forever
            // during a prolonged IdP outage.
            if (File.Exists(cacheFile) && CacheFileIsTrusted(cacheFile))
            {
                var staleAge = (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile)).TotalSeconds;
                var maxStale = _config.JwksMaxStaleTtl > 0 ? _config.JwksMaxStaleTtl : ttl * 8;
                if (staleAge <= maxStale)
                {
                    var stale = TryReadJsonFile(cacheFile);
                    if (stale != null) return stale;
                }
            }
            throw new ApiException("AUTH_CONFIG_ERROR", "Failed to fetch JWKS and no fresh-enough cached copy available.", 503);
        }

        try
        {
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(fresh));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(cacheFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best-effort cache write */ }
        return fresh;
    }

    /// <summary>
    /// Create the JWKS cache directory 0700 and reject it if it is
    /// group/other-writable — a writable cache dir on a shared host is a
    /// key-substitution vector (SECURITY-REVIEW H2). No-op on Windows, where
    /// directory ACLs (not POSIX mode bits) govern access.
    /// </summary>
    private static void EnsureSecureCacheDir(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(cacheDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* best-effort; the mode check below is the real gate */ }

        var mode = File.GetUnixFileMode(cacheDir);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new ApiException(
                "AUTH_CONFIG_ERROR",
                "OIDC JWKS cache directory is group/other-writable and cannot be trusted.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = $"Point OidcConfig.JwksCacheDir at a private directory owned by the service account (mode 0700). Current: '{cacheDir}'."
                });
    }

    /// <summary>
    /// A cache file is trusted only when it is not group/other-writable. Blocks
    /// reading a JWKS an attacker planted or tampered with (SECURITY-REVIEW H2).
    /// Always true on Windows (ACL-governed).
    /// </summary>
    private static bool CacheFileIsTrusted(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object?>? TryReadJsonFile(string path)
    {
        try
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw);
            if (parsed == null || !parsed.TryGetValue("keys", out var keysEl)
                || keysEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            return UnwrapJwks(parsed);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?>? FetchJwks(string uri)
    {
        try
        {
            var raw = Http.GetStringAsync(uri).GetAwaiter().GetResult();
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw);
            if (parsed == null || !parsed.TryGetValue("keys", out var keysEl)
                || keysEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            return UnwrapJwks(parsed);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> UnwrapJwks(Dictionary<string, JsonElement> parsed)
    {
        var keys = new List<object?>();
        foreach (var key in parsed["keys"].EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object) continue;
            var keyDict = new Dictionary<string, object?>();
            foreach (var prop in key.EnumerateObject())
                keyDict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
            keys.Add(keyDict);
        }
        return new Dictionary<string, object?> { ["keys"] = keys };
    }

    private static Dictionary<string, object?>? FindKey(Dictionary<string, object?> jwks, string kid)
    {
        if (jwks["keys"] is not List<object?> keys) return null;

        foreach (var key in keys)
        {
            if (key is not Dictionary<string, object?> dict) continue;
            if (kid.Length > 0 && dict.GetValueOrDefault("kid")?.ToString() == kid)
                return dict;
        }

        // No kid in the token? Fall back to the only key when the set has one.
        if (kid.Length == 0 && keys.Count == 1 && keys[0] is Dictionary<string, object?> only)
            return only;

        return null;
    }

    /// <summary>
    /// Build an <see cref="RSA"/> instance from a JWK with <c>n</c> (modulus) and
    /// <c>e</c> (exponent). Replaces PHP's hand-rolled DER encoder with .NET's
    /// native <see cref="RSAParameters"/> import.
    /// </summary>
    internal static RSA JwkToRsa(Dictionary<string, object?> jwk)
    {
        var kty = jwk.GetValueOrDefault("kty")?.ToString() ?? "";
        if (kty != "RSA")
            throw new ApiException("AUTH_INVALID", $"Unsupported JWK type '{kty}' (only RSA is supported).", 401);

        var nStr = jwk.GetValueOrDefault("n")?.ToString();
        var eStr = jwk.GetValueOrDefault("e")?.ToString();
        if (string.IsNullOrEmpty(nStr) || string.IsNullOrEmpty(eStr))
            throw new ApiException("AUTH_INVALID", "OIDC signing key is missing modulus/exponent.", 401);

        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Base64UrlDecode(nStr),
            Exponent = Base64UrlDecode(eStr)
        });
        return rsa;
    }

    // ── Claim validation ──────────────────────────────────────────────

    private void ValidateClaims(Dictionary<string, object?> payload)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // SECURITY-REVIEW M1: require `exp` — never accept a token that cannot
        // expire. L10: allow a small clock-skew leeway at the boundary.
        const long clockSkewSeconds = 60;
        if (!TryAsLong(payload.GetValueOrDefault("exp"), out var exp))
            throw new ApiException("AUTH_INVALID", "OIDC token is missing the required exp claim.", 401);
        if (now > exp + clockSkewSeconds)
            throw new ApiException("AUTH_INVALID", "OIDC token expired.", 401);

        if (TryAsLong(payload.GetValueOrDefault("nbf"), out var nbf) && now + clockSkewSeconds < nbf)
            throw new ApiException("AUTH_INVALID", "OIDC token not active yet.", 401);

        // SECURITY-REVIEW M2: issuer and audience are MANDATORY when OIDC is
        // enabled. Without an `aud` binding, any token the IdP signed for any
        // application under the same issuer would be accepted (confused-deputy).
        // The boot-time check normally catches an unconfigured audience/issuer;
        // this is the runtime backstop for programmatically-built verifiers.
        if (string.IsNullOrEmpty(_config.Issuer))
            throw new ApiException("AUTH_CONFIG_ERROR", "OIDC issuer is not configured.", 500);
        if (string.IsNullOrEmpty(_config.Audience))
            throw new ApiException("AUTH_CONFIG_ERROR", "OIDC audience is not configured.", 500);

        var iss = payload.GetValueOrDefault("iss")?.ToString() ?? "";
        if (iss != _config.Issuer)
            throw new ApiException("AUTH_INVALID", "OIDC token issuer mismatch.", 401);

        var tokenAud = payload.GetValueOrDefault("aud");
        switch (tokenAud)
        {
            case string s when s == _config.Audience:
                break;
            case List<object?> list when list.Any(a => a?.ToString() == _config.Audience):
                break;
            default:
                throw new ApiException("AUTH_INVALID", "OIDC token audience mismatch.", 401);
        }
    }

    // ── Claim mapping ─────────────────────────────────────────────────

    /// <summary>
    /// Map IdP-specific claims into Shape's auth-context format
    /// (<c>id</c>, <c>email</c>, <c>role</c>) without losing the original claims.
    /// </summary>
    internal Dictionary<string, object?> MapClaims(Dictionary<string, object?> payload)
    {
        var idClaim = _config.IdClaim;
        var emailClaim = _config.EmailClaim;
        var roleClaim = _config.RoleClaim;
        var mapping = _config.RoleMapping ?? new Dictionary<string, string>();

        var rawRole = payload.GetValueOrDefault(roleClaim);
        var role = "";
        if (rawRole is string singleRole)
        {
            role = mapping.TryGetValue(singleRole, out var mapped) ? mapped : singleRole;
        }
        else if (rawRole is IEnumerable<object?> rawRoleList)
        {
            // Token has roles[]; the first claim that has a mapping wins.
            // If nothing maps, fall back to the first non-empty role string.
            foreach (var candidate in rawRoleList)
            {
                if (candidate is string s && mapping.TryGetValue(s, out var mapped))
                {
                    role = mapped;
                    break;
                }
            }
            if (role.Length == 0)
            {
                foreach (var candidate in rawRoleList)
                {
                    if (candidate is string s && s.Length > 0)
                    {
                        role = s;
                        break;
                    }
                }
            }
        }

        var context = new Dictionary<string, object?>(payload);
        context["id"] = payload.GetValueOrDefault(idClaim) ?? payload.GetValueOrDefault("sub");
        context["email"] = (payload.GetValueOrDefault(emailClaim) ?? payload.GetValueOrDefault("email") ?? "").ToString();
        context["role"] = role;
        context["_oidc"] = true;
        return context;
    }

    // ── Local user resolution ─────────────────────────────────────────

    /// <summary>
    /// Map the verified token's <c>oid</c> to the local <c>users.id</c>. Throws
    /// <c>AUTH_FORBIDDEN</c> with a clear hint when the user isn't provisioned
    /// and <c>auto_provision</c> is off.
    /// </summary>
    private Dictionary<string, object?> ResolveLocalUserId(Dictionary<string, object?> context)
    {
        var usersTable = _config.UsersTable;
        // Auto-detection runs in ShapeApp.FromDatabase. When null reaches us
        // here (entity-first paths or paths without an introspector), fall
        // back to the historical default so behavior stays compatible.
        var lookupColumn = _config.UsersLookupColumn ?? "oid";
        var oidObj = context.GetValueOrDefault("oid")
            ?? context.GetValueOrDefault("sub")
            ?? context.GetValueOrDefault("id");
        var oid = oidObj?.ToString() ?? "";

        if (oid.Length == 0)
            throw new ApiException("AUTH_INVALID", "OIDC token has no usable identifier for local user lookup.", 401);

        var tenantSelect = _tenancy != null ? ", " + _database!.QuoteIdentifier("tenant_id") : "";
        // Row scopes (ROW-SCOPES-PLAN §2.2): dimension claims resolve from the
        // LOCAL user row, never from the IdP token. Membership-table scopes are
        // loaded separately once the local user id is known.
        var scopeColumns = UsersRowScopeColumns();
        var scopeSelect = string.Concat(scopeColumns.Select(column => ", " + _database!.QuoteIdentifier(column)));
        var sql = "SELECT " + _database!.TopClause(1)
            + _database.QuoteIdentifier("id") + ", "
            + _database.QuoteIdentifier("email") + ", "
            + _database.QuoteIdentifier("role")
            + tenantSelect
            + scopeSelect
            + " FROM " + _database.QuoteIdentifier(usersTable)
            + " WHERE " + _database.QuoteIdentifier(lookupColumn) + " = @oid"
            + _database.RowLimitSuffix(1);

        var result = _database.Run(sql, new Dictionary<string, object?> { ["oid"] = oid });
        var row = result.Rows.Count > 0 ? result.Rows[0] : null;

        if (row == null)
        {
            if (_config.AutoProvision)
            {
                var tokenRole = context.GetValueOrDefault("role")?.ToString() ?? "";
                var effectiveRole = tokenRole.Length > 0 ? tokenRole : _config.AutoProvisionRole;
                var provisionTenant = ResolveProvisionTenant(context);

                // Gate the INSERT on the mapped role being one the operator
                // explicitly declared via RoleMapping. Without this gate, a
                // token whose role didn't match any mapping key falls through
                // verbatim and would auto-provision a row that could never
                // pass a mask check — orphan junk in the users table.
                var mappingValues = _config.RoleMapping?.Values.Distinct().ToList() ?? new List<string>();
                if (mappingValues.Count > 0 && !mappingValues.Contains(effectiveRole))
                {
                    throw new ApiException(
                        "AUTH_FORBIDDEN",
                        $"OIDC user has unmapped role '{effectiveRole}'; nothing was provisioned.",
                        403,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["expected"] = "token role mapped to one of: " + string.Join(", ", mappingValues),
                        ["hint"] = $"Token's role claim resolved to '{effectiveRole}', which isn't in OidcConfig.RoleMapping values. Either add a mapping entry for this IdP role, remove the user from it, or — to accept the IdP role verbatim — clear RoleMapping to opt out of the framework-role gate."
                    });
                }

                return AutoProvision(context, oid, usersTable, lookupColumn, effectiveRole, provisionTenant);
            }

            throw new ApiException(
                "AUTH_FORBIDDEN",
                $"OIDC user '{oid}' is not provisioned in '{usersTable}'.",
                403,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = $"Row in '{usersTable}' where '{lookupColumn}' = '{oid}'",
                    ["hint"] = $"Insert a row into '{usersTable}' with {lookupColumn} set to the token's oid, or set OidcConfig.AutoProvision = true to create rows on first login."
                });
        }

        // Overwrite id with the local users.id (int PK). Preserve oid for
        // app code that wants the IdP identifier. Keep token-mapped role as
        // authoritative (the IdP's role-assignment decision wins by default).
        var idCell = row[0];
        context["id"] = idCell switch
        {
            int i => i,
            long l => l,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => idCell
        };
        context["oid"] = oid;
        var localEmail = StringOrNull(row.Length > 1 ? row[1] : null);
        if (localEmail != null)
            context["email"] = localEmail;
        var rowTenant = _tenancy != null ? row.Length > 3 ? row[3] : null : null;
        if (_tenancy != null)
        {
            var role = StringOrNull(row.Length > 2 ? row[2] : null) ?? "";
            var unscoped = TenancyResolver.IsUnscopedRole(_tenancy, role);
            // Unscoped roles (superadmin) stay reachable even when their home
            // tenant is suspended — otherwise the account that can re-activate a
            // tenant is locked out with it. Mirrors LoginService.Login().
            if (!unscoped)
                TenancyResolver.EnsureTenantActive(_database!, _tenancy, rowTenant);
            context["tenant_id"] = unscoped ? null : rowTenant;
        }

        // Dimension claims from the local row / membership table.
        var scopeBase = 3 + (_tenancy != null ? 1 : 0);
        var scopeValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < scopeColumns.Count; i++)
        {
            var idx = scopeBase + i;
            scopeValues[scopeColumns[i]] = idx < row.Length ? row[idx] : null;
        }
        ApplyScopeClaims(context, scopeValues, StringOrNull(row.Length > 2 ? row[2] : null) ?? "");

        context["_local_user"] = new Dictionary<string, object?>
        {
            ["id"] = context["id"],
            ["email"] = localEmail,
            ["role"] = StringOrNull(row.Length > 2 ? row[2] : null)
                ?? "",
            ["tenant_id"] = _tenancy != null ? context["tenant_id"] : null
        };

        return context;
    }

    private Dictionary<string, object?> AutoProvision(
        Dictionary<string, object?> context,
        string oid,
        string usersTable,
        string lookupColumn,
        string role,
        object? tenantId)
    {
        var email = context.GetValueOrDefault("email")?.ToString() ?? "";

        // password_hash is non-null on most schemas — store a sentinel that
        // can never validate via BCrypt.Verify so this row can't be used for
        // the local password-login flow. The user only authenticates via OIDC.
        const string sentinel = "!locked-oidc-only!";

        var data = new Dictionary<string, object?>
        {
            [lookupColumn] = oid,
            ["email"] = email,
            ["password_hash"] = sentinel,
            ["role"] = role
        };
        if (_tenancy != null)
        {
            if (tenantId == null)
                throw new ApiException(
                    "AUTH_CONFIG_ERROR",
                    "OIDC auto-provision requires a tenant source.",
                    500,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["hint"] = "Configure `tenancy.oidc_tenant_claim` or `tenancy.oidc_auto_provision_tenant`."
                    });

            data["tenant_id"] = tenantId;
            TenancyResolver.EnsureTenantActive(_database!, _tenancy, tenantId);
        }

        // Row scopes: auto-provision stamps single-valued dimensions from
        // `scopes.<column>.oidc_auto_provision_value` (mirror of the tenancy
        // keys) or rejects — the framework never guesses a dimension. A
        // membership-table scope cannot be provisioned by a users-row insert.
        var provisionedScopes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (_scopes is { Count: > 0 })
        {
            foreach (var (column, scope) in _scopes)
            {
                if (scope.Self)
                    continue; // claim = the new user's own id

                if (scope.ClaimTable != null)
                    throw new ApiException(
                        "AUTH_CONFIG_ERROR",
                        $"OIDC auto-provision cannot populate membership scope '{column}'.",
                        500,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["hint"] = $"Provision users (and their '{scope.ClaimTable.Table}' membership rows) explicitly, or disable OidcConfig.AutoProvision."
                        });

                if (!scope.OidcAutoProvisionValue.HasValue)
                    throw new ApiException(
                        "AUTH_CONFIG_ERROR",
                        $"OIDC auto-provision requires a value source for scope '{column}'.",
                        500,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["hint"] = $"Configure `scopes.{column}.oidc_auto_provision_value` or disable OidcConfig.AutoProvision."
                        });

                var value = ParamResolver.UnwrapJsonElement(scope.OidcAutoProvisionValue.Value);
                data[column] = value;
                provisionedScopes[column] = value;
            }
        }

        long newId;
        if (Metadata.PkStrategyResolver.IsForcedManual(_pkAssignment, usersTable))
        {
            // Legacy users table without IDENTITY — allocate MAX(id)+1 atomically.
            newId = _database!.InsertWithManualPk(usersTable, "id", data);
        }
        else
        {
            var cols = data.Keys.ToList();
            var quoted = cols.Select(c => _database!.QuoteIdentifier(c));
            var placeholders = cols.Select(c => "@" + c);

            var sql = "INSERT INTO " + _database!.QuoteIdentifier(usersTable)
                + " (" + string.Join(", ", quoted) + ")"
                + " VALUES (" + string.Join(", ", placeholders) + ")";

            newId = _database.InsertReturningId(sql, data);
        }

        context["id"] = newId;
        context["oid"] = oid;
        context["email"] = email;
        if (_tenancy != null)
        {
            var unscoped = TenancyResolver.IsUnscopedRole(_tenancy, role);
            context["tenant_id"] = unscoped ? null : tenantId;
        }
        ApplyScopeClaims(context, provisionedScopes, role);
        context["_local_user"] = new Dictionary<string, object?>
        {
            ["id"] = newId,
            ["email"] = email,
            ["role"] = role,
            ["tenant_id"] = _tenancy != null ? context["tenant_id"] : null,
            ["auto_provisioned"] = true
        };
        return context;
    }

    /// <summary>Scope columns whose value lives on the users row (membership-table scopes excluded).</summary>
    private List<string> UsersRowScopeColumns() =>
        _scopes is { Count: > 0 }
            ? _scopes.Where(kv => kv.Value.ClaimTable == null && !kv.Value.Self).Select(kv => kv.Key).ToList()
            : new List<string>();

    /// <summary>
    /// Stamp the per-dimension claims on the auth context: unscoped roles get
    /// an explicit null; membership-table scopes run one indexed SELECT keyed
    /// by the resolved local user id; multi scopes normalize to a list.
    /// </summary>
    private void ApplyScopeClaims(
        Dictionary<string, object?> context,
        Dictionary<string, object?> usersRowValues,
        string localRole)
    {
        if (_scopes is not { Count: > 0 })
            return;

        foreach (var (column, scope) in _scopes)
        {
            var claimKey = ScopeSpec.ClaimKeyFor(column, scope);
            if (scope.UnscopedRoles.Contains(localRole, StringComparer.Ordinal))
            {
                context[claimKey] = null;
                continue;
            }

            if (scope.ClaimTable != null)
            {
                var result = _database!.Run(
                    "SELECT " + _database.QuoteIdentifier(column)
                    + " FROM " + _database.QuoteIdentifier(scope.ClaimTable.Table)
                    + " WHERE " + _database.QuoteIdentifier(scope.ClaimTable.UserColumn) + " = @user_id",
                    new Dictionary<string, object?> { ["user_id"] = context.GetValueOrDefault("id") });
                context[claimKey] = result.Rows
                    .Select(r => r.Length > 0 ? r[0] : null)
                    .Where(v => v != null)
                    .ToList();
                continue;
            }

            var value = scope.Self ? context.GetValueOrDefault("id") : usersRowValues.GetValueOrDefault(column);
            if (scope.Multi && value != null)
                value = new List<object?> { value };
            context[claimKey] = value;
        }
    }

    private object? ResolveProvisionTenant(Dictionary<string, object?> payload)
    {
        if (_tenancy == null)
            return null;

        if (!string.IsNullOrWhiteSpace(_tenancy.OidcTenantClaim)
            && payload.TryGetValue(_tenancy.OidcTenantClaim, out var claimTenant)
            && TenancyResolver.TenantValueAsString(claimTenant) != null)
        {
            return claimTenant;
        }

        if (_tenancy.OidcAutoProvisionTenant.HasValue)
            return ParamResolver.UnwrapJsonElement(_tenancy.OidcAutoProvisionTenant.Value);

        throw new ApiException(
            "AUTH_CONFIG_ERROR",
            "OIDC auto-provision is missing a tenant source.",
            500,
            null,
            new Dictionary<string, object?>
            {
                ["hint"] = "Configure `tenancy.oidc_tenant_claim` or `tenancy.oidc_auto_provision_tenant`."
            });
    }

    // ── Helpers ───────────────────────────────────────────────────────

    internal static byte[] Base64UrlDecode(string raw)
    {
        var padded = raw.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static bool TryAsLong(object? value, out long result)
    {
        switch (value)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case double d: result = (long)d; return true;
            case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed; return true;
            default:
                result = 0; return false;
        }
    }

    private static string? StringOrNull(object? value)
    {
        if (value == null) return null;
        var s = value.ToString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static string Sha256Hex(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
