using System.Text.Json;
using Shape.Auth;
using Shape.Core;
using Shape.Mcp;
using Xunit;

namespace Shape.Tests;

public sealed class StdioTransportTests
{
    private static ShapeConfig Config() => new()
    {
        ServiceName = "stdiotest",
        Auth = new AuthConfig { Driver = "none" },
        Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" }
    };

    private static List<Dictionary<string, JsonElement>> RunWith(string stdinPayload)
    {
        var server = new McpServer(Config());
        var transport = new StdioTransport(server);

        using var stdin = new StringReader(stdinPayload);
        using var stdout = new StringWriter();
        transport.Serve(stdin, stdout);

        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines
            .Select(l => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(l)!)
            .ToList();
    }

    [Fact]
    public void Initialize_round_trip_emits_one_response()
    {
        var responses = RunWith("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""" + "\n");

        Assert.Single(responses);
        Assert.Equal(1, responses[0]["id"].GetInt32());
        Assert.True(responses[0].ContainsKey("result"));
    }

    [Fact]
    public void Notification_produces_no_output_line()
    {
        // No "id" → notification → no response
        var responses = RunWith("""{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n");
        Assert.Empty(responses);
    }

    [Fact]
    public void Multiple_messages_emit_multiple_responses_in_order()
    {
        var stdin =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}\n";

        var responses = RunWith(stdin);

        Assert.Equal(3, responses.Count);
        Assert.Equal(1, responses[0]["id"].GetInt32());
        Assert.Equal(2, responses[1]["id"].GetInt32());
        Assert.Equal(3, responses[2]["id"].GetInt32());
    }

    [Fact]
    public void Malformed_json_produces_parse_error_envelope()
    {
        var responses = RunWith("not json at all\n");

        Assert.Single(responses);
        var error = responses[0]["error"];
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public void Blank_lines_between_messages_are_skipped()
    {
        var stdin = "\n\n   \n{\"id\":1,\"method\":\"ping\"}\n\n";
        var responses = RunWith(stdin);

        Assert.Single(responses);
        Assert.Equal(1, responses[0]["id"].GetInt32());
    }
}
