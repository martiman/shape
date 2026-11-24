using Shape.Hosting;
using Xunit;

namespace Shape.Tests;

public sealed class ShapeHostTests
{
    [Fact]
    public void IsStdioMcpMode_detects_flag()
    {
        Assert.True(ShapeHost.IsStdioMcpMode(new[] { "--mcp-stdio" }));
        Assert.True(ShapeHost.IsStdioMcpMode(new[] { "--other", "--mcp-stdio" }));
    }

    [Fact]
    public void IsStdioMcpMode_case_insensitive()
    {
        Assert.True(ShapeHost.IsStdioMcpMode(new[] { "--MCP-STDIO" }));
        Assert.True(ShapeHost.IsStdioMcpMode(new[] { "--Mcp-Stdio" }));
    }

    [Fact]
    public void IsStdioMcpMode_returns_false_when_absent()
    {
        Assert.False(ShapeHost.IsStdioMcpMode(Array.Empty<string>()));
        Assert.False(ShapeHost.IsStdioMcpMode(new[] { "--urls", "http://+:8080" }));
    }
}
