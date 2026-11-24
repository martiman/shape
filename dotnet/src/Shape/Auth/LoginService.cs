using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Shape.Core;
using Shape.Metadata;

namespace Shape.Auth;

/// <summary>
/// HS256 local-user authentication: login, refresh-with-rotation, logout,
/// admin create-user / change-password. Backed by <see cref="Database"/>
/// (driver-aware via Dapper) so the same flow works on MySQL / Postgres /
/// SQLite without changes.
/// </summary>
public sealed class LoginService
{
    private readonly DatabaseConfig _dbConfig;
    private readonly AuthConfig _authConfig;
    private readonly JwtIssuer _issuer;

    /// <summary>
    /// A valid BCrypt hash used only to spend equivalent CPU on the
    /// user-not-found login branch, closing the enumeration timing oracle
    /// (SECURITY-REVIEW M4). Generated at type init so it is always parseable.
    /// </summary>
    private static readonly string DummyBcryptHash = BCrypt.Net.BCrypt.HashPassword("shape-timing-equalizer");

    public LoginService(DatabaseConfig dbConfig, AuthConfig authConfig, JwtIssuer issuer)
    {
        _dbConfig = dbConfig;
        _authConfig = authConfig;
        _issuer = issuer;
    }

    /// <summary>
    /// Backwards-compat overload — older callers passed the connection string
    /// directly. Wraps it in a default <see cref="DatabaseConfig"/> with
    /// <c>Driver = "mysql"</c>; new code should use the strongly-typed overload.
    /// </summary>
    public LoginService(string connectionString, AuthConfig authConfig, JwtIssuer issuer)
        : this(new DatabaseConfig { ConnectionString = connectionString, Driver = "mysql" }, authConfig, issuer)
    {
    }

    private void EnsureLocalLoginEnabled()
    {
        if (!_authConfig.LocalLoginDisabled) return;

        throw new ApiException(
            "AUTH_NOT_CONFIGURED",
            "Local password authentication is disabled on this deployment.",
            400,
            null,
            new Dictionary<string, object?>
            {
                ["hint"] = _authConfig.Oidc != null
                    ? "Authenticate via the configured OIDC provider; obtain a Bearer token from the IdP and include it in the Authorization header."
                    : "AuthConfig.LocalLoginDisabled is true but no AuthConfig.Oidc is configured. Set one or the other."
            });
    }

    public Dictionary<string, object?> Login(Dictionary<string, object?> payload)
    {
        EnsureLocalLoginEnabled();

        var email = GetString(payload, "email")?.Trim() ?? "";
        var password = GetString(payload, "password") ?? "";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            throw new ApiException("AUTH_VALIDATION_ERROR", "Fields email and password are required.", 400);

        using var db = new Database(_dbConfig);
        var row = FindUserByEmail(db, email);
        if (row == null)
        {
            // SECURITY-REVIEW M4: run a dummy verify so the not-found branch
            // costs the same as a wrong-password branch. Without it the absence
            // of the BCrypt work is a timing oracle for enumerating valid emails.
            BCrypt.Net.BCrypt.Verify(password, DummyBcryptHash);
            throw new ApiException("AUTH_INVALID_CREDENTIALS", "Invalid email or password.", 401);
        }

        var hash = row.GetValueOrDefault("password_hash")?.ToString() ?? "";
        if (string.IsNullOrEmpty(hash) || !BCrypt.Net.BCrypt.Verify(password, hash))
            throw new ApiException("AUTH_INVALID_CREDENTIALS", "Invalid email or password.", 401);

        var isActive = ToInt(row.GetValueOrDefault("is_active") ?? 1);
        if (isActive != 1)
            throw new ApiException("AUTH_INVALID_CREDENTIALS", "Invalid email or password.", 401);

        var userId = ToInt(row.GetValueOrDefault("id") ?? 0);
        if (userId <= 0)
            throw new ApiException("AUTH_CONFIG_ERROR", "Invalid users table data.", 500);

        // Unscoped roles (superadmin) see across tenants and must stay reachable
        // even when their home tenant is suspended — otherwise the only account
        // that can re-activate a tenant is locked out with it.
        if (!TenancyResolver.IsUnscopedRole(_authConfig.Tenancy, row.GetValueOrDefault("role")?.ToString()))
            TenancyResolver.EnsureTenantActive(db, _authConfig.Tenancy, row.GetValueOrDefault("tenant_id"));

        AttachScopeClaims(db, row);

        return IssueTokenPair(db, row);
    }

