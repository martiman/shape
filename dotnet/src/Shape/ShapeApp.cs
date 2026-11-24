using System.Reflection;
using Shape.Auth;
using Shape.Config;
using Shape.Core;
using Shape.Generator;
using Shape.Hosting;
using Shape.Metadata;

namespace Shape;

public static class ShapeApp
{
    public static ShapeConfig FromAssembly(Assembly assembly, ShapeConfig config)
    {
        JwtSecretMaterializer.Ensure(config.Auth, AppContext.BaseDirectory);
        var reader = new EntityReader();
        var entities = reader.ReadFromAssembly(assembly);
        return Build(config, entities, includeAdminMasks: false, auditLogTable: null);
    }

    public static ShapeConfig FromTypes(IEnumerable<Type> entityTypes, ShapeConfig config)
    {
        JwtSecretMaterializer.Ensure(config.Auth, AppContext.BaseDirectory);
        var reader = new EntityReader();
        var entities = reader.ReadTypes(entityTypes);
        return Build(config, entities, includeAdminMasks: false, auditLogTable: null);
    }

    /// <summary>
    /// Cold-boot probe: when OIDC is configured and <see cref="OidcConfig.UsersLookupColumn"/>
    /// is unset, inspect the users table for <c>oid</c> / <c>sub</c> /
    /// <c>azure_oid</c> / <c>auth0_id</c> / <c>external_id</c> and use the
    /// single match. Multi-match emits a stderr warning and falls back to
    /// <c>"oid"</c> so the operator can disambiguate explicitly. No match
    /// also falls back to <c>"oid"</c>.
    /// </summary>
    internal static void AutoDetectOidcLookupColumn(ShapeConfig config, DatabaseIntrospector introspector)
    {
        var oidc = config.Auth?.Oidc;
        if (oidc == null) return;
        if (!string.IsNullOrEmpty(oidc.UsersLookupColumn)) return;

        var usersTable = oidc.UsersTable;
        if (string.IsNullOrEmpty(usersTable)) return;

        List<ColumnInfo> columns;
        try { columns = introspector.DescribeColumns(usersTable); }
        catch { oidc.UsersLookupColumn = "oid"; return; }
        if (columns.Count == 0) { oidc.UsersLookupColumn = "oid"; return; }

        var candidates = new[] { "oid", "sub", "azure_oid", "auth0_id", "external_id" };
        var present = columns
            .Select(c => c.Name)
            .Where(n => candidates.Contains(n, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (present.Count == 1)
        {
            oidc.UsersLookupColumn = present[0];
            return;
        }

        if (present.Count > 1)
        {
            Console.Error.WriteLine(
                $"[Shape] Ambiguous OIDC users_lookup_column on '{usersTable}': found {string.Join(", ", present)}. " +
                "Set Oidc.UsersLookupColumn explicitly to disambiguate. Falling back to 'oid'.");
        }

        oidc.UsersLookupColumn = "oid";
    }

    /// <summary>
    /// Merge <see cref="ShapeConfig.Access"/> with <see cref="ShapeConfig.AccessByRole"/>
    /// (role-first shorthand) into a single entity-first map. Empty result means
    /// "no allowlist configured" — callers should fall through to today's
    /// behavior of generating masks for all entities.
    /// </summary>
    internal static Dictionary<string, List<string>> ResolveAccessMap(ShapeConfig config)
    {
        var merged = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (config.Access != null)
        {
            foreach (var (table, roles) in config.Access)
            {
                if (string.IsNullOrWhiteSpace(table)) continue;
                merged[table] = roles?.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList() ?? new List<string>();
            }
        }

        if (config.AccessByRole != null)
        {
            foreach (var (role, tables) in config.AccessByRole)
            {
                if (string.IsNullOrWhiteSpace(role) || tables == null) continue;
                foreach (var table in tables)
                {
                    if (string.IsNullOrWhiteSpace(table)) continue;
                    if (!merged.TryGetValue(table, out var rolesForTable))
                    {
                        rolesForTable = new List<string>();
                        merged[table] = rolesForTable;
                    }
                    if (!rolesForTable.Contains(role)) rolesForTable.Add(role);
                }
            }
        }

        if (config.Tenancy != null
            && config.Tenancy.UnscopedRoles.Count > 0
            && !merged.ContainsKey(config.Tenancy.Table))
        {
            merged[config.Tenancy.Table] = config.Tenancy.UnscopedRoles
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return merged;
    }

    /// <summary>
    /// Introspect the live database (mysql / pgsql / sqlite via Dapper) and
    /// build the <see cref="ShapeConfig"/> from its schema. Honors
    /// <see cref="ShapeConfig.IncludeTables"/> / <see cref="ShapeConfig.ExcludeTables"/>;
    /// merges <see cref="AdminMaskGenerator"/> output for <see cref="ShapeConfig.SecretTables"/>
    /// when <see cref="ShapeConfig.AdminMasks"/> is set; auto-detects an
    /// <c>audit_log</c> table for the <see cref="Shape.Audit.AuditLogger"/> wiring.
    /// </summary>
    /// <summary>
    /// Load a JSON config file and run <see cref="FromDatabase(ShapeConfig)"/>
    /// against it. Lets the web host and <c>Shape.Mcp.Cli</c> share one source
    /// of truth for masks / access / auth / db. Validators, Commands, and
    /// Uploads (which carry delegates) stay code-supplied via
    /// <paramref name="overrides"/>.
    /// </summary>
    public static ShapeConfig FromDatabase(string configPath, ShapeConfig? overrides = null)
    {
        var config = ConfigLoader.Load(configPath, overrides);
        // Anchor state files (jwt-secret, ...) to the directory holding the
        // config file — survives `cd` between dev and deploy invocations.
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory;
        JwtSecretMaterializer.Ensure(config.Auth, baseDir);
        return FromDatabase(config);
    }

    /// <summary>
    /// Load a JSON config and overlay conventional environment variables
    /// (<c>DB_DSN</c>, <c>DB_USER</c>, <c>DB_PASS</c>, <c>DB_NAME</c>,
    /// <c>CLOUD_SQL_INSTANCE</c>, <c>JWT_SECRET</c>) before introspecting
    /// the database. The standard PHP→C# port shape — JSON for declarative
    /// metadata, env for secrets and per-environment connection bits — in
    /// one call.
    /// </summary>
    /// <param name="configPath">Path to the JSON config file.</param>
    /// <param name="overrides">
    /// Optional code-supplied <see cref="ShapeConfig"/> for fields the JSON
    /// can't carry (Validators, Commands, Uploads). JSON wins for properties
    /// it sets; overrides fill the rest.
    /// </param>
    /// <param name="customize">
    /// Optional callback invoked after JSON load + env overlay but before
    /// database introspection. Escape hatch for app-specific tweaks (adding
    /// a Command lambda, mutating Mask roles, registering an Upload sink).
    /// </param>
    public static ShapeConfig FromEnv(
        string configPath,
        ShapeConfig? overrides = null,
        Action<ShapeConfig>? customize = null)
    {
        var config = ConfigLoader.Load(configPath, overrides);
        EnvLoader.ApplyEnv(config);
        customize?.Invoke(config);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory;
        JwtSecretMaterializer.Ensure(config.Auth, baseDir);
        return FromDatabase(config);
    }

    /// <summary>
    /// One-call host for thin-host apps: load JSON, overlay env, run
    /// either the stdio MCP transport (when <c>--mcp-stdio</c> is in
    /// <paramref name="args"/>) or a <see cref="Microsoft.AspNetCore.Builder.WebApplication"/>
    /// with Shape mapped at the configured endpoint. Apps that need custom
    /// routes / middleware / DI services can still use the manual
    /// <c>FromEnv</c> + <c>MapShape</c> pair; this is the 95%-case fast
    /// path.
    /// </summary>
    public static Task Run(
        string configPath,
        string[] args,
        Action<ShapeConfig>? customize = null,
        Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configureBuilder = null,
        Action<Microsoft.AspNetCore.Builder.WebApplication>? configureApp = null)
    {
        var config = FromEnv(configPath, overrides: null, customize: customize);
        return ShapeHost.RunAsync(config, args, configureBuilder, configureApp);
    }

    public static ShapeConfig FromDatabase(ShapeConfig config)
    {
        // Idempotent — no-ops when JwtSecret is already set (e.g. by the
        // string-overload above with a different baseDir).
        JwtSecretMaterializer.Ensure(config.Auth, AppContext.BaseDirectory);
        using var db = new Database(config.Db);
        var introspector = new DatabaseIntrospector(db);

        AutoDetectOidcLookupColumn(config, introspector);
        ValidateTenancyControlTables(config, introspector);

        var accessMap = ResolveAccessMap(config);

        // Introspect domain tables — exclude secret tables here so the secret
        // tables don't get auto-CRUD masks; they get admin-only masks below.
        var exclude = new List<string>(config.ExcludeTables ?? new());
        if (config.AdminMasks)
        {
            foreach (var t in config.SecretTables)
                if (!exclude.Contains(t)) exclude.Add(t);
        }

        // When an access allowlist is configured, narrow the catalog scan to
        // its keys (intersected with IncludeTables when both are set). Tables
        // not listed get no introspection cost and no generated masks.
        IReadOnlyList<string>? include = config.IncludeTables;
        if (accessMap.Count > 0)
        {
            var keys = accessMap.Keys.AsEnumerable();
            if (include != null) keys = keys.Intersect(include, StringComparer.Ordinal);
            include = keys.ToList();
        }

        var entities = introspector.Introspect(
            includeTables: include,
            excludeTables: exclude,
            autoValidators: config.AutoValidators,
            tenancy: config.Tenancy,
            scopes: config.Scopes);

        // DB-first introspection defaults Writable=false. When an access
        // allowlist is configured, listed tables are intended writable too —
        // operators putting a table in `Access` want both reads and writes.
        if (accessMap.Count > 0)
        {
            entities = entities.Select(e => accessMap.ContainsKey(e.Table)
                ? new EntityMeta
                {
                    ClrType = e.ClrType,
                    Table = e.Table,
                    Writable = true,
                    Fields = e.Fields,
                    ReadRoles = e.ReadRoles,
                    WriteRoles = e.WriteRoles,
                    WriteRolesByAction = e.WriteRolesByAction,
                    Description = e.Description,
                    WritableMode = e.WritableMode,
                    SoftDeleteColumn = e.SoftDeleteColumn,
                    AuditColumns = e.AuditColumns,
                    TenantColumn = e.TenantColumn,
                    ScopeColumns = e.ScopeColumns,
                    UniqueConstraints = e.UniqueConstraints,
                    Hooks = e.Hooks
                }
                : e).ToList();
        }

        // Detect audit_log table if Audit feature is on.
        string? auditLogTable = null;
        if (config.Audit)
        {
            var allTables = introspector.ListTables();
            if (allTables.Contains("audit_log"))
                auditLogTable = "audit_log";
        }

        // Generate admin masks for secret tables that exist in the DB.
        Dictionary<string, MaskConfig> adminMasks = new();
        if (config.AdminMasks)
        {
            var schemas = introspector.AsTableSchemas();
            adminMasks = AdminMaskGenerator.Generate(schemas, config.SecretTables, config.Db.Driver, config.Tenancy);
        }

        return Build(config, entities, includeAdminMasks: true, auditLogTable, adminMasks);
    }

    private static ShapeConfig Build(
        ShapeConfig config,
        List<EntityMeta> entities,
        bool includeAdminMasks,
        string? auditLogTable,
        Dictionary<string, MaskConfig>? adminMasks = null)
    {
        config.Auth.Tenancy = config.Tenancy;
        config.Auth.Scopes = config.Scopes;
        ValidateOidcConfig(config);
        ValidateOidcTenancy(config);

        var accessMap = ResolveAccessMap(config);
        var tenancyWarnings = TenancyResolver.Apply(config, entities, accessMap);

        // Resolve each entity's PK assignment strategy (config + schema meet here)
        // before mask generation — the writable masks carry the decision to the
        // runtime. Also hand the raw map to the auth layer: LoginService /
        // OidcVerifier insert into users/refresh_tokens outside the mask pipeline.
        PkStrategyResolver.Apply(entities, config.PkAssignment);
        config.Auth.PkAssignment ??= config.PkAssignment;

        var maskGenerator = new MaskGenerator(config.Db.Driver, config.DefaultListLimit, config.Tenancy, config.Scopes);
        var generatedMasks = maskGenerator.Generate(
            entities,
            accessMap.Count > 0 ? accessMap : null);

        var validatorGenerator = new ValidatorGenerator();
        var generatedValidators = validatorGenerator.Generate(entities);

        var manualMasks = config.Masks;
        var manualValidators = config.Validators ?? new Dictionary<string, ValidatorConfig>();

        // Order: generated → admin → manual. Manual wins.
        var combined = new Dictionary<string, MaskConfig>(generatedMasks);
        if (includeAdminMasks && adminMasks != null)
        {
            foreach (var (k, v) in adminMasks)
                combined[k] = v;
        }
        foreach (var (k, v) in manualMasks)
            combined[k] = v;

        config.Masks = combined;
        config.Validators = MergeValidators(generatedValidators, manualValidators);
        AttachEntityHooks(config.Validators, entities);
        TenancyResolver.NormalizeRowScopeDeclarations(config);
        var generatedMaskNames = generatedMasks.Keys
            .Concat(adminMasks?.Keys ?? Enumerable.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);
        TenancyResolver.ValidateMaskContracts(config, entities, tenancyWarnings, generatedMaskNames);
        EnsureTenantProvisionCommand(config);
        ValidateStrictSecurity(config);
        config.Runtime = new ShapeRuntime
        {
            Entities = entities,
            GeneratedMasks = generatedMasks,
            Commands = config.Commands?.Keys.ToList() ?? new List<string>(),
            AuditLogTable = auditLogTable,
            AdminMasks = adminMasks ?? new(),
            Warnings = tenancyWarnings
        };

        return config;
    }

    /// <summary>
    /// <c>security: "strict"</c> (brownfield adapter guide §4): turn
    /// "someone forgot to gate a surface" into a boot failure instead of an
    /// anonymous-reachable endpoint. Three checks, each with its own hint.
    /// </summary>
    private static void ValidateStrictSecurity(ShapeConfig config)
    {
        if (!string.Equals(config.Security, "strict", StringComparison.OrdinalIgnoreCase))
            return;

        if (config.Auth.Driver == "none")
            throw new ApiException(
                "CONFIG_INVALID",
                "security: strict requires an auth driver.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Set Auth.Driver = \"jwt\" (auth: \"jwt\" in JSON config) — with driver \"none\" every mask is reachable anonymously."
                });

        var openMasks = config.Masks
            .Where(pair => pair.Value.Roles is not { Count: > 0 })
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (openMasks.Count > 0)
            throw new ApiException(
                "CONFIG_INVALID",
                "security: strict forbids masks without a roles list: " + string.Join(", ", openMasks) + ".",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "A mask with empty roles is callable by every authenticated caller regardless of role. Add 'roles' to each listed mask (or to the access map entry generating it)."
                });

        if (config.ManifestRoles == null)
            throw new ApiException(
                "CONFIG_INVALID",
                "security: strict requires manifest_roles.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Set ManifestRoles — [] to require any authenticated caller, or list specific roles. Null leaves the manifest/OpenAPI surface open to anonymous readers."
                });
    }

