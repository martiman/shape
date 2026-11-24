using Shape.Auth;
using Shape.Core;
using Shape.Mcp;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class McpServerTests
{
    private sealed class Order { }

    private static ShapeConfig BuildConfig() => new()
    {
        ServiceName = "shapetest",
        Auth = new AuthConfig { Driver = "none" },
        Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" },
        Masks = new()
        {
            ["orders.list"] = new() { Sql = "SELECT * FROM orders", Description = "List orders" },
            ["orders.adminGet"] = new()
            {
                Sql = "SELECT * FROM orders WHERE id = @id",
                Roles = new() { "admin" },
                Params = new() { ["id"] = new() { Type = "int", Required = true } }
            },
            ["orders"] = new() { Sql = "SELECT id FROM orders LIMIT 1", Writable = true }
        },
        Runtime = new ShapeRuntime
        {
            Entities = new()
            {
                new EntityMeta
                {
                    ClrType = typeof(Order),
                    Table = "orders",
                    Writable = true,
                    Fields = new()
                    {
                        new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true },
                        new FieldMeta { Name = "title", ClrType = typeof(string), ClrTypeName = "String", DbType = "varchar" }
                    }
                }
            }
        }
    };

    [Fact]
    public void Initialize_returns_protocolVersion_and_serverInfo()
    {
        var server = new McpServer(BuildConfig());
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize"
        });

        Assert.NotNull(response);
        var result = (Dictionary<string, object?>)response!["result"]!;
        Assert.Equal("2024-11-05", result["protocolVersion"]);
        var info = (Dictionary<string, object?>)result["serverInfo"]!;
        Assert.Contains("shapetest", (string)info["title"]!);
    }

    [Fact]
    public void Ping_returns_empty_object()
    {
        var server = new McpServer(BuildConfig());
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["id"] = 2,
            ["method"] = "ping"
        });

        Assert.NotNull(response);
        Assert.NotNull(response!["result"]);
    }

    [Fact]
    public void Notifications_initialized_returns_null()
    {
        var server = new McpServer(BuildConfig());
        // No "id" key — this is a notification.
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["method"] = "notifications/initialized"
        });

        Assert.Null(response);
    }

    [Fact]
    public void Unknown_method_returns_jsonrpc_error()
    {
        var server = new McpServer(BuildConfig());
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["id"] = 3,
            ["method"] = "totally/unknown"
        });

        Assert.NotNull(response);
        var error = (Dictionary<string, object?>)response!["error"]!;
        Assert.Equal(-32601, error["code"]);
    }

    [Fact]
    public void ToolsList_admin_role_sees_all_tools()
    {
        var server = new McpServer(BuildConfig(), new Dictionary<string, object?> { ["role"] = "admin" });
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["id"] = 4,
            ["method"] = "tools/list"
        });

        var result = (Dictionary<string, object?>)response!["result"]!;
        var tools = (List<Dictionary<string, object?>>)result["tools"]!;
        var names = tools.Select(t => t["name"]?.ToString()).ToList();

        Assert.Contains("orders_list", names);
        Assert.Contains("orders_adminGet", names);
        Assert.Contains("orders_mutate", names);
    }

    [Fact]
    public void ToolsList_filters_admin_only_tools_for_user_role()
    {
        var server = new McpServer(BuildConfig(), new Dictionary<string, object?> { ["role"] = "user" });
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["id"] = 5,
            ["method"] = "tools/list"
        });

        var tools = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)response!["result"]!)["tools"]!;
        var names = tools.Select(t => t["name"]?.ToString()).ToList();

        Assert.Contains("orders_list", names);     // no role restriction → visible
        Assert.DoesNotContain("orders_adminGet", names); // admin-only → hidden
    }

    [Fact]
    public void ResourcesList_includes_README_manifest_tools_and_per_entity()
    {
        var server = new McpServer(BuildConfig());
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["id"] = 6,
            ["method"] = "resources/list"
        });

        var resources = (List<Dictionary<string, object?>>)
            ((Dictionary<string, object?>)response!["result"]!)["resources"]!;
        var uris = resources.Select(r => r["uri"]?.ToString()).ToList();

        Assert.Contains("shape://README.md", uris);
        Assert.Contains("shape://manifest", uris);
        Assert.Contains("shape://tools", uris);
        Assert.Contains("shape://entities/orders", uris);
    }

    [Fact]
    public void ResourcesRead_README_returns_markdown_content()
    {
        var server = new McpServer(BuildConfig());
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["id"] = 7,
            ["method"] = "resources/read",
            ["params"] = new Dictionary<string, object?> { ["uri"] = "shape://README.md" }
        });

        var contents = (List<Dictionary<string, object?>>)
            ((Dictionary<string, object?>)response!["result"]!)["contents"]!;
        Assert.Equal("text/markdown", contents[0]["mimeType"]);
        Assert.Contains("shapetest Backend", (string)contents[0]["text"]!);
    }

    [Fact]
    public void ResourcesRead_unknown_entity_throws_jsonrpc_error()
    {
        var server = new McpServer(BuildConfig());
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["id"] = 8,
            ["method"] = "resources/read",
            ["params"] = new Dictionary<string, object?> { ["uri"] = "shape://entities/nonexistent" }
        });

        Assert.NotNull(response!["error"]);
    }

    [Fact]
    public void ToolsCall_unknown_tool_returns_isError_content()
    {
        var server = new McpServer(BuildConfig());
        var response = server.Handle(new Dictionary<string, object?>
        {
            ["id"] = 9,
            ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object?>
            {
                ["name"] = "nonexistent_tool",
                ["arguments"] = new Dictionary<string, object?>()
            }
        });

        var result = (Dictionary<string, object?>)response!["result"]!;
        Assert.True((bool)result["isError"]!);
    }
}