    public Dictionary<string, object?> Refresh(Dictionary<string, object?> payload)
    {
        var refreshToken = GetString(payload, "refresh_token")?.Trim() ?? "";
        if (string.IsNullOrEmpty(refreshToken))
            throw new ApiException("AUTH_VALIDATION_ERROR", "Field refresh_token is required.", 400);

        using var db = new Database(_dbConfig);
        var tokensTable = db.QuoteIdentifier(SafeTable(_authConfig.RefreshTokensTable, "refresh_tokens"));
        var usersTable = db.QuoteIdentifier(SafeTable(_authConfig.UsersTable, "users"));
        var hash = ComputeSha256(refreshToken);

        db.BeginTransaction();
        try
        {
            var sel = db.Run(
                $"SELECT {db.TopClause(1)}rt.id, rt.user_id, rt.expires_at, rt.revoked_at, " +
                $"u.email, u.display_name, u.role, u.is_active " +
                (_authConfig.Tenancy != null ? $", u.{TenantColumn()} AS tenant_id " : " ") +
                string.Concat(ScopeColumnSelects("u.")) + " " +
                $"FROM {tokensTable} rt " +
                $"INNER JOIN {usersTable} u ON u.id = rt.user_id " +
                $"WHERE rt.token_hash = @token_hash{db.RowLimitSuffix(1)}",
                new Dictionary<string, object?> { ["token_hash"] = hash });

            if (sel.Rows.Count == 0)
            {
                db.Rollback();
                throw new ApiException("AUTH_INVALID_REFRESH_TOKEN", "Invalid refresh token.", 401);
            }

            var row = ToAssoc(sel.Columns, sel.Rows[0]);

            var tokenId = ToInt(row.GetValueOrDefault("id"));
            var userId = ToInt(row.GetValueOrDefault("user_id"));
            var revokedAt = row.GetValueOrDefault("revoked_at");
            var expiresAt = row.GetValueOrDefault("expires_at");
            var isActive = ToInt(row.GetValueOrDefault("is_active") ?? 1);

            if (tokenId <= 0 || userId <= 0 || isActive != 1)
            {
                db.Rollback();
                throw new ApiException("AUTH_INVALID_REFRESH_TOKEN", "Invalid refresh token.", 401);
            }
            if (revokedAt != null)
            {
                db.Rollback();
                throw new ApiException("AUTH_INVALID_REFRESH_TOKEN",
                    "Refresh token already used or revoked.", 401);
            }
            if (ParseDateTime(expiresAt) is { } exp && exp <= DateTime.UtcNow)
            {
                db.Rollback();
                throw new ApiException("AUTH_INVALID_REFRESH_TOKEN", "Refresh token expired.", 401);
            }

            // Unscoped roles (superadmin) must keep refreshing even if their
            // home tenant is suspended — see the note in Login().
            if (!TenancyResolver.IsUnscopedRole(_authConfig.Tenancy, row.GetValueOrDefault("role")?.ToString()))
                TenancyResolver.EnsureTenantActive(db, _authConfig.Tenancy, row.GetValueOrDefault("tenant_id"));

            var newRefreshToken = GenerateRefreshToken();
            var newRefreshHash = ComputeSha256(newRefreshToken);
            var refreshTtl = GetRefreshTokenTtl();
            var newExpiresAt = DateTime.UtcNow.AddSeconds(refreshTtl).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            var newTokenId = InsertRow(db,
                SafeTable(_authConfig.RefreshTokensTable, "refresh_tokens"),
                new Dictionary<string, object?>
                {
                    ["user_id"] = userId,
                    ["token_hash"] = newRefreshHash,
                    ["expires_at"] = newExpiresAt
                });

            db.Exec(
                $"UPDATE {tokensTable} SET revoked_at = CURRENT_TIMESTAMP, " +
                $"replaced_by_token_id = @new_token_id WHERE id = @id",
                new Dictionary<string, object?> { ["new_token_id"] = newTokenId, ["id"] = tokenId });

            db.Commit();

            var user = new Dictionary<string, object?>
            {
                ["id"] = userId,
                ["email"] = row.GetValueOrDefault("email")?.ToString() ?? "",
                ["display_name"] = row.GetValueOrDefault("display_name")?.ToString() ?? "",
                ["role"] = row.GetValueOrDefault("role")?.ToString() ?? "user",
                ["tenant_id"] = row.GetValueOrDefault("tenant_id")
            };
            // Refresh re-reads the dimension claims (ROW-SCOPES-PLAN §2.2) —
            // copy the aliased users-row values, then load memberships.
            foreach (var claimKey in ScopeClaimKeys())
                user[claimKey] = row.GetValueOrDefault(claimKey);
            AttachScopeClaims(db, user);

            return BuildTokenResponse(user, newRefreshToken, refreshTtl);
        }
        catch
        {
            if (db.IsInTransaction) db.Rollback();
            throw;
        }
    }