    private static void EnsureTenantProvisionCommand(ShapeConfig config)
    {
        if (config.Tenancy == null || config.Tenancy.UnscopedRoles.Count == 0)
            return;

        config.Commands ??= new Dictionary<string, Func<Dictionary<string, object?>, CommandContext, Dictionary<string, object?>>>();
        if (config.Commands.ContainsKey("tenant.provision"))
            return;

        config.Commands["tenant.provision"] = (parameters, context) => TenantProvision(config, parameters, context);
    }

    /// <summary>
    /// SECURITY-REVIEW M2: when OIDC is enabled, <c>audience</c> is mandatory.
    /// Without an audience binding, any token the IdP signs for any application
    /// under the same issuer is accepted (confused-deputy). Fail the boot
    /// instead of silently accepting all audiences — "operator forgot audience"
    /// becomes un-bootable rather than a latent auth bypass. (Issuer is filled
    /// by IdP presets at verify time, so it is enforced at runtime in
    /// <see cref="OidcVerifier.ValidateClaims"/> where the preset is applied,
    /// not here where a preset-only config would false-positive.)
    /// </summary>
    private static void ValidateOidcConfig(ShapeConfig config)
    {
        var oidc = config.Auth.Oidc;
        if (oidc == null)
            return;

        if (string.IsNullOrWhiteSpace(oidc.Audience))
            throw new ApiException(
                "CONFIG_INVALID",
                "OIDC is enabled but `audience` is not configured.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Set auth.oidc.audience to your application's client/app id. Without it, tokens the IdP minted for other applications under the same issuer would be accepted."
                });
    }

    private static void ValidateOidcTenancy(ShapeConfig config)
    {
        if (config.Tenancy == null || config.Auth.Oidc == null || !config.Auth.Oidc.AutoProvision)
            return;

        if (!string.IsNullOrWhiteSpace(config.Tenancy.OidcTenantClaim))
            return;

        if (config.Tenancy.OidcAutoProvisionTenant.HasValue)
            return;

        throw new ApiException(
            "CONFIG_INVALID",
            "OIDC auto-provision requires a tenant source.",
            500,
            null,
            new Dictionary<string, object?>
            {
                ["hint"] = "Configure `tenancy.oidc_tenant_claim` or `tenancy.oidc_auto_provision_tenant`."
            });
    }

    private static Dictionary<string, object?> TenantProvision(
        ShapeConfig config,
        Dictionary<string, object?> parameters,
        CommandContext context)
    {
        var tenancy = config.Tenancy ?? throw new ApiException("CONFIG_INVALID", "Tenancy is not configured.", 500);
        var role = context.AuthContext.GetValueOrDefault("role")?.ToString() ?? "";
        if (!TenancyResolver.IsUnscopedRole(tenancy, role))
            throw new ApiException(
                "AUTH_FORBIDDEN",
                "Tenant provisioning requires an unscoped role.",
                403,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Use one of the roles listed in `tenancy.unscoped_roles`."
                });

        var tenantData = GetDict(parameters, "tenant")
            ?? throw new ApiException("COMMAND_VALIDATION_ERROR", "Field tenant is required.", 400);
        var userData = GetDict(parameters, "user")
            ?? throw new ApiException("COMMAND_VALIDATION_ERROR", "Field user is required.", 400);

        var usersLookupColumn = config.Auth.Oidc?.UsersLookupColumn ?? "oid";
        var tenantRow = new Dictionary<string, object?>(tenantData);
        if (tenantRow.ContainsKey(tenancy.Column))
            throw new ApiException("VALIDATION_ERROR", "tenant_id is reserved on tenant rows.", 400);

        context.BeginTransaction();
        try
        {
            var tenantId = InsertRow(context.Database, tenancy.Table, tenantRow, config.PkAssignment);

            var userRow = new Dictionary<string, object?>(userData)
            {
                [tenancy.Column] = tenantId
            };
            var email = userRow.GetValueOrDefault("email")?.ToString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(email))
                throw new ApiException("COMMAND_VALIDATION_ERROR", "User email is required.", 400);
            if (string.IsNullOrEmpty(userRow.GetValueOrDefault("display_name")?.ToString()?.Trim()))
            {
                var atIdx = email.IndexOf('@');
                userRow["display_name"] = atIdx > 0 ? email[..atIdx] : email;
            }

            var userRole = userRow.GetValueOrDefault("role")?.ToString()?.Trim() ?? "admin";
            if (string.IsNullOrWhiteSpace(userRole))
                userRole = "admin";
            if (TenancyResolver.IsUnscopedRole(tenancy, userRole))
                throw new ApiException(
                    "AUTH_FORBIDDEN",
                    "Tenant admin cannot be an unscoped role.",
                    403,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["hint"] = "Pick a tenant-scoped role for the first tenant admin."
                    });

            userRow["role"] = userRole;

            object? oidValue = null;
            if (userRow.TryGetValue(usersLookupColumn, out var lookupVal) && lookupVal != null)
            {
                oidValue = lookupVal;
            }
            else if (userRow.TryGetValue("oid", out var oidRaw) && oidRaw != null)
            {
                oidValue = oidRaw;
                if (!string.Equals(usersLookupColumn, "oid", StringComparison.Ordinal))
                {
                    userRow.Remove("oid");
                    userRow[usersLookupColumn] = oidRaw;
                }
            }

            if (userRow.TryGetValue("password", out var passwordObj) && passwordObj != null)
            {
                var password = passwordObj.ToString() ?? "";
                if (string.IsNullOrEmpty(password))
                    throw new ApiException("COMMAND_VALIDATION_ERROR", "User password is empty.", 400);
                if (password.Length < config.Auth.PasswordMinLength)
                    throw new ApiException(
                        "COMMAND_VALIDATION_ERROR",
                        $"Password must have at least {config.Auth.PasswordMinLength} characters.",
                        400);
                userRow.Remove("password");
                userRow["password_hash"] = BCrypt.Net.BCrypt.HashPassword(password);
            }
            else if (oidValue != null)
            {
                userRow["password_hash"] = "!locked-oidc-only!";
            }
            else
            {
                throw new ApiException(
                    "COMMAND_VALIDATION_ERROR",
                    "User must include either password or oid.",
                    400,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = new Dictionary<string, object?>
                        {
                            ["password"] = "string",
                            [usersLookupColumn] = "string"
                        }
                    });
            }

            var userId = InsertRow(context.Database, config.Auth.UsersTable, userRow, config.Auth.PkAssignment);
            context.Commit();

            return new Dictionary<string, object?>
            {
                ["tenant_id"] = tenantId,
                ["user_id"] = userId
            };
        }
        catch
        {
            if (context.InTransaction)
                context.Rollback();
            throw;
        }
    }

    private static long InsertRow(
        Database database,
        string table,
        Dictionary<string, object?> data,
        IReadOnlyDictionary<string, string>? pkAssignment)
    {
        if (PkStrategyResolver.IsForcedManual(pkAssignment, table))
            return database.InsertWithManualPk(table, "id", data);

        var cols = data.Keys.ToList();
        var sql = $"INSERT INTO {database.QuoteIdentifier(table)} " +
                  $"({string.Join(", ", cols.Select(database.QuoteIdentifier))}) " +
                  $"VALUES ({string.Join(", ", cols.Select(c => "@" + c))})";
        return database.InsertReturningId(sql, data);
    }

    /// <summary>
    /// Bridge declarative <c>[BeforeInsert]</c> / <c>[AfterUpdate]</c> /etc.
    /// hooks discovered by <see cref="EntityReader"/> into the dictionary-based
    /// <see cref="ValidatorConfig.Hooks"/> structure that <see cref="BatchExecutor"/>
    /// runs. Per (table, action) we collect every matching attribute-driven hook
    /// and chain them into the existing Before/After delegates so manual hook
    /// config keeps working alongside attribute-based ones.
    /// </summary>
    private static void AttachEntityHooks(
        Dictionary<string, ValidatorConfig> validators,
        List<EntityMeta> entities)
    {
        foreach (var meta in entities)
        {
            if (meta.Hooks.Count == 0) continue;

            foreach (var action in new[] { "insert", "update", "delete" })
            {
                var beforeKey = "before-" + action;
                var afterKey = "after-" + action;
                var beforeList = meta.Hooks.GetValueOrDefault(beforeKey);
                var afterList = meta.Hooks.GetValueOrDefault(afterKey);
                if ((beforeList == null || beforeList.Count == 0) && (afterList == null || afterList.Count == 0))
                    continue;

                var key = $"{meta.Table}.{action}";
                if (!validators.TryGetValue(key, out var cfg))
                {
                    cfg = new ValidatorConfig();
                    validators[key] = cfg;
                }
                cfg.Hooks ??= new HooksConfig();

                if (beforeList is { Count: > 0 })
                {
                    var existing = cfg.Hooks.Before;
                    cfg.Hooks.Before = ctx =>
                    {
                        existing?.Invoke(ctx);
                        foreach (var h in beforeList) h(ctx);
                    };
                }
                if (afterList is { Count: > 0 })
                {
                    var existing = cfg.Hooks.After;
                    cfg.Hooks.After = ctx =>
                    {
                        existing?.Invoke(ctx);
                        foreach (var h in afterList) h(ctx);
                    };
                }
            }
        }
    }

    private static void ValidateTenancyControlTables(ShapeConfig config, DatabaseIntrospector introspector)
    {
        var tenancy = config.Tenancy;
        if (tenancy == null)
            return;

        var tenantColumn = tenancy.Column;
        var usersTable = config.Auth.UsersTable;
        var usersColumns = introspector.DescribeColumns(usersTable);
        if (usersColumns.Count == 0)
        {
            throw new ApiException(
                "CONFIG_INVALID",
                $"Users table '{usersTable}' is not present in the database.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Create the auth users table before enabling tenancy."
                });
        }

        if (!usersColumns.Any(col => string.Equals(col.Name, tenantColumn, StringComparison.Ordinal)))
        {
            throw new ApiException(
                "CONFIG_INVALID",
                $"Users table '{usersTable}' is missing tenancy column '{tenantColumn}'.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = $"Add '{tenantColumn}' to the users table before enabling tenancy."
                });
        }

        var tenantTable = tenancy.Table;
        var registryColumns = introspector.DescribeColumns(tenantTable);
        if (registryColumns.Count == 0)
        {
            throw new ApiException(
                "CONFIG_INVALID",
                $"Tenancy registry table '{tenantTable}' is not present in the database.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = $"Create '{tenantTable}' before enabling tenancy."
                });
        }

        if (registryColumns.Any(col => string.Equals(col.Name, tenantColumn, StringComparison.Ordinal)))
        {
            throw new ApiException(
                "CONFIG_INVALID",
                $"Registry table '{tenantTable}' must not carry tenancy column '{tenantColumn}'.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "The registry table is global by definition and must remain unscoped."
                });
        }
    }

    private static Dictionary<string, ValidatorConfig> MergeValidators(
        Dictionary<string, ValidatorConfig> generated,
        Dictionary<string, ValidatorConfig> manual)
    {
        var merged = generated.ToDictionary(pair => pair.Key, pair => new ValidatorConfig
        {
            Validators = pair.Value.Validators.ToDictionary(item => item.Key, item => item.Value),
            Hooks = pair.Value.Hooks
        });

        foreach (var (key, manualConfig) in manual)
        {
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = manualConfig;
                continue;
            }

            foreach (var (field, validator) in manualConfig.Validators)
                existing.Validators[field] = validator;

            if (manualConfig.Hooks != null)
                existing.Hooks = manualConfig.Hooks;
        }

        return merged;
    }

    private static Dictionary<string, object?>? GetDict(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var value) && value is Dictionary<string, object?> nested ? nested : null;
}
