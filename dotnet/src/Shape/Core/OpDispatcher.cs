using Microsoft.AspNetCore.Http;
using Shape.Audit;
using Shape.Auth;
using Shape.Http;

namespace Shape.Core;

/// <summary>
/// Stateless per-request dispatcher for the Shape op set (query, mutation, command, auth.*).
/// Holds the immutable runtime state built once at <c>MapShape</c>/<c>MapShapeRuntime</c> time;
/// the only per-request objects are <see cref="Database"/>, <see cref="BatchExecutor"/>,
/// and <see cref="CommandExecutor"/>.
/// </summary>
public sealed class OpDispatcher
{
    private readonly DatabaseConfig _dbConfig;
    private readonly AuthConfig _authConfig;
    private readonly MaskRegistry _masks;
    private readonly ValidatorRegistry _validators;
    private readonly Dictionary<string, Func<Dictionary<string, object?>, CommandContext, Dictionary<string, object?>>> _commands;
    private readonly Dictionary<string, List<string>>? _commandRoles;
    private readonly JwtIssuer _issuer;
    private readonly LoginService _loginService;
    private readonly AdminAuth _adminAuth;

    /// <summary>
    /// Reusable guard for the no-Database path. Hot path when OIDC isn't
    /// configured (or when the caller is doing manifest auth before opening a DB).
    /// When OIDC is configured, the guard built on this field still runs the
    /// HS256 + OIDC verify, but local-user resolution requires a Database — see
    /// <see cref="BuildGuard"/>.
    /// </summary>
    private readonly IAuthGuard _guardNoDb;

    private readonly string? _auditLogTable;

    public OpDispatcher(
        DatabaseConfig dbConfig,
        AuthConfig authConfig,
        Dictionary<string, MaskConfig> masks,
        Dictionary<string, ValidatorConfig>? validators,
        Dictionary<string, Func<Dictionary<string, object?>, CommandContext, Dictionary<string, object?>>>? commands,
        string? auditLogTable = null,
        Dictionary<string, List<string>>? commandRoles = null)
    {
        _dbConfig = dbConfig;
        _authConfig = authConfig;
        _masks = new MaskRegistry(masks);
        _validators = new ValidatorRegistry(validators);
        _commands = commands ?? new();
        _commandRoles = commandRoles;
        _issuer = new JwtIssuer(authConfig);
        _loginService = new LoginService(dbConfig, authConfig, _issuer);
        _adminAuth = new AdminAuth(authConfig);
        _guardNoDb = BuildGuard(database: null);
        _auditLogTable = auditLogTable;
    }

    /// <summary>
    /// Build a fully wired BatchExecutor for the given Database. When the runtime
    /// detected an audit_log table at boot, the executor's audit-callback hook
    /// is wired to <see cref="AuditLogger.Log"/>.
    /// </summary>
    private BatchExecutor BuildExecutor(Database database)
    {
        var executor = new BatchExecutor(database, _masks, _validators, _authConfig.Tenancy, _authConfig.Scopes);
        if (!string.IsNullOrEmpty(_auditLogTable))
        {
            var logger = new AuditLogger(database, _auditLogTable);
            executor.SetAuditLogger(logger.Log);
        }
        return executor;
    }

    /// <summary>
    /// Long-lived guard suitable for the no-DB path (manifest auth, NullGuard
    /// flows, HS256-only deployments). When OIDC user resolution is needed
    /// per-request, prefer <see cref="BuildGuard(Database)"/>.
    /// </summary>
    public IAuthGuard Guard => _guardNoDb;

    /// <summary>
    /// Build an <see cref="IAuthGuard"/> with the given (optional) <see cref="Database"/>.
    /// When OIDC is configured and a Database is supplied, the verifier resolves
    /// <c>oid</c> → <c>users.id</c> as part of the auth context.
    /// </summary>
    public IAuthGuard BuildGuard(Database? database) => _authConfig.Driver == "jwt"
        ? new JwtGuard(_authConfig, database)
        : new NullGuard();

    public AdminAuth AdminAuth => _adminAuth;
    public AuthConfig AuthConfig => _authConfig;
    public MaskRegistry Masks => _masks;
    public ValidatorRegistry Validators => _validators;
    public Database CreateDatabase() => new(_dbConfig);

    /// <summary>
    /// A fully wired executor (scopes, tenancy, audit log) for code outside the op set — e.g. the
    /// attachment service — so it enforces exactly the rules the JSON API does.
    /// </summary>
    public BatchExecutor CreateExecutor(Database database) => BuildExecutor(database);

    public (Dictionary<string, object?> Result, int Status) Dispatch(
        HttpContext context,
        string op,
        Dictionary<string, object?> payload)
    {
        switch (op)
        {
            case "auth.token":
            case "auth.login":
                return (_loginService.Login(payload), 200);

            case "auth.refresh":
                return (_loginService.Refresh(payload), 200);

            case "auth.exchange":
                // Brownfield bridge: swap an app-validated external token for a
                // Shape JWT (exchange-once; see AuthConfig.Exchange). Anonymous
                // like auth.token — the external token IS the credential.
                return (_loginService.Exchange(payload), 200);

            case "auth.logout":
                return (_loginService.Logout(payload), 200);

            case "auth.admin.create-user":
            {
                var authContext = _adminAuth.AuthorizeAdmin(context);
                return (_loginService.CreateUser(payload, authContext), 201);
            }

            case "auth.admin.change-password":
            {
                var authContext = _adminAuth.AuthorizeAdmin(context);
                return (_loginService.ChangePassword(payload, authContext), 200);
            }

            case "auth.change-password":
            {
                using var database = CreateDatabase();
                var authContext = BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));
                return (_loginService.ChangeOwnPassword(payload, authContext), 200);
            }

            case "query":
            {
                using var database = CreateDatabase();
                var authContext = BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));
                var queries = GetList(payload, "queries")
                    ?? throw new ApiException("QUERY_VALIDATION_ERROR", "Field queries must be an array.", 400);

                var executor = BuildExecutor(database);
                return (executor.Execute(queries, authContext), 200);
            }

            case "mutation":
            {
                using var database = CreateDatabase();
                var authContext = BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));
                var mutations = GetList(payload, "mutations")
                    ?? throw new ApiException("MUTATION_VALIDATION_ERROR", "Field mutations must be an array.", 400);
                var useTransaction = payload.TryGetValue("transaction", out var transaction) && transaction is true;

                var executor = BuildExecutor(database);
                return (executor.ExecuteMutations(mutations, authContext, useTransaction), 200);
            }

            case "command":
            {
                using var database = CreateDatabase();
                var authContext = BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));
                var name = payload.GetValueOrDefault("name")?.ToString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    throw new ApiException("COMMAND_VALIDATION_ERROR", "Field name is required.", 400);
                var commandParams = GetDict(payload, "params") ?? new Dictionary<string, object?>();

                var executor = BuildExecutor(database);
                var commandExecutor = new CommandExecutor(database, executor, _commands, _commandRoles);
                return (commandExecutor.Execute(name, commandParams, authContext), 200);
            }

            default:
                throw new ApiException("NOT_FOUND", $"Unknown operation '{op}'.", 404);
        }
    }

    internal static List<Dictionary<string, object?>>? GetList(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value is not IList<object?> list)
            return null;

        return list.OfType<Dictionary<string, object?>>().ToList();
    }

    internal static Dictionary<string, object?>? GetDict(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var value) && value is Dictionary<string, object?> nested ? nested : null;
}
