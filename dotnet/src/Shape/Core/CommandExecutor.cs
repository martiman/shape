namespace Shape.Core;

public sealed class CommandExecutor
{
    private readonly Database _database;
    private readonly BatchExecutor _executor;
    private readonly Dictionary<string, Func<Dictionary<string, object?>, CommandContext, Dictionary<string, object?>>> _handlers;
    private readonly Dictionary<string, List<string>> _commandRoles;

    public CommandExecutor(
        Database database,
        BatchExecutor executor,
        Dictionary<string, Func<Dictionary<string, object?>, CommandContext, Dictionary<string, object?>>>? handlers = null,
        Dictionary<string, List<string>>? commandRoles = null)
    {
        _database = database;
        _executor = executor;
        _handlers = handlers ?? new();
        _commandRoles = commandRoles ?? new();
    }

    public Dictionary<string, object?> Execute(
        string commandName,
        Dictionary<string, object?> parameters,
        Dictionary<string, object?>? authContext = null)
    {
        authContext ??= new();

        // SECURITY-REVIEW M9: role-gate command invocation. A command with a
        // configured role list requires the caller's role to be in it; a command
        // with no entry is unrestricted (mirrors the mask-role default). Runs
        // before the handler lookup so an unauthorized caller cannot even probe
        // which commands exist.
        if (_commandRoles.TryGetValue(commandName, out var allowedRoles) && allowedRoles.Count > 0)
        {
            var callerRole = authContext.GetValueOrDefault("role")?.ToString() ?? "";
            if (callerRole.Length == 0 || !allowedRoles.Contains(callerRole, StringComparer.Ordinal))
                throw new ApiException(
                    "AUTH_FORBIDDEN",
                    $"Your role cannot run command '{commandName}'.",
                    403,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = new Dictionary<string, object?> { ["role"] = "one of: " + string.Join(", ", allowedRoles) },
                        ["hint"] = callerRole.Length == 0
                            ? "Authenticate first via POST {op:'auth.token'} and pass the access token as Authorization: Bearer <token>."
                            : $"Command '{commandName}' is restricted to: {string.Join(", ", allowedRoles)}."
                    });
        }

        if (!_handlers.TryGetValue(commandName, out var handler))
        {
            var available = _handlers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            throw new ApiException(
                "COMMAND_NOT_FOUND",
                $"Command '{commandName}' does not exist.",
                404,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = available.Count == 0
                        ? "(no commands registered)"
                        : "one of: " + string.Join(", ", available),
                    ["example"] = new Dictionary<string, object?>
                    {
                        ["op"] = "command",
                        ["payload"] = new Dictionary<string, object?>
                        {
                            ["name"] = available.FirstOrDefault() ?? "<command-name>",
                            ["params"] = new Dictionary<string, object?>()
                        }
                    },
                    ["hint"] = "Fetch GET /api?manifest to see registered commands."
                });
        }

        try
        {
            var context = new CommandContext(_database, _executor, authContext);
            return handler(parameters, context);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception e)
        {
            // SECURITY-REVIEW M7: don't echo the raw exception message to the
            // client (leaks internals). Log it server-side under a correlation id.
            var correlationId = Guid.NewGuid().ToString("n");
            try
            {
                Console.Error.WriteLine(
                    $"[shape][COMMAND_ERROR][{correlationId}] {commandName}: {e.GetType().FullName}: {e.Message}\n{e.StackTrace}");
            }
            catch { /* best-effort */ }
            throw new ApiException(
                "COMMAND_ERROR",
                "Command execution failed.",
                500,
                null,
                new Dictionary<string, object?> { ["correlation_id"] = correlationId });
        }
    }
}

public sealed class CommandContext
{
    private readonly Database _database;
    private readonly BatchExecutor _executor;
    private readonly Dictionary<string, object?> _authContext;

    public CommandContext(Database database, BatchExecutor executor, Dictionary<string, object?> authContext)
    {
        _database = database;
        _executor = executor;
        _authContext = authContext;
    }

    public Dictionary<string, object?> ExecuteMutations(
        List<Dictionary<string, object?>> mutations, bool useTransaction = true)
    {
        return _executor.ExecuteMutations(mutations, _authContext, useTransaction);
    }

    public void BeginTransaction() => _database.BeginTransaction();
    public void Commit() => _database.Commit();
    public void Rollback() => _database.Rollback();
    public bool InTransaction => _database.IsInTransaction;
    public Database Database => _database;
    public Dictionary<string, object?> AuthContext => _authContext;
}