    public Dictionary<string, object?> Logout(Dictionary<string, object?> payload)
    {
        var refreshToken = GetString(payload, "refresh_token")?.Trim() ?? "";
        if (string.IsNullOrEmpty(refreshToken))
            throw new ApiException("AUTH_VALIDATION_ERROR", "Field refresh_token is required.", 400);

        using var db = new Database(_dbConfig);
        var tokensTable = db.QuoteIdentifier(SafeTable(_authConfig.RefreshTokensTable, "refresh_tokens"));
        var hash = ComputeSha256(refreshToken);

        var affected = db.Exec(
            $"UPDATE {tokensTable} SET revoked_at = CURRENT_TIMESTAMP " +
            $"WHERE token_hash = @token_hash AND revoked_at IS NULL",
            new Dictionary<string, object?> { ["token_hash"] = hash });

        return new Dictionary<string, object?> { ["revoked"] = affected > 0 };
    }

    /// <summary>
    /// <c>auth.exchange</c> (brownfield adapter guide §3): swap an
    /// app-validated external token for a Shape-issued JWT, RFC 8693 style.
    /// Exchange-once by design — the app's <see cref="ITokenExchangeSource"/>
    /// verifies the foreign token a single time; afterwards the caller is an
    /// ordinary Shape client and tenancy claims, role gates, and suspension
    /// checks apply through the standard machinery. Stateless by default
    /// (access token only); <see cref="TokenExchangeConfig.IssueRefreshToken"/>
    /// opts into the full rotation pair.
    /// </summary>
    public Dictionary<string, object?> Exchange(Dictionary<string, object?> payload)
    {
        var exchange = _authConfig.Exchange;
        if (exchange?.Source == null)
            throw new ApiException(
                "AUTH_NOT_CONFIGURED",
                "Token exchange is not configured.",
                501,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Set AuthConfig.Exchange with an ITokenExchangeSource to enable auth.exchange."
                });

