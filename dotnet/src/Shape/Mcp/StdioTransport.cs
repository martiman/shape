using System.Text.Json;
using Shape.Core;

namespace Shape.Mcp;

/// <summary>
/// Wraps <see cref="McpServer"/> in a newline-delimited JSON-RPC loop over
/// <see cref="Console.In"/> / <see cref="Console.Out"/>. Each line on stdin is
/// one JSON-RPC message; each non-null response is one line on stdout.
///
/// Used by the <c>shape-mcp</c> console app for Claude Desktop / Cursor /
/// Cline local integrations. The client spawns this process once per session
/// and keeps the pipe open for the conversation's lifetime.
/// </summary>
public sealed class StdioTransport
{
    private readonly McpServer _server;

    public StdioTransport(McpServer server)
    {
        _server = server;
    }

    /// <summary>
    /// Block on stdin until the stream closes, dispatching each line through
    /// the server. Non-null responses flush to stdout immediately.
    /// </summary>
    public void Serve()
    {
        Serve(Console.In, Console.Out);
    }

    /// <summary>
    /// Same as <see cref="Serve()"/> but with caller-supplied streams (for tests).
    /// </summary>
    public void Serve(TextReader stdin, TextWriter stdout)
    {
        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0)
                continue;

            Dictionary<string, object?>? message;
            try
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (doc == null)
                {
                    WriteParseError(stdout);
                    continue;
                }
                message = doc.ToDictionary(p => p.Key, p => ParamResolver.UnwrapJsonElement(p.Value));
            }
            catch (JsonException)
            {
                WriteParseError(stdout);
                continue;
            }

            var response = _server.Handle(message);
            if (response != null)
                WriteLine(stdout, response);
        }
    }

    private static void WriteParseError(TextWriter stdout)
    {
        WriteLine(stdout, new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = -32700,
                ["message"] = "Parse error"
            }
        });
    }

    private static void WriteLine(TextWriter stdout, Dictionary<string, object?> response)
    {
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        stdout.WriteLine(json);
        stdout.Flush();
    }
}
