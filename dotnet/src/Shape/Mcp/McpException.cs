namespace Shape.Mcp;

/// <summary>
/// Protocol-level error to be reported as a JSON-RPC error response.
/// For tool-call failures (where the client expects <c>isError</c> in a
/// successful response), throw <see cref="Shape.Core.ApiException"/> instead —
/// <see cref="McpServer"/> wraps it with the self-documenting envelope.
/// </summary>
public sealed class McpException : Exception
{
    public int McpCode { get; }

    public McpException(int mcpCode, string message) : base(message)
    {
        McpCode = mcpCode;
    }
}