        var token = GetString(payload, "token")?.Trim() ?? "";
        if (token.Length == 0)
            throw new ApiException("AUTH_VALIDATION_ERROR", "Field token is required.", 400, null,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?> { ["token"] = "string (the external token to exchange)" }
                });

        using var db = new Database(_dbConfig);
        var identity = exchange.Source.Validate(token, db);
        if (identity == null)
            throw new ApiException(
                "AUTH_EXCHANGE_INVALID",
                "External token is invalid, expired, or revoked.",
                401,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Obtain a fresh token from the application's own login flow, then retry the exchange."
                });

        identity = ApplyClaimMapping(identity, exchange.ClaimMapping);

        var role = identity.GetValueOrDefault("role")?.ToString() ?? "user";
        if (exchange.RoleMapping is { Count: > 0 } roleMapping
            && roleMapping.TryGetValue(role, out var mappedRole)
            && !string.IsNullOrWhiteSpace(mappedRole))
        {
            role = mappedRole;
        }
        identity["role"] = role;

        // The id may be the app's native type (int, Guid, …) — require presence,
        // not int-ness; BuildTokenResponse keeps non-numeric ids verbatim.
        if (string.IsNullOrWhiteSpace(identity.GetValueOrDefault("id")?.ToString()))
            throw new ApiException(
                "AUTH_CONFIG_ERROR",
                "Token exchange source returned no usable user id.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "ITokenExchangeSource.Validate must return an `id` key (or map one via TokenExchangeConfig.ClaimMapping)."
                });

        // Same §2.3 rules as login: a scoped role needs a live tenant; an
        // unscoped role stays reachable even when its home tenant is suspended.
        var tenancy = _authConfig.Tenancy;
        if (tenancy != null && !TenancyResolver.IsUnscopedRole(tenancy, role))
            TenancyResolver.EnsureTenantActive(db, tenancy, identity.GetValueOrDefault("tenant_id"));

        var response = exchange.IssueRefreshToken
            ? IssueTokenPair(db, identity)
            : BuildTokenResponse(identity, refreshToken: null, refreshTtl: 0);

        // Extra identity keys the source chose to return (e.g. organization_name)
        // surface on the response's user object — the source author controls the
        // identity dictionary, so returning a key IS the pass-through intent.
        // Only the exchange path does this; local-login rows carry secrets
        // (password_hash) and keep the fixed key set.
        if (response.GetValueOrDefault("user") is Dictionary<string, object?> userObject)
        {
            foreach (var (key, value) in identity)
            {
                if (ReservedIdentityKeys.Contains(key) || userObject.ContainsKey(key))
                    continue;
                userObject[key] = value;
            }
        }
        return response;
    }

    /// <summary>
    /// Identity keys consumed by the exchange itself (or too dangerous to
    /// echo); everything else passes through to the response's user object.
    /// </summary>
    private static readonly HashSet<string> ReservedIdentityKeys = new(StringComparer.Ordinal)
    {
        "id", "email", "role", "tenant_id", "display_name", "password_hash", "password"
    };

    /// <summary>
    /// Rename source-identity keys to Shape's canonical claim names —
    /// e.g. <c>OrganizationId → tenant_id</c>. Unmapped keys pass through;
    /// a mapping never overwrites an already-present canonical key.
    /// </summary>
    private static Dictionary<string, object?> ApplyClaimMapping(
        Dictionary<string, object?> identity, Dictionary<string, string>? mapping)
    {
        if (mapping is not { Count: > 0 })
            return identity;

        var mapped = new Dictionary<string, object?>(identity);
        foreach (var (from, to) in mapping)
        {
            if (string.IsNullOrWhiteSpace(to) || !mapped.TryGetValue(from, out var value))
                continue;
            mapped.Remove(from);
            if (!mapped.ContainsKey(to) || mapped[to] == null)
                mapped[to] = value;
        }
        return mapped;
    }

    public Dictionary<string, object?> CreateUser(
        Dictionary<string, object?> payload,
        Dictionary<string, object?>? authContext = null)
    {
        var email = GetString(payload, "email")?.Trim() ?? "";
        var password = GetString(payload, "password") ?? "";
        var displayName = GetString(payload, "display_name")?.Trim() ?? "";
        var targetRole = GetString(payload, "role")?.Trim() ?? "user";
        var isActive = !payload.ContainsKey("is_active") || Convert.ToBoolean(payload["is_active"]);
        var tenancy = _authConfig.Tenancy;
        var callerRole = authContext?.GetValueOrDefault("role")?.ToString() ?? "";
        var callerTenant = ResolveTenantId(authContext);
        var callerUnscoped = tenancy != null && TenancyResolver.IsUnscopedRole(tenancy, callerRole);
        var requestedTenant = ResolveTenantId(payload);

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            throw new ApiException("AUTH_VALIDATION_ERROR", "Fields email and password are required.", 400);
        if (!IsValidEmail(email))
            throw new ApiException("AUTH_VALIDATION_ERROR", "Field email must be valid.", 400);
        if (password.Length < _authConfig.PasswordMinLength)
            throw new ApiException("AUTH_VALIDATION_ERROR",
                $"Password must have at least {_authConfig.PasswordMinLength} characters.", 400);

        if (string.IsNullOrEmpty(displayName))
        {
            var atIdx = email.IndexOf('@');
            displayName = atIdx > 0 ? email[..atIdx] : email;
        }

        var allowedRoles = _authConfig.AssignableRoles.Count > 0
            ? _authConfig.AssignableRoles
            : new List<string> { "user", "admin" };
        if (!allowedRoles.Contains(targetRole))
            throw new ApiException("AUTH_VALIDATION_ERROR", "Role is not allowed.", 400);

        if (tenancy != null && !callerUnscoped && callerRole.Length > 0
            && TenancyResolver.IsUnscopedRole(tenancy, targetRole))
        {
            throw new ApiException(
                "AUTH_FORBIDDEN",
                "Scoped admins cannot assign unscoped roles.",
                403,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Use a scoped role or an unscoped admin account for superadmin provisioning."
                });
        }

        object? tenantId = null;
        if (tenancy != null)
        {
            if (TenancyResolver.IsUnscopedRole(tenancy, targetRole))
            {
                // An unscoped-role (superadmin) user still needs a home tenant on
                // its row — users.tenant_id is NOT NULL under strict tenancy — so
                // stamp the requested/caller tenant rather than leaving it out of
                // the INSERT (which hits the NOT NULL constraint). The issued
                // token still carries tenant_id = null for unscoped roles (see
                // IssueTokenPair), so cross-tenant reach is unaffected.
                tenantId = requestedTenant ?? callerTenant;
                if (tenantId == null)
                {
                    throw new ApiException(
                        "VALIDATION_ERROR",
                        "tenant_id required when creating an unscoped-role user.",
                        400,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["detail"] = "TENANT_MISSING",
                            ["expected"] = "Supply `tenant_id` for the new account's home tenant."
                        });
                }
            }
            else if (callerUnscoped)
            {
                tenantId = requestedTenant ?? callerTenant;
                if (tenantId == null)
                {
                    throw new ApiException(
                        "VALIDATION_ERROR",
                        "tenant_id required for unscoped writes.",
                        400,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["detail"] = "TENANT_MISSING"
                        });
                }
            }
            else
            {
                if (callerTenant == null)
                    throw new ApiException("AUTH_INVALID", "Missing tenant_id claim.", 401);
                // A scoped admin may only create users in its own tenant. A
                // client-supplied tenant_id that differs is a hard error (plan
                // §2.6) — silently stamping the caller's tenant would create the
                // user somewhere the caller did not intend, with no signal.
                var requestedText = TenancyResolver.TenantValueAsString(requestedTenant);
                if (requestedText != null
                    && !string.Equals(requestedText, TenancyResolver.TenantValueAsString(callerTenant), StringComparison.Ordinal))
                {
                    throw new ApiException(
                        "VALIDATION_ERROR",
                        "tenant_id does not match the caller's tenant.",
                        400,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["detail"] = "TENANT_MISMATCH",
                            ["expected"] = callerTenant
                        });
                }
                tenantId = callerTenant;
            }

        }

        using var db = new Database(_dbConfig);
        var hash = BCrypt.Net.BCrypt.HashPassword(password);

        try
        {
            if (tenancy != null && tenantId != null)
                TenancyResolver.EnsureTenantActive(db, tenancy, tenantId);

            var data = new Dictionary<string, object?>
            {
                ["email"] = email,
                ["password_hash"] = hash,
                ["display_name"] = displayName,
                ["role"] = targetRole,
                ["is_active"] = isActive ? 1 : 0
            };
            if (tenancy != null && tenantId != null)
                data[TenantColumn()] = tenantId;

            var id = InsertRow(db,
                SafeTable(_authConfig.UsersTable, "users"),
                data);

            return new Dictionary<string, object?>
            {
                ["id"] = (int)id,
                ["email"] = email,
                ["display_name"] = displayName,
                ["role"] = targetRole,
                ["is_active"] = isActive,
                ["tenant_id"] = tenancy != null ? tenantId : null
            };
        }
        catch (ApiException ex) when (LooksLikeUniqueViolation(ex))
        {
            throw new ApiException("AUTH_USER_EXISTS", "User with this email already exists.", 409);
        }
    }

    public Dictionary<string, object?> ChangePassword(
        Dictionary<string, object?> payload,
        Dictionary<string, object?>? authContext = null)
    {
        EnsureLocalLoginEnabled();

        var email = GetString(payload, "email")?.Trim() ?? "";
        var newPassword = GetString(payload, "new_password") ?? "";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(newPassword))
            throw new ApiException("AUTH_VALIDATION_ERROR",
                "Fields email and new_password are required.", 400);
        if (!IsValidEmail(email))
            throw new ApiException("AUTH_VALIDATION_ERROR", "Field email must be valid.", 400);
        if (newPassword.Length < _authConfig.PasswordMinLength)
            throw new ApiException("AUTH_VALIDATION_ERROR",
                $"Password must have at least {_authConfig.PasswordMinLength} characters.", 400);

        using var db = new Database(_dbConfig);

        var user = FindUserByEmail(db, email, authContext)
            ?? throw new ApiException("AUTH_USER_NOT_FOUND", "User not found.", 404);

        var userId = ToInt(user.GetValueOrDefault("id") ?? 0);
        if (userId <= 0)
            throw new ApiException("AUTH_CONFIG_ERROR", "Invalid users table data.", 500);

        var hash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        var usersTable = db.QuoteIdentifier(SafeTable(_authConfig.UsersTable, "users"));
        var tokensTable = db.QuoteIdentifier(SafeTable(_authConfig.RefreshTokensTable, "refresh_tokens"));

        db.BeginTransaction();
        try
        {
            db.Exec(
                $"UPDATE {usersTable} SET password_hash = @password_hash WHERE id = @id",
                new Dictionary<string, object?> { ["password_hash"] = hash, ["id"] = userId });

            var revoked = db.Exec(
                $"UPDATE {tokensTable} SET revoked_at = CURRENT_TIMESTAMP " +
                $"WHERE user_id = @user_id AND revoked_at IS NULL",
                new Dictionary<string, object?> { ["user_id"] = userId });

            db.Commit();

            return new Dictionary<string, object?>
            {
                ["updated"] = true,
                ["email"] = email,
                ["revoked_refresh_tokens"] = revoked
            };
        }
        catch
        {
            if (db.IsInTransaction) db.Rollback();
            throw;
        }
    }

    // ── Internals ─────────────────────────────────────────────────────

    /// <summary>
    /// Insert a row into an auth table (users / refresh_tokens) and return the new id.
    /// Routes through <see cref="Database.InsertWithManualPk"/> when
    /// <see cref="AuthConfig.PkAssignment"/> forces <c>manual</c> for the table —
    /// legacy schemas whose auth-table PKs are plain non-identity ints.
    /// </summary>
    private long InsertRow(Database db, string table, Dictionary<string, object?> data)
    {
        if (Metadata.PkStrategyResolver.IsForcedManual(_authConfig.PkAssignment, table))
            return db.InsertWithManualPk(table, "id", data);

        var cols = data.Keys.ToList();
        var sql = $"INSERT INTO {db.QuoteIdentifier(table)} " +
                  $"({string.Join(", ", cols.Select(db.QuoteIdentifier))}) " +
                  $"VALUES ({string.Join(", ", cols.Select(c => "@" + c))})";
        return db.InsertReturningId(sql, data);
    }

    private Dictionary<string, object?> IssueTokenPair(Database db, Dictionary<string, object?> user)
    {
        var refreshToken = GenerateRefreshToken();
        var refreshHash = ComputeSha256(refreshToken);
        var refreshTtl = GetRefreshTokenTtl();
        var expiresAt = DateTime.UtcNow.AddSeconds(refreshTtl).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        InsertRow(db,
            SafeTable(_authConfig.RefreshTokensTable, "refresh_tokens"),
            new Dictionary<string, object?>
            {
                ["user_id"] = ToInt(user["id"]),
                ["token_hash"] = refreshHash,
                ["expires_at"] = expiresAt
            });

        return BuildTokenResponse(user, refreshToken, refreshTtl);
    }

    private Dictionary<string, object?> BuildTokenResponse(
        Dictionary<string, object?> user, string? refreshToken, int refreshTtl)
    {
        var ttl = _authConfig.AccessTokenTtl;
        if (ttl < 60) ttl = 60;
        var tenancy = _authConfig.Tenancy;
        var role = user.GetValueOrDefault("role")?.ToString() ?? "user";
        var tenantId = ResolveTenantId(user);
        if (tenancy != null && !TenancyResolver.IsUnscopedRole(tenancy, role) && tenantId == null)
            throw new ApiException("AUTH_CONFIG_ERROR", "Invalid users table data.", 500);

        // Local users carry int PKs; exchanged identities may carry the app's
        // native id type (e.g. ASP.NET Identity Guids) — keep non-numeric ids
        // verbatim instead of collapsing them to 0 through an int cast.
        var idValue = user.GetValueOrDefault("id");
        var idInt = ToInt(idValue ?? 0);
        var idText = idInt > 0
            ? idInt.ToString(CultureInfo.InvariantCulture)
            : idValue?.ToString() ?? "0";

        var claims = new Dictionary<string, object?>
        {
            ["sub"] = idText,
            ["email"] = user.GetValueOrDefault("email")?.ToString() ?? "",
            ["role"] = role,
            ["tenant_id"] = tenancy != null ? (TenancyResolver.IsUnscopedRole(tenancy, role) ? null : tenantId) : null
        };

        // One claim per row-scope dimension (ROW-SCOPES-PLAN §2.2) — server-
        // issued only; an unscoped role gets an explicit null claim.
        if (_authConfig.Scopes is { Count: > 0 } scopeConfigs)
        {
            foreach (var (column, scope) in scopeConfigs)
            {
                var claimKey = ScopeSpec.ClaimKeyFor(column, scope);
                if (scope.UnscopedRoles.Contains(role, StringComparer.Ordinal))
                {
                    claims[claimKey] = null;
                    continue;
                }

                var value = user.GetValueOrDefault(claimKey);
                if (scope.Multi && value != null && value is not IList<object?>)
                    value = new List<object?> { value };
                claims[claimKey] = value;
            }
        }

        var displayName = user.GetValueOrDefault("display_name")?.ToString() ?? "";
        if (!string.IsNullOrEmpty(displayName))
            claims["name"] = displayName;

        var accessToken = _issuer.Issue(claims, ttl);

        var response = new Dictionary<string, object?>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = ttl
        };
        // Null on the stateless auth.exchange path — the client re-exchanges
        // its external token instead of rotating a Shape refresh token.
        if (refreshToken != null)
        {
            response["refresh_token"] = refreshToken;
            response["refresh_token_expires_in"] = refreshTtl;
        }
        response["user"] = new Dictionary<string, object?>
        {
            ["id"] = idInt > 0 ? idInt : idValue,
            ["email"] = user.GetValueOrDefault("email")?.ToString() ?? "",
            ["display_name"] = displayName,
            ["role"] = role,
            ["tenant_id"] = claims["tenant_id"]
        };
        return response;
    }

    private Dictionary<string, object?>? FindUserByEmail(Database db, string email, Dictionary<string, object?>? authContext = null)
    {
        var usersTable = db.QuoteIdentifier(SafeTable(_authConfig.UsersTable, "users"));
        // Select the *configured* discriminator column, aliased to the canonical
        // tenant_id key every downstream consumer (claims, EnsureTenantActive)
        // reads — a renamed column (organization_id, …) must not break auth.
        var tenantSelect = _authConfig.Tenancy != null ? $", {TenantColumn()} AS tenant_id" : "";
        // Row scopes: one aliased select per dimension whose value lives on the
        // users row (claim_table scopes load their memberships separately).
        var scopeSelect = string.Concat(ScopeColumnSelects());
        var sql = $"SELECT {db.TopClause(1)}id, email, password_hash, display_name, role, is_active{tenantSelect}{scopeSelect} FROM {usersTable} WHERE email = @email";
        var parameters = new Dictionary<string, object?> { ["email"] = email };
        if (_authConfig.Tenancy != null && authContext != null && !TenancyResolver.IsUnscopedRole(_authConfig.Tenancy, authContext.GetValueOrDefault("role")?.ToString() ?? ""))
        {
            var tenantId = ResolveTenantId(authContext);
            if (tenantId == null)
                throw new ApiException("AUTH_INVALID", "Missing tenant_id claim.", 401);
            sql += $" AND {TenantColumn()} = @tenant_id";
            parameters["tenant_id"] = tenantId;
        }

        var result = db.Run(
            sql + db.RowLimitSuffix(1),
            parameters);

        if (result.Rows.Count == 0) return null;
        return ToAssoc(result.Columns, result.Rows[0]);
    }

    private static Dictionary<string, object?> ToAssoc(List<string> columns, object?[] row)
    {
        var assoc = new Dictionary<string, object?>();
        for (var i = 0; i < columns.Count; i++)
            assoc[columns[i]] = i < row.Length ? row[i] : null;
        return assoc;
    }

    private static object? ResolveTenantId(Dictionary<string, object?>? values)
    {
        if (values == null)
            return null;
        if (!values.TryGetValue("tenant_id", out var tenantValue) || tenantValue is null)
            return null;
        return TenancyResolver.TenantValueAsString(tenantValue) is null ? null : tenantValue;
    }

    private static bool LooksLikeUniqueViolation(ApiException ex)
    {
        if (ex.Details == null) return false;
        var sqlstate = ex.Details.GetValueOrDefault("sqlstate")?.ToString() ?? "";
        var code = ex.Details.GetValueOrDefault("driver_error_code")?.ToString() ?? "";
        // 23000 — generic SQLSTATE for integrity-constraint violation; MySQL 1062;
        // SQLite "19" (SQLITE_CONSTRAINT); Postgres SQLSTATE 23505. SQL Server has no
        // SQLSTATE — its unique-violation error numbers are 2627 (PK/unique constraint)
        // and 2601 (unique index).
        if (sqlstate is "23000" or "23505") return true;
        if (code is "1062" or "2627" or "2601") return true;
        if (ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Aliased SELECT fragments for the dimension values carried on the users
    /// row (one <c>, [prefix]&lt;column&gt; AS &lt;claim&gt;</c> per scope whose
    /// source is <c>users.&lt;column&gt;</c>; membership-table scopes load
    /// separately). Column and claim names are validated identifiers — same
    /// literal-interpolation discipline as <see cref="TenantColumn"/>.
    /// </summary>
    private IEnumerable<string> ScopeColumnSelects(string prefix = "")
    {
        if (_authConfig.Scopes is not { Count: > 0 } scopes)
            yield break;

        foreach (var (column, scope) in scopes)
        {
            if (scope.ClaimTable != null)
                continue;
            yield return $", {prefix}{SafeColumn(column)} AS {SafeColumn(ScopeSpec.ClaimKeyFor(column, scope))}";
        }
    }

    /// <summary>Claim keys for scopes whose value rides on the users row.</summary>
    private IEnumerable<string> ScopeClaimKeys()
    {
        if (_authConfig.Scopes is not { Count: > 0 } scopes)
            yield break;

        foreach (var (column, scope) in scopes)
        {
            if (scope.ClaimTable != null)
                continue;
            yield return ScopeSpec.ClaimKeyFor(column, scope);
        }
    }

    /// <summary>
    /// Populate the per-dimension claim values on the user dictionary before
    /// token issuance: a <c>claim_table</c> scope runs one indexed SELECT for
    /// the membership list; a <c>multi</c> scope without a membership table
    /// wraps the single users-row value into a one-element list.
    /// </summary>
    private void AttachScopeClaims(Database db, Dictionary<string, object?> user)
    {
        if (_authConfig.Scopes is not { Count: > 0 } scopes)
            return;

        foreach (var (column, scope) in scopes)
        {
            var claimKey = ScopeSpec.ClaimKeyFor(column, scope);
            if (scope.ClaimTable != null)
            {
                var membershipTable = db.QuoteIdentifier(SafeTable(scope.ClaimTable.Table, scope.ClaimTable.Table));
                var result = db.Run(
                    $"SELECT {SafeColumn(column)} FROM {membershipTable} " +
                    $"WHERE {SafeColumn(scope.ClaimTable.UserColumn)} = @user_id",
                    new Dictionary<string, object?> { ["user_id"] = ToInt(user.GetValueOrDefault("id") ?? 0) });
                user[claimKey] = result.Rows
                    .Select(r => r.Length > 0 ? r[0] : null)
                    .Where(v => v != null)
                    .ToList();
            }
            else if (scope.Multi && user.GetValueOrDefault(claimKey) is { } single and not IList<object?>)
            {
                user[claimKey] = new List<object?> { single };
            }
        }
    }

    /// <summary>
    /// The configured tenancy discriminator column, validated as a plain SQL
    /// identifier (it is interpolated into auth SQL). Defaults to tenant_id —
    /// the auth path must keep working when an app renames the column.
    /// </summary>
    private string TenantColumn()
    {
        var col = _authConfig.Tenancy?.Column ?? "tenant_id";
        if (string.IsNullOrEmpty(col) || !Regex.IsMatch(col, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ApiException("AUTH_CONFIG_ERROR", "Invalid tenancy column name.", 500);
        return col;
    }

    /// <summary>
    /// Validate a scope column / claim identifier before interpolating it into
    /// auth SQL — the same discipline as <see cref="TenantColumn"/>.
    /// </summary>
    private static string SafeColumn(string column)
    {
        if (string.IsNullOrEmpty(column) || !Regex.IsMatch(column, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ApiException("AUTH_CONFIG_ERROR", "Invalid scope column or claim name.", 500);
        return column;
    }

    private string SafeTable(string table, string fallback)
    {
        if (!Regex.IsMatch(table, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ApiException("AUTH_CONFIG_ERROR", "Invalid table name.", 500);
        return string.IsNullOrEmpty(table) ? fallback : table;
    }

    private int GetRefreshTokenTtl()
    {
        var ttl = _authConfig.RefreshTokenTtl;
        return ttl < 300 ? 300 : ttl;
    }

    private static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsValidEmail(string email)
    {
        try { var addr = new System.Net.Mail.MailAddress(email); return addr.Address == email; }
        catch { return false; }
    }

    private static string? GetString(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        return val?.ToString();
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        short s => s,
        byte b => b,
        bool bb => bb ? 1 : 0,
        string str when int.TryParse(str, out var parsed) => parsed,
        _ => 0
    };

    private static DateTime? ParseDateTime(object? value)
    {
        if (value is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        if (value is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed;
        return null;
    }
}
