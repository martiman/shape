using Microsoft.AspNetCore.Builder;
using Shape.Core;
using Shape.Mcp;

namespace Shape.Hosting;

/// <summary>
/// One-call host for apps that look like the canonical Shape shape: load
/// config, optionally serve MCP over stdio, otherwise expose Shape on
/// ASP.NET Core's <c>WebApplication</c>. Apps with custom routes / DI /
/// middleware can still use the manual <c>MapShape</c> path; this is the
/// fast path for <c>tocsbe</c>-style backends.
///
/// Used by <see cref="ShapeApp.Run"/> — operators don't typically reach
/// for this directly.
/// </summary>
public static class ShapeHost
{
    /// <summary>
    /// True when <c>--mcp-stdio</c> appears in <paramref name="args"/>.
    /// Convention: this single flag flips the binary into MCP-server mode
    /// for Claude Desktop / Cursor / Cline; everything else is HTTP.
    /// </summary>
    public static bool IsStdioMcpMode(string[] args)
        => args.Any(a => string.Equals(a, "--mcp-stdio", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Run the configured Shape backend. Picks stdio MCP when
    /// <c>--mcp-stdio</c> is present, runs the e-mail notification job when <c>--notify</c> is present
    /// (see <see cref="Shape.Notify.NotifyRunner"/>), otherwise builds a
    /// <see cref="WebApplication"/> and maps Shape on the configured
    /// endpoint. The two <c>configure*</c> hooks let consumers add custom
    /// routes / middleware / DI services without giving up the one-call
    /// helper.
    /// </summary>
    public static Task RunAsync(
        ShapeConfig config,
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? configureApp = null)
    {
        if (IsStdioMcpMode(args))
        {
            var role = Environment.GetEnvironmentVariable("SHAPE_MCP_ROLE") ?? "admin";
            var server = new McpServer(config, new Dictionary<string, object?> { ["role"] = role });
            new StdioTransport(server).Serve();
            return Task.CompletedTask;
        }

        if (Notify.NotifyRunner.IsNotifyMode(args))
        {
            // Cron mode: evaluate the notification rules, send the outbox, exit (no web server).
            var code = Notify.NotifyRunner.Run(config, args);
            if (code != 0) Environment.ExitCode = code;
            return Task.CompletedTask;
        }

        var builder = WebApplication.CreateBuilder(args);
        configureBuilder?.Invoke(builder);
        var app = builder.Build();
        configureApp?.Invoke(app);
        app.MapShape(config);
        return app.RunAsync();
    }
}
