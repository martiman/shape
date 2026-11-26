using System.Text.Json;
using System.Text.Json.Serialization;
using Shape.Auth;
using Shape.Core;

namespace Shape.Config;

/// <summary>
/// Loads a <see cref="ShapeConfig"/> from a JSON file so the web host
/// (<c>Program.cs</c>) and the MCP CLI (<c>Shape.Mcp.Cli</c>) can share one
/// source of truth — masks, access, auth, db, secret tables, etc. The loader
/// covers everything that's pure data; <see cref="ShapeConfig.Validators"/>,
/// <see cref="ShapeConfig.Commands"/>, and <see cref="ShapeConfig.Uploads"/>
/// remain code-supplied (rule/handler delegates can't be expressed in JSON)
/// and are layered on top via the <c>overrides</c> parameter.
///
/// Mirrors the PHP <c>Shape\ShapeApp::fromDatabase($configPath)</c> entrypoint.
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = BuildOptions(includeAuthShorthand: true);

    private static JsonSerializerOptions BuildOptions(bool includeAuthShorthand)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        if (includeAuthShorthand)
            opts.Converters.Add(new AuthDtoConverter());
        return opts;
    }

    /// <summary>
    /// Read a JSON config file and produce a populated <see cref="ShapeConfig"/>.
    /// Optional <paramref name="overrides"/> supplies code-only fields
    /// (Validators / Commands / Uploads) and any property the JSON omits.
    /// JSON wins for properties it sets; overrides fill the rest.
    /// </summary>
    public static ShapeConfig Load(string configPath, ShapeConfig? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("Config path is required.", nameof(configPath));
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Shape config file not found: {configPath}", configPath);

        var json = File.ReadAllText(configPath);
        ConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ConfigDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ApiException(
                "CONFIG_INVALID",
                $"Failed to parse Shape config JSON at {configPath}: {ex.Message}",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["path"] = configPath,
                    ["hint"] = "The file must be valid JSON. Comments and trailing commas are allowed."
                });
        }
        if (dto == null)
            throw new ApiException("CONFIG_INVALID", $"Shape config at {configPath} parsed to null.", 500);

        var cfg = overrides ?? new ShapeConfig();
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory;
        ApplyDto(cfg, dto, configDir);
        return cfg;
    }

    private static void ApplyDto(ShapeConfig cfg, ConfigDto dto, string configDir)
    {
        if (!string.IsNullOrEmpty(dto.ServiceName)) cfg.ServiceName = dto.ServiceName!;
        if (!string.IsNullOrEmpty(dto.Endpoint)) cfg.Endpoint = dto.Endpoint!;

        if (dto.Db != null)
        {
            cfg.Db ??= new DatabaseConfig();
            // PDO-style DSN takes priority — when present, parse it and let
            // the translator pick the driver and build the native string.
            // Explicit `driver` / `connectionString` still override (operator escape hatch).
            if (!string.IsNullOrEmpty(dto.Db.Dsn))
            {
                var (driver, conn) = DsnTranslator.Translate(dto.Db.Dsn!, dto.Db.User, dto.Db.Pass);
                cfg.Db.Driver = driver;
                cfg.Db.ConnectionString = conn;
            }
            if (!string.IsNullOrEmpty(dto.Db.Driver)) cfg.Db.Driver = dto.Db.Driver!;
            if (!string.IsNullOrEmpty(dto.Db.ConnectionString)) cfg.Db.ConnectionString = dto.Db.ConnectionString!;
        }

        if (dto.Auth != null) cfg.Auth = MapAuth(dto.Auth, cfg.Auth);

        if (dto.Masks != null)
        {
            cfg.Masks ??= new();
            foreach (var (name, mask) in dto.Masks)
                cfg.Masks[name] = MapMask(name, mask, configDir);
        }

        if (dto.Access != null) cfg.Access = dto.Access;
        if (dto.AccessByRole != null) cfg.AccessByRole = dto.AccessByRole;
        if (dto.ManifestRoles != null) cfg.ManifestRoles = dto.ManifestRoles;
        if (!string.IsNullOrWhiteSpace(dto.Security)) cfg.Security = dto.Security;
        if (dto.DocsUi.HasValue) cfg.DocsUi = dto.DocsUi.Value;
        if (!string.IsNullOrWhiteSpace(dto.DocsUiAssetUrl)) cfg.DocsUiAssetUrl = dto.DocsUiAssetUrl;
        if (dto.SecretTables != null) cfg.SecretTables = dto.SecretTables;
        if (dto.IncludeTables != null) cfg.IncludeTables = dto.IncludeTables;
        if (dto.ExcludeTables != null) cfg.ExcludeTables = dto.ExcludeTables;
        if (dto.Tenancy.HasValue) cfg.Tenancy = ReadTenancy(dto.Tenancy.Value);
        if (dto.Scopes.HasValue) cfg.Scopes = ReadScopes(dto.Scopes.Value);
        if (dto.AdminMasks.HasValue) cfg.AdminMasks = dto.AdminMasks.Value;
        if (dto.Audit.HasValue) cfg.Audit = dto.Audit.Value;
        if (dto.AutoValidators.HasValue) cfg.AutoValidators = dto.AutoValidators.Value;
        if (dto.DefaultListLimit.HasValue) cfg.DefaultListLimit = dto.DefaultListLimit.Value;

        // `pkAssignment` (or PHP-parity `pk_assignment`): object map, or a plain
        // string shorthand meaning the database-wide default ({"*": value}).
        var pkAssignment = dto.PkAssignment ?? dto.PkAssignmentSnakeCase;
        if (pkAssignment is { } pkElement && pkElement.ValueKind != JsonValueKind.Null)
            cfg.PkAssignment = ReadPkAssignment(pkElement);
    }

    private static Dictionary<string, string> ReadPkAssignment(JsonElement element)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                map["*"] = element.GetString() ?? "";
                break;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        throw PkAssignmentInvalid($"table '{prop.Name}' has a non-string value");
                    map[prop.Name] = prop.Value.GetString() ?? "";
                }
                break;
            default:
                throw PkAssignmentInvalid($"unsupported JSON type {element.ValueKind}");
        }

        // Fail at config load, not at first insert. PkStrategyResolver re-validates
        // for code-supplied configs that never pass through this loader.
        Metadata.PkStrategyResolver.Validate(map);
        return map;
    }

    private static ApiException PkAssignmentInvalid(string reason) => new(
        "CONFIG_INVALID",
        $"Config key `pkAssignment` is invalid: {reason}.",
        500,
        null,
        new Dictionary<string, object?>
        {
            ["expected"] = "\"auto\" | \"db\" | \"manual\", or an object like {\"*\": \"auto\", \"invoices\": \"manual\"}"
        });

    private static AuthConfig MapAuth(AuthDto dto, AuthConfig existing)
    {
        var auth = existing;
        if (!string.IsNullOrEmpty(dto.Driver)) auth.Driver = dto.Driver!;
        if (dto.JwtSecret != null) auth.JwtSecret = dto.JwtSecret;
        if (dto.JwtIssuer != null) auth.JwtIssuer = dto.JwtIssuer;
        if (dto.JwtAudience != null) auth.JwtAudience = dto.JwtAudience;
        if (!string.IsNullOrEmpty(dto.UsersTable)) auth.UsersTable = dto.UsersTable!;
        if (!string.IsNullOrEmpty(dto.RefreshTokensTable)) auth.RefreshTokensTable = dto.RefreshTokensTable!;
        if (dto.AccessTokenTtl.HasValue) auth.AccessTokenTtl = dto.AccessTokenTtl.Value;
        if (dto.RefreshTokenTtl.HasValue) auth.RefreshTokenTtl = dto.RefreshTokenTtl.Value;
        if (dto.PasswordMinLength.HasValue) auth.PasswordMinLength = dto.PasswordMinLength.Value;
        if (dto.AssignableRoles != null) auth.AssignableRoles = dto.AssignableRoles;
        if (dto.LocalLoginDisabled.HasValue) auth.LocalLoginDisabled = dto.LocalLoginDisabled.Value;
        if (dto.Oidc != null) auth.Oidc = MapOidc(dto.Oidc);
        return auth;
    }

    private static OidcConfig MapOidc(OidcDto dto) => new()
    {
        Preset = dto.Preset,
        TenantId = dto.TenantId,
        Domain = dto.Domain,
        Region = dto.Region,
        UserPoolId = dto.UserPoolId,
        JwksUri = dto.JwksUri,
        Issuer = dto.Issuer,
        Audience = dto.Audience,
        Algorithms = dto.Algorithms,
        IdClaim = dto.IdClaim ?? "sub",
        EmailClaim = dto.EmailClaim ?? "email",
        RoleClaim = dto.RoleClaim ?? "role",
        RoleMapping = dto.RoleMapping,
        JwksCacheTtl = dto.JwksCacheTtl ?? 3600,
        JwksCacheDir = dto.JwksCacheDir,
        UsersTable = dto.UsersTable ?? "users",
        UsersLookupColumn = dto.UsersLookupColumn, // null = auto-detect (see OidcConfig docs)
        AutoProvision = dto.AutoProvision ?? false,
        AutoProvisionRole = dto.AutoProvisionRole ?? "user"
    };

    private static MaskConfig MapMask(string maskName, MaskDto dto, string configDir)
    {
        var sql = ResolveSql(maskName, dto, configDir);
        var mask = new MaskConfig
        {
            Sql = sql,
            Roles = dto.Roles,
            Writable = dto.Writable ?? false,
            SoftDelete = dto.SoftDelete,
            Description = dto.Description,
            CacheControl = dto.CacheControl,
            DefaultLimit = dto.DefaultLimit ?? 0
        };
        if (dto.Params != null)
        {
            mask.Params = dto.Params.ToDictionary(
                kv => kv.Key,
                kv => new ParamSchema
                {
                    Type = kv.Value.Type ?? "mixed",
                    Required = kv.Value.Required ?? false
                });
        }
        if (dto.Audit != null) mask.Audit = dto.Audit;
        if (dto.TenantScope.HasValue)
            mask.TenantScope = ParamResolver.UnwrapJsonElement(dto.TenantScope.Value);
        var rowScopes = dto.RowScopes ?? dto.RowScopesSnakeCase;
        if (rowScopes is { } rowScopesElement && rowScopesElement.ValueKind != JsonValueKind.Null)
            mask.RowScopes = ReadMaskRowScopes(maskName, rowScopesElement);
        if (!string.IsNullOrEmpty(dto.SqlUnscoped))
            mask.SqlUnscoped = dto.SqlUnscoped;
        if (dto.RolesByAction is { Count: > 0 })
            mask.RolesByAction = dto.RolesByAction;
        return mask;
    }

    private static TenancyConfig? ReadTenancy(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.Object:
                var config = new TenancyConfig();
                if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("table", out var table) && table.ValueKind == JsonValueKind.String)
                        config.Table = table.GetString() ?? config.Table;
                    if (element.TryGetProperty("column", out var column) && column.ValueKind == JsonValueKind.String)
                        config.Column = column.GetString() ?? config.Column;
                    if (element.TryGetProperty("unscoped_roles", out var unscopedRoles) && unscopedRoles.ValueKind == JsonValueKind.Array)
                        config.UnscopedRoles = unscopedRoles.EnumerateArray()
                            .Select(v => v.GetString())
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v!)
                            .ToList();
                    if (element.TryGetProperty("enforce", out var enforce) && enforce.ValueKind == JsonValueKind.String)
                        config.Enforce = enforce.GetString() ?? config.Enforce;
                    if (element.TryGetProperty("global_tables", out var globals) && globals.ValueKind == JsonValueKind.Array)
                        config.GlobalTables = globals.EnumerateArray()
                            .Select(v => v.GetString())
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v!)
                            .ToList();
                    if (element.TryGetProperty("check_active_per_request", out var checkActive) && checkActive.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        config.CheckActivePerRequest = checkActive.GetBoolean();
                    if (element.TryGetProperty("validate_fks", out var validateFks) && validateFks.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        config.ValidateFks = validateFks.GetBoolean();
                    if (element.TryGetProperty("oidc_tenant_claim", out var tenantClaim) && tenantClaim.ValueKind == JsonValueKind.String)
                        config.OidcTenantClaim = tenantClaim.GetString();
                    if (element.TryGetProperty("oidc_auto_provision_tenant", out var autoProv) && autoProv.ValueKind != JsonValueKind.Undefined)
                        config.OidcAutoProvisionTenant = autoProv;
                }

                ValidateTenancy(config);
                return config;
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return null;
            default:
                throw new ApiException(
                    "CONFIG_INVALID",
                    $"Config key `tenancy` is invalid JSON type {element.ValueKind}.",
                    500,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = "true, false, or an object with tenancy settings"
                    });
        }
    }

    private static void ValidateTenancy(TenancyConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Table))
            throw TenancyInvalid("table");
        if (string.IsNullOrWhiteSpace(cfg.Column))
            throw TenancyInvalid("column");

        var enforce = cfg.Enforce.ToLowerInvariant();
        if (enforce is not ("strict" or "relaxed"))
            throw TenancyInvalid("enforce");
    }

    /// <summary>
    /// Parse the <c>scopes</c> config block (ROW-SCOPES-PLAN §2.1). Keys are
    /// discriminator column names; values are objects with snake_case keys
    /// identical to the PHP runtime: <c>claim</c>, <c>unscoped_roles</c>,
    /// <c>multi</c>, <c>claim_table</c>, <c>nullable</c>,
    /// <c>oidc_auto_provision_value</c>.
    /// </summary>
    private static Dictionary<string, ScopeConfig>? ReadScopes(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Object:
                break;
            default:
                throw new ApiException(
                    "CONFIG_INVALID",
                    $"Config key `scopes` is invalid JSON type {element.ValueKind}.",
                    500,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = "an object keyed by discriminator column, e.g. {\"department_id\": {\"unscoped_roles\": [\"admin\"]}}"
                    });
        }

        var scopes = new Dictionary<string, ScopeConfig>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            var column = prop.Name;
            var scope = new ScopeConfig();
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.True:
                    break; // all defaults
                case JsonValueKind.Object:
                    if (prop.Value.TryGetProperty("claim", out var claim) && claim.ValueKind == JsonValueKind.String)
                        scope.Claim = claim.GetString();
                    if (prop.Value.TryGetProperty("unscoped_roles", out var unscopedRoles) && unscopedRoles.ValueKind == JsonValueKind.Array)
                        scope.UnscopedRoles = unscopedRoles.EnumerateArray()
                            .Select(v => v.GetString())
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v!)
                            .ToList();
                    if (prop.Value.TryGetProperty("multi", out var multi) && multi.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        scope.Multi = multi.GetBoolean();
                    if (prop.Value.TryGetProperty("self", out var self) && self.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        scope.Self = self.GetBoolean();
                    if (prop.Value.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.String)
                        scope.Nullable = nullable.GetString() ?? scope.Nullable;
                    if (prop.Value.TryGetProperty("claim_table", out var claimTable) && claimTable.ValueKind == JsonValueKind.Object)
                    {
                        var ct = new ClaimTableConfig();
                        if (claimTable.TryGetProperty("table", out var table) && table.ValueKind == JsonValueKind.String)
                            ct.Table = table.GetString() ?? "";
                        if (claimTable.TryGetProperty("user_column", out var userColumn) && userColumn.ValueKind == JsonValueKind.String)
                            ct.UserColumn = userColumn.GetString() ?? ct.UserColumn;
                        scope.ClaimTable = ct;
                    }
                    if (prop.Value.TryGetProperty("oidc_auto_provision_value", out var autoProv) && autoProv.ValueKind != JsonValueKind.Undefined)
                        scope.OidcAutoProvisionValue = autoProv;
                    break;
                default:
                    throw ScopeInvalid(column, "(entry)", "an object with scope settings (or true for all defaults)");
            }

            Metadata.TenancyResolver.ValidateScope(column, scope);
            scopes[column] = scope;
        }

        return scopes.Count > 0 ? scopes : null;
    }

    /// <summary>
    /// Parse a mask's <c>row_scopes</c> key: <c>false</c> = opt-out; array of
    /// column names = declaration (normalized to the full carrier at boot);
    /// object <c>{column: {multi}}</c> = the cross-runtime carrier form.
    /// </summary>
    private static object? ReadMaskRowScopes(string maskName, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray()
                    .Select(v => v.GetString())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!)
                    .ToList();
            case JsonValueKind.Object:
                var map = new Dictionary<string, RowScopeMeta>(StringComparer.Ordinal);
                foreach (var prop in element.EnumerateObject())
                {
                    var meta = new RowScopeMeta();
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("multi", out var multi)
                        && multi.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        meta.Multi = multi.GetBoolean();
                    }
                    map[prop.Name] = meta;
                }
                return map;
            default:
                throw new ApiException(
                    "CONFIG_INVALID",
                    $"Mask '{maskName}' has an invalid `row_scopes` value (JSON type {element.ValueKind}).",
                    500,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["mask"] = maskName,
                        ["expected"] = "a list of scope columns, false (opt-out), or {column: {multi}}"
                    });
        }
    }

    private static ApiException ScopeInvalid(string column, string field, string expected) => new(
        "CONFIG_INVALID",
        $"Config key `scopes.{column}.{field}` is invalid.",
        500,
        null,
        new Dictionary<string, object?>
        {
            ["expected"] = expected
        });

    private static ApiException TenancyInvalid(string field) => new(
        "CONFIG_INVALID",
        $"Config key `tenancy.{field}` is invalid.",
        500,
        null,
        new Dictionary<string, object?>
        {
            ["expected"] = field == "enforce"
                ? "one of: 'strict', 'relaxed'"
                : "non-empty string"
        });

    /// <summary>
    /// Resolve the mask body from either inline <c>sql</c> or an external
    /// <c>sqlFile</c>. Resolution rules:
    /// <list type="bullet">
    ///   <item>Both set → <c>CONFIG_INVALID</c> (ambiguous source of truth).</item>
    ///   <item>Neither set → returns <c>""</c> (mask without SQL is an
    ///   <c>MaskRegistry</c> concern, not a config one — the existing
    ///   <c>IsNullOrWhiteSpace(mask.Sql)</c> filter still skips empties).</item>
    ///   <item><c>sqlFile</c> path is resolved relative to the config file's
    ///   directory; absolute paths and <c>~/</c>-style paths are rejected so
    ///   configs travel as a self-contained folder.</item>
    ///   <item>Missing file → <c>CONFIG_INVALID</c> with the resolved path.</item>
    /// </list>
    /// </summary>
    private static string ResolveSql(string maskName, MaskDto dto, string configDir)
    {
        var hasInline = !string.IsNullOrWhiteSpace(dto.Sql);
        var hasFile = !string.IsNullOrWhiteSpace(dto.SqlFile);

        if (hasInline && hasFile)
        {
            throw new ApiException(
                "CONFIG_INVALID",
                $"Mask '{maskName}' specifies both `sql` and `sqlFile`.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["mask"] = maskName,
                    ["hint"] = "Specify exactly one of `sql` (inline body) or `sqlFile` (path to a .sql file relative to the config directory)."
                });
        }

        if (hasInline) return dto.Sql!;
        if (!hasFile) return "";

        var rel = dto.SqlFile!;
        if (Path.IsPathRooted(rel) || rel.StartsWith("~", StringComparison.Ordinal))
        {
            throw new ApiException(
                "CONFIG_INVALID",
                $"Mask '{maskName}' has an absolute or home-relative `sqlFile` path: {rel}.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["mask"] = maskName,
                    ["sqlFile"] = rel,
                    ["hint"] = "Use a path relative to the config file's directory so the config + masks travel as a folder."
                });
        }

        var resolved = Path.GetFullPath(Path.Combine(configDir, rel));
        if (!File.Exists(resolved))
        {
            throw new ApiException(
                "CONFIG_INVALID",
                $"Mask '{maskName}' references missing `sqlFile`: {resolved}.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["mask"] = maskName,
                    ["sqlFile"] = rel,
                    ["resolvedPath"] = resolved,
                    ["hint"] = "Path is resolved relative to the directory containing the JSON config file."
                });
        }

        return File.ReadAllText(resolved);
    }

    // ── DTOs ───────────────────────────────────────────────────────────

    private sealed class ConfigDto
    {
        public string? ServiceName { get; set; }
        public string? Endpoint { get; set; }
        public DbDto? Db { get; set; }
        public AuthDto? Auth { get; set; }
        public Dictionary<string, MaskDto>? Masks { get; set; }
        public JsonElement? Tenancy { get; set; }

        /// <summary>Row scopes by dimension — parsed via <see cref="ReadScopes"/>.</summary>
        public JsonElement? Scopes { get; set; }
        public Dictionary<string, List<string>>? Access { get; set; }
        public Dictionary<string, List<string>>? AccessByRole { get; set; }
        public List<string>? ManifestRoles { get; set; }
        public string? Security { get; set; }
        public bool? DocsUi { get; set; }
        public string? DocsUiAssetUrl { get; set; }
        public bool? AdminMasks { get; set; }
        public bool? Audit { get; set; }
        public List<string>? SecretTables { get; set; }
        public List<string>? IncludeTables { get; set; }
        public List<string>? ExcludeTables { get; set; }
        public bool? AutoValidators { get; set; }
        public int? DefaultListLimit { get; set; }

        /// <summary>String shorthand or table→mode object — normalized in <see cref="ApplyDto"/>.</summary>
        public JsonElement? PkAssignment { get; set; }

        /// <summary>PHP-parity spelling of <see cref="PkAssignment"/> so one JSON config can boot both stacks.</summary>
        [JsonPropertyName("pk_assignment")]
        public JsonElement? PkAssignmentSnakeCase { get; set; }
    }

    private sealed class DbDto
    {
        public string? Driver { get; set; }
        public string? ConnectionString { get; set; }

        /// <summary>PHP/PDO-style DSN. When present, parsed via <see cref="DsnTranslator"/>.</summary>
        public string? Dsn { get; set; }
        public string? User { get; set; }
        public string? Pass { get; set; }
    }

    /// <summary>
    /// Coerces a JSON shorthand into a full <see cref="AuthDto"/>:
    /// <list type="bullet">
    ///   <item><c>"auth": "jwt"</c> → <c>{ Driver = "jwt" }</c></item>
    ///   <item><c>"auth": true</c> → <c>{ Driver = "jwt" }</c> (idiomatic on/off toggle)</item>
    ///   <item><c>"auth": false</c> → <c>{ Driver = "none" }</c></item>
    ///   <item><c>"auth": { ... }</c> → standard object deserialization</item>
    /// </list>
    /// </summary>
    private static readonly JsonSerializerOptions BareAuthOptions = BuildOptions(includeAuthShorthand: false);

    private sealed class AuthDtoConverter : JsonConverter<AuthDto>
    {
        public override AuthDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var s = reader.GetString();
                    return new AuthDto { Driver = string.IsNullOrWhiteSpace(s) ? "none" : s };
                case JsonTokenType.True:
                    return new AuthDto { Driver = "jwt" };
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    return new AuthDto { Driver = "none" };
                case JsonTokenType.StartObject:
                    // Use an options bundle that omits this converter; otherwise the
                    // recursive call would re-enter Read and stack-overflow.
                    return JsonSerializer.Deserialize<AuthDto>(ref reader, BareAuthOptions)
                           ?? new AuthDto();
                default:
                    throw new JsonException($"Unsupported JSON token for `auth`: {reader.TokenType}");
            }
        }

        public override void Write(Utf8JsonWriter writer, AuthDto value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, BareAuthOptions);
    }

    private sealed class AuthDto
    {
        public string? Driver { get; set; }
        public string? JwtSecret { get; set; }
        public string? JwtIssuer { get; set; }
        public string? JwtAudience { get; set; }
        public string? UsersTable { get; set; }
        public string? RefreshTokensTable { get; set; }
        public int? AccessTokenTtl { get; set; }
        public int? RefreshTokenTtl { get; set; }
        public int? PasswordMinLength { get; set; }
        public List<string>? AssignableRoles { get; set; }
        public bool? LocalLoginDisabled { get; set; }
        public OidcDto? Oidc { get; set; }
    }

    private sealed class OidcDto
    {
        public string? Preset { get; set; }
        public string? TenantId { get; set; }
        public string? Domain { get; set; }
        public string? Region { get; set; }
        public string? UserPoolId { get; set; }
        public string? JwksUri { get; set; }
        public string? Issuer { get; set; }
        public string? Audience { get; set; }
        public List<string>? Algorithms { get; set; }
        public string? IdClaim { get; set; }
        public string? EmailClaim { get; set; }
        public string? RoleClaim { get; set; }
        public Dictionary<string, string>? RoleMapping { get; set; }
        public int? JwksCacheTtl { get; set; }
        public string? JwksCacheDir { get; set; }
        public string? UsersTable { get; set; }
        public string? UsersLookupColumn { get; set; }
        public bool? AutoProvision { get; set; }
        public string? AutoProvisionRole { get; set; }
    }

    private sealed class MaskDto
    {
        public string? Sql { get; set; }

        /// <summary>
        /// Path to a <c>.sql</c> file holding this mask's body, resolved
        /// relative to the directory containing the loaded JSON config file.
        /// Mutually exclusive with <see cref="Sql"/>; absolute paths and
        /// <c>~/</c>-style paths are rejected with <c>CONFIG_INVALID</c>.
        /// </summary>
        public string? SqlFile { get; set; }
        public Dictionary<string, ParamDto>? Params { get; set; }
        public List<string>? Roles { get; set; }
        public bool? Writable { get; set; }
        public string? SoftDelete { get; set; }
        public string? Description { get; set; }
        public string? CacheControl { get; set; }
        public Dictionary<string, string>? Audit { get; set; }
        public JsonElement? TenantScope { get; set; }
        public string? SqlUnscoped { get; set; }
        public int? DefaultLimit { get; set; }
        public Dictionary<string, List<string>>? RolesByAction { get; set; }

        /// <summary>
        /// Row-scope declaration: a list of scope columns (contract check),
        /// <c>false</c> (opt-out), or the full <c>{column: {multi}}</c> carrier.
        /// Accepts <c>rowScopes</c> (case-insensitive default binding).
        /// </summary>
        public JsonElement? RowScopes { get; set; }

        /// <summary>PHP-parity spelling of <see cref="RowScopes"/> (<c>row_scopes</c>).</summary>
        [JsonPropertyName("row_scopes")]
        public JsonElement? RowScopesSnakeCase { get; set; }
    }

    private sealed class ParamDto
    {
        public string? Type { get; set; }
        public bool? Required { get; set; }
    }
}
