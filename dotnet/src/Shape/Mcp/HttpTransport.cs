using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shape.Core;
using Shape.Http;

namespace Shape.Mcp;

/// <summary>
/// HTTP transport for <see cref="McpServer"/>. Reads a JSON-RPC message from
/// the request body, dispatches to the server, and writes the response body.
/// CORS preflight handled. Auth context comes from the request's
/// <see cref="Shape.Auth.IAuthGuard"/> — same JwtGuard pipeline as the rest of Shape.
/// </summary>
public static class HttpTransport
{
    /// <summary>
    /// Handle a single <c>POST /api?mcp</c> request. Caller is responsible for
    /// running this inside <see cref="FatalHandler.RunSafelyAsync"/>.
    /// </summary>
    public static async Task HandlePostAsync(
        HttpContext context,
        McpServer server,
        Dictionary<string, object?> authContext)
    {
        // Hand the per-request auth context to the server so role-filtering
        // and mask resolution match the rest of the request pipeline.
        server.SetAuthContext(authContext);

        using var reader = new StreamReader(context.Request.Body);
        var raw = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(raw))
            throw new ApiException("INVALID_JSON", "MCP request body is empty.", 400);

        Dictionary<string, object?> message;
        try
        {
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw)
                ?? throw new ApiException("INVALID_JSON", "MCP body must be a JSON object.", 400);
            message = doc.ToDictionary(p => p.Key, p => ParamResolver.UnwrapJsonElement(p.Value));
        }
        catch (JsonException)
        {
            throw new ApiException("INVALID_JSON", "MCP body must be a JSON object.", 400);
        }

        var response = server.Handle(message);

        // Notifications expect no response.
        if (response == null)
        {
            context.Response.StatusCode = 204;
            return;
        }

        await JsonResponse.SuccessAsync(context, response);
    }

    /// <summary>
    /// Handle <c>OPTIONS /api?mcp</c> preflight. Permissive CORS so
    /// browser-based MCP clients (Claude.ai web, custom UIs) can connect.
    /// </summary>
    public static Task HandleOptionsAsync(HttpContext context)
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
        context.Response.StatusCode = 204;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handle <c>GET /api?mcp</c> — capability hint so curl-style probes get
    /// something useful instead of a 405.
    /// </summary>
    public static Task HandleGetAsync(HttpContext context, string serviceName)
    {
        return JsonResponse.SuccessAsync(context, new Dictionary<string, object?>
        {
            ["service"] = serviceName,
            ["protocol"] = "mcp",
            ["transport"] = "http",
            ["message"] = "POST a JSON-RPC body to this endpoint to call the MCP server.",
            ["example"] = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new Dictionary<string, object?>()
            }
        });
    }
}
