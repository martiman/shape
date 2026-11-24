using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Shape.Core;

namespace Shape.Http;

/// <summary>
/// Centralized request-level exception handling plus process-level fatal hooks.
///
/// <list type="bullet">
///   <item><see cref="RunSafelyAsync"/> wraps an endpoint body so <see cref="ApiException"/>s
///   become typed JSON envelopes and any other exception becomes an <c>INTERNAL_ERROR</c>
///   envelope with <c>details.exception_type</c> + <c>details.hint</c> derived from
///   <see cref="MessageHint"/>.</item>
///   <item><see cref="Register"/> hooks <see cref="AppDomain.UnhandledException"/> and
///   <see cref="TaskScheduler.UnobservedTaskException"/> for last-ditch logging on
///   non-request threads.</item>
///   <item><see cref="MessageHint"/> and <see cref="SanitizeMessage"/> are pure helpers,
///   exposed for testing and for callers that need to format their own envelopes.</item>
/// </list>
///
/// PHP's <c>FatalHandler</c> mostly catches PHP fatals (E_ERROR, OOM, etc.) that bypass
/// normal try/catch. .NET doesn't have that pathology — exceptions propagate. So the
/// .NET-shaped handler focuses on enriching the envelope and catching async/background
/// failures that the request-level catch can't see.
/// </summary>
public static class FatalHandler
{
    private static readonly Regex AbsolutePathRegex = new(
        @"(?:[A-Za-z]:)?[/\\][^\s:]+[/\\]([^\s/\\]+\.(?:cs|dll|so|dylib))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static int s_registered;

    /// <summary>
    /// Wrap an endpoint body. <see cref="ApiException"/>s pass through to the typed
    /// envelope; anything else becomes <c>INTERNAL_ERROR</c> with a sanitized message
    /// and a one-line hint.
    /// </summary>
    public static async Task RunSafelyAsync(HttpContext context, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (ApiException ex)
        {
            await JsonResponse.ErrorAsync(context, ex);
        }
        catch (Exception ex)
        {
            // SECURITY-REVIEW M7: never return the raw exception/DB message or
            // the exception type to the client — those leak schema, SQL, and
            // deployment internals. Emit a generic message plus a correlation id;
            // the full detail is logged server-side under that id.
            var correlationId = Guid.NewGuid().ToString("n");
            try
            {
                Console.Error.WriteLine(
                    $"[shape][INTERNAL_ERROR][{correlationId}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            }
            catch { /* logging is best-effort */ }

            var details = new Dictionary<string, object?>
            {
                ["correlation_id"] = correlationId,
                ["hint"] = MessageHint(ex)
            };
            await JsonResponse.ErrorAsync(
                context,
                "INTERNAL_ERROR",
                "An internal error occurred. Contact the operator with the correlation id.",
                500,
                null,
                details);
        }
    }

    /// <summary>
    /// Idempotent. Hooks process-level handlers for unhandled exceptions on
    /// non-request threads (background tasks, timers, etc.). The optional
    /// <paramref name="logger"/> is invoked with the exception so callers
    /// can wire a real logging pipeline; without it the exception is simply
    /// swallowed (the process is usually going down anyway).
    /// </summary>
    public static void Register(Action<Exception>? logger = null)
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                logger?.Invoke(ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger?.Invoke(args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// Map an exception to a one-line hint for the JSON envelope. Pattern-matches
    /// the type first, then the message (cheaper than walking the inner-exception
    /// chain). Returns a generic hint when nothing more specific applies.
    /// </summary>
    public static string MessageHint(Exception ex)
    {
        if (ex is OutOfMemoryException)
            return "Server out of memory. Lower 'top' in the query, paginate the read, or increase the host's memory.";

        if (ex is TimeoutException)
            return "Request exceeded its timeout. Narrow the query or raise the timeout.";

        if (ex is DbException dbe)
        {
            var msg = dbe.Message?.ToLowerInvariant() ?? "";
            if (msg.Contains("deadlock"))
                return "Database deadlock — retry the request; another session held conflicting locks.";
            if (msg.Contains("timeout"))
                return "Database query timed out. Narrow the query (smaller 'top', narrower filter) or raise the DB timeout.";
            if (msg.Contains("connection") || msg.Contains("refused"))
                return "Database connection failed. Check connection string and that the server is reachable.";
            return "Database error. SQLSTATE in details.sqlstate; check the server log for the failed statement.";
        }

        if (ex is InvalidOperationException && ex.Message.Contains("transaction", StringComparison.OrdinalIgnoreCase))
            return "Transaction state was wrong (commit/rollback after dispose, or nested begin). Check the call sequence.";

        return "Check the server log for stack details. A request timestamp narrows the search.";
    }

    /// <summary>
    /// Strip absolute filesystem paths from an error message so deployment layout
    /// doesn't leak to clients (per OWASP A01:2021 — sensitive data exposure).
    /// File basenames are preserved for debugging context.
    /// </summary>
    public static string SanitizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;
        return AbsolutePathRegex.Replace(message, "$1");
    }
}
