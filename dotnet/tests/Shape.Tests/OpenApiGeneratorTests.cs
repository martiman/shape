using Shape.Auth;
using Shape.Core;
using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class OpenApiGeneratorTests
{
    private sealed class Order { }
    private sealed class Item { }

    private static EntityMeta OrderMeta(bool writable = true, string? softDelete = null) => new()
    {
        ClrType = typeof(Order),
        Table = "orders",
        Writable = writable,
        SoftDeleteColumn = softDelete,
        AuditColumns = new() { ["created_at"] = "timestamp" },
        Fields = new()
        {
            new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true, IsAutoIncrement = true },
            new FieldMeta { Name = "title", ClrType = typeof(string), ClrTypeName = "String", DbType = "varchar", Length = 200 },
            new FieldMeta { Name = "total", ClrType = typeof(decimal), ClrTypeName = "Decimal", DbType = "decimal" },
            new FieldMeta { Name = "created_at", ClrType = typeof(DateTime), ClrTypeName = "DateTime", DbType = "datetime", Nullable = true }
        }
    };

    private static EntityMeta ItemMeta() => new()
    {
        ClrType = typeof(Item),
        Table = "items",
        Writable = false,
        Fields = new()
        {
            new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true },
            new FieldMeta
            {
                Name = "order_id",
                ClrType = typeof(int),
                ClrTypeName = "Int32",
                DbType = "int",
                FkEntityType = typeof(Order)
            }
        }
    };

    private static Dictionary<string, MaskConfig> StandardMasks() => new()
    {
        ["orders.list"] = new() { Sql = "SELECT * FROM orders", DefaultLimit = 50, Description = "List all orders" },
        ["orders.get"] = new()
        {
            Sql = "SELECT * FROM orders WHERE id = @id",
            Params = new() { ["id"] = new() { Type = "int", Required = true } }
        },
        ["orders"] = new() { Sql = "SELECT id FROM orders LIMIT 1", Writable = true },
        ["items.list"] = new() { Sql = "SELECT * FROM items" },
        ["recent-activity"] = new() { Sql = "SELECT * FROM events ORDER BY created_at DESC", Description = "Standalone query" }
    };

    private static Dictionary<string, object?> Generate(OpenApiConfig? cfg = null)
    {
        return new OpenApiGenerator().Generate(
            new[] { OrderMeta(), ItemMeta() },
            StandardMasks(),
            new[] { "order.process" },
            new AuthConfig { Driver = "jwt" },
            "shapetest",
            cfg);
    }

    private static Dictionary<string, object?> Paths(Dictionary<string, object?> spec) =>
        (Dictionary<string, object?>)spec["paths"]!;

    private static Dictionary<string, object?> Schemas(Dictionary<string, object?> spec) =>
        (Dictionary<string, object?>)((Dictionary<string, object?>)spec["components"]!)["schemas"]!;

    // ── Top-level shape ────────────────────────────────────────────────

    [Fact]
    public void Spec_has_openapi_3_1_with_title_and_security_scheme()
    {
        var spec = Generate();

        Assert.Equal("3.1.0", spec["openapi"]);
        var info = (Dictionary<string, object?>)spec["info"]!;
        Assert.Equal("Shapetest API", info["title"]);

        var schemes = (Dictionary<string, object?>)((Dictionary<string, object?>)spec["components"]!)["securitySchemes"]!;
        var bearer = (Dictionary<string, object?>)schemes["bearerAuth"]!;
        Assert.Equal("http", bearer["type"]);
        Assert.Equal("bearer", bearer["scheme"]);
    }

    [Fact]
    public void Tags_and_x_tagGroups_are_emitted()
    {
        var spec = Generate();

        var tags = (List<object?>)spec["tags"]!;
        Assert.Contains(tags, t => ((Dictionary<string, object?>)t!)["name"]?.ToString() == "Auth");
        Assert.Contains(tags, t => ((Dictionary<string, object?>)t!)["name"]?.ToString() == "Manifest");
        Assert.Contains(tags, t => ((Dictionary<string, object?>)t!)["name"]?.ToString() == "Orders");
        Assert.Contains(tags, t => ((Dictionary<string, object?>)t!)["name"]?.ToString() == "Commands");

        var groups = (List<object?>)spec["x-tagGroups"]!;
        Assert.Contains(groups, g => ((Dictionary<string, object?>)g!)["name"]?.ToString() == "Entities");
        Assert.Contains(groups, g => ((Dictionary<string, object?>)g!)["name"]?.ToString() == "System");
    }

    // ── Auth paths ────────────────────────────────────────────────────

    [Fact]
    public void Auth_token_path_has_typed_request_and_response()
    {
        var paths = Paths(Generate());
        var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api?op=auth.token"]!)["post"]!;

        Assert.Equal("auth.token", op["operationId"]);
        Assert.Contains("Auth", (List<string>)op["tags"]!);
        Assert.True(op.ContainsKey("requestBody"));

        var responses = (Dictionary<string, object?>)op["responses"]!;
        Assert.Contains("200", responses.Keys);
        Assert.Contains("400", responses.Keys);
    }

    [Fact]
    public void Auth_admin_paths_require_bearer()
    {
        var paths = Paths(Generate());
        foreach (var key in new[] { "/api?op=auth.admin.create-user", "/api?op=auth.admin.change-password" })
        {
            var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths[key]!)["post"]!;
            Assert.True(op.ContainsKey("security"));
        }
    }

    // ── REST/OData GET ────────────────────────────────────────────────

    [Fact]
    public void Rest_list_path_emits_OData_query_parameters()
    {
        var paths = Paths(Generate());
        var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api/orders"]!)["get"]!;
        var parameters = (List<object?>)op["parameters"]!;
        var paramNames = parameters
            .Select(p => ((Dictionary<string, object?>)p!)["name"]?.ToString())
            .ToList();

        Assert.Contains("$top", paramNames);
        Assert.Contains("$skip", paramNames);
        Assert.Contains("$orderby", paramNames);
        Assert.Contains("$filter", paramNames);
        Assert.Contains("$select", paramNames);
        Assert.Contains("$count", paramNames);
    }

    [Fact]
    public void Rest_list_response_carries_ETag_and_Cache_Control_headers_and_304()
    {
        var paths = Paths(Generate());
        var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api/orders"]!)["get"]!;
        var responses = (Dictionary<string, object?>)op["responses"]!;
        var ok = (Dictionary<string, object?>)responses["200"]!;
        var headers = (Dictionary<string, object?>)ok["headers"]!;

        Assert.True(headers.ContainsKey("ETag"));
        Assert.True(headers.ContainsKey("Cache-Control"));
        Assert.True(responses.ContainsKey("304"));
    }

    [Fact]
    public void Rest_list_default_limit_lands_in_top_schema()
    {
        var paths = Paths(Generate());
        var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api/orders"]!)["get"]!;
        var parameters = (List<object?>)op["parameters"]!;
        var topParam = parameters
            .Cast<Dictionary<string, object?>>()
            .First(p => p["name"]?.ToString() == "$top");
        var topSchema = (Dictionary<string, object?>)topParam["schema"]!;

        Assert.Equal(50, topSchema["default"]);
        Assert.Equal(200, topSchema["maximum"]);
    }

    [Fact]
    public void Rest_get_by_pk_path_includes_path_parameter()
    {
        var paths = Paths(Generate());
        Assert.True(paths.ContainsKey("/api/orders/{id}"));
        var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api/orders/{id}"]!)["get"]!;
        var parameters = (List<object?>)op["parameters"]!;
        var idParam = (Dictionary<string, object?>)parameters[0]!;
        Assert.Equal("path", idParam["in"]);
        Assert.Equal(true, idParam["required"]);
    }

    // ── Generic mask GET ──────────────────────────────────────────────

    [Fact]
    public void Generic_mask_path_emits_query_parameters_from_mask_params()
    {
        var paths = Paths(Generate());
        Assert.True(paths.ContainsKey("/api/_/orders.get"));
        var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api/_/orders.get"]!)["get"]!;
        var parameters = (List<object?>)op["parameters"]!;
        var idParam = (Dictionary<string, object?>)parameters[0]!;

        Assert.Equal("id", idParam["name"]);
        Assert.Equal("query", idParam["in"]);
        Assert.Equal(true, idParam["required"]);
        Assert.Equal("integer", ((Dictionary<string, object?>)idParam["schema"]!)["type"]);
    }

    [Fact]
    public void Generic_mask_path_skips_writable_masks()
    {
        var paths = Paths(Generate());
        Assert.False(paths.ContainsKey("/api/_/orders"));
    }

    // ── Mutation paths ────────────────────────────────────────────────

    [Fact]
    public void Writable_entity_emits_insert_update_delete_paths()
    {
        var paths = Paths(Generate());

        Assert.True(paths.ContainsKey("/api?op=mutation&action=insert&table=orders"));
        Assert.True(paths.ContainsKey("/api?op=mutation&action=update&table=orders"));
        Assert.True(paths.ContainsKey("/api?op=mutation&action=delete&table=orders"));
    }

    [Fact]
    public void Insert_body_excludes_auto_increment_columns()
    {
        var paths = Paths(Generate());
        var insert = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api?op=mutation&action=insert&table=orders"]!)["post"]!;
        var body = (Dictionary<string, object?>)insert["requestBody"]!;
        var content = (Dictionary<string, object?>)body["content"]!;
        var schema = (Dictionary<string, object?>)((Dictionary<string, object?>)content["application/json"]!)["schema"]!;
        var payload = (Dictionary<string, object?>)((Dictionary<string, object?>)schema["properties"]!)["payload"]!;
        var mutations = (Dictionary<string, object?>)((Dictionary<string, object?>)payload["properties"]!)["mutations"]!;
        var mutationItem = (Dictionary<string, object?>)mutations["items"]!;
        var data = (Dictionary<string, object?>)((Dictionary<string, object?>)mutationItem["properties"]!)["data"]!;
        var dataProps = (Dictionary<string, object?>)data["properties"]!;

        Assert.False(dataProps.ContainsKey("id"));
        Assert.True(dataProps.ContainsKey("title"));
    }

    // ── Commands ──────────────────────────────────────────────────────

    [Fact]
    public void Each_command_becomes_a_path()
    {
        var paths = Paths(Generate());
        Assert.True(paths.ContainsKey("/api?op=command&name=order.process"));
        var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api?op=command&name=order.process"]!)["post"]!;
        Assert.Contains("Commands", (List<string>)op["tags"]!);
    }

    // ── Schemas ───────────────────────────────────────────────────────

    [Fact]
    public void Entity_schema_typed_with_format_for_datetime_and_maxLength_for_string()
    {
        var schemas = Schemas(Generate());
        var orders = (Dictionary<string, object?>)schemas["Orders"]!;
        var props = (Dictionary<string, object?>)orders["properties"]!;

        var title = (Dictionary<string, object?>)props["title"]!;
        Assert.Equal("string", title["type"]);
        Assert.Equal(200, title["maxLength"]);

        var createdAt = (Dictionary<string, object?>)props["created_at"]!;
        Assert.Equal("date-time", createdAt["format"]);
    }

    [Fact]
    public void Pk_field_is_readOnly_in_schema()
    {
        var schemas = Schemas(Generate());
        var orders = (Dictionary<string, object?>)schemas["Orders"]!;
        var idField = (Dictionary<string, object?>)((Dictionary<string, object?>)orders["properties"]!)["id"]!;
        Assert.Equal(true, idField["readOnly"]);
    }

    [Fact]
    public void Fk_field_carries_x_foreignKey_extension()
    {
        var schemas = Schemas(Generate());
        var items = (Dictionary<string, object?>)schemas["Items"]!;
        var orderId = (Dictionary<string, object?>)((Dictionary<string, object?>)items["properties"]!)["order_id"]!;
        Assert.Equal("Order", orderId["x-foreignKey"]);
    }

    [Fact]
    public void Error_schema_documents_self_documenting_envelope()
    {
        var schemas = Schemas(Generate());
        var error = (Dictionary<string, object?>)schemas["Error"]!;
        var props = (Dictionary<string, object?>)error["properties"]!;
        var envelope = (Dictionary<string, object?>)((Dictionary<string, object?>)props["error"]!)["properties"]!;

        Assert.True(envelope.ContainsKey("code"));
        Assert.True(envelope.ContainsKey("message"));
        Assert.True(envelope.ContainsKey("expected"));
        Assert.True(envelope.ContainsKey("example"));
        Assert.True(envelope.ContainsKey("hint"));
    }

    [Fact]
    public void MutationResult_schema_documents_id_field()
    {
        var schemas = Schemas(Generate());
        var mut = (Dictionary<string, object?>)schemas["MutationResult"]!;
        var results = (Dictionary<string, object?>)((Dictionary<string, object?>)mut["properties"]!)["results"]!;
        var item = (Dictionary<string, object?>)results["items"]!;
        var props = (Dictionary<string, object?>)item["properties"]!;
        Assert.True(props.ContainsKey("id"));
    }

    // ── Reads config ──────────────────────────────────────────────────

    [Fact]
    public void Reads_get_default_emits_GET_paths_no_POST_query()
    {
        var paths = Paths(Generate());
        Assert.True(paths.ContainsKey("/api/orders"));
        Assert.False(paths.ContainsKey("/api?op=query&ref=orders.list"));
    }

    [Fact]
    public void Reads_post_emits_POST_paths_no_GET()
    {
        var paths = Paths(Generate(new OpenApiConfig { Reads = "post" }));
        Assert.False(paths.ContainsKey("/api/orders"));
        Assert.True(paths.ContainsKey("/api?op=query&ref=orders.list"));
    }

    [Fact]
    public void Reads_both_emits_both()
    {
        var paths = Paths(Generate(new OpenApiConfig { Reads = "both" }));
        Assert.True(paths.ContainsKey("/api/orders"));
        Assert.True(paths.ContainsKey("/api?op=query&ref=orders.list"));
    }

    [Fact]
    public void Standalone_mask_appears_under_Queries_tag_in_post_mode()
    {
        var paths = Paths(Generate(new OpenApiConfig { Reads = "post" }));
        var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api?op=query&ref=recent-activity"]!)["post"]!;
        Assert.Contains("Queries", (List<string>)op["tags"]!);
    }

    // ── Manifest path ─────────────────────────────────────────────────

    [Fact]
    public void Manifest_GET_path_references_ShapeManifest_schema()
    {
        var paths = Paths(Generate());
        var op = (Dictionary<string, object?>)((Dictionary<string, object?>)paths["/api?manifest"]!)["get"]!;
        var responses = (Dictionary<string, object?>)op["responses"]!;
        var ok = (Dictionary<string, object?>)responses["200"]!;
        var content = (Dictionary<string, object?>)ok["content"]!;
        var schema = (Dictionary<string, object?>)((Dictionary<string, object?>)content["application/json"]!)["schema"]!;

        Assert.Equal("#/components/schemas/ShapeManifest", schema["$ref"]);
    }
}
