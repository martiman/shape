using System.Text.Json;
using System.Text.RegularExpressions;
using Shape.Core;
using Shape.Generator;
using Shape.Metadata;

namespace Shape.Mcp;

/// <summary>
/// MCP (Model Context Protocol) server for a Shape backend. Transport-agnostic:
/// takes a JSON-RPC message, returns a JSON-RPC response. Wrap with stdio
/// (Claude Desktop) or HTTP transport (hosted backends).
///
/// Protocol surface:
/// <list type="bullet">
///   <item><c>initialize</c> / <c>initialized</c> — handshake</item>
///   <item><c>tools/list</c> — derived from <see cref="LlmToolGenerator"/>, role-filtered</item>
///   <item><c>tools/call</c> — dispatched into <see cref="BatchExecutor"/> / <see cref="CommandExecutor"/></item>
///   <item><c>resources/list</c> — <c>shape://README.md</c>, <c>shape://manifest</c>, <c>shape://tools</c>, <c>shape://entities/&lt;table&gt;</c></item>
///   <item><c>resources/templates/list</c></item>
///   <item><c>resources/read</c> — serves the resource content</item>
///   <item><c>ping</c></item>
/// </list>
/// </summary>
public sealed class McpServer
{
    private const string ProtocolVersion = "2024-11-05";

    private readonly ShapeConfig _config;
    private readonly string _serviceName;
    private readonly Dictionary<string, ToolEntry> _toolIndex = new();
    private Dictionary<string, object?> _authContext;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public McpServer(ShapeConfig config, Dictionary<string, object?>? authContext = null)
    {
        _config = config;
        _serviceName = config.ServiceName;
        _authContext = authContext ?? new Dictionary<string, object?> { ["role"] = "admin" };
        IndexTools();
    }

    /// <summary>Override the auth context (used by HTTP transport after JwtGuard authorization).</summary>
    public void SetAuthContext(Dictionary<string, object?> authContext) => _authContext = authContext;

    /// <summary>
    /// Handle a single JSON-RPC message. Returns the response object, or
    /// <c>null</c> for notifications (no response expected by the client).
    /// </summary>
    public Dictionary<string, object?>? Handle(Dictionary<string, object?> message)
    {
        var id = message.GetValueOrDefault("id");
        var method = message.GetValueOrDefault("method")?.ToString() ?? "";
        var @params = message.TryGetValue("params", out var p) && p is Dictionary<string, object?> dict
            ? dict
            : new Dictionary<string, object?>();
        var isNotification = !message.ContainsKey("id");

        try
        {
            object? result = method switch
            {
                "initialize" => HandleInitialize(),
                "initialized" or "notifications/initialized" => null,
                "ping" => new Dictionary<string, object?>(),
                "tools/list" => HandleToolsList(),
                "tools/call" => HandleToolsCall(@params),
                "resources/list" => HandleResourcesList(),
                "resources/templates/list" => HandleResourceTemplatesList(),
                "resources/read" => HandleResourcesRead(@params),
                _ => throw new McpException(-32601, $"Method not found: {method}")
            };

            if (isNotification) return null;

            return new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result
            };
        }
        catch (McpException e)
        {
            if (isNotification) return null;
            return new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object?> { ["code"] = e.McpCode, ["message"] = e.Message }
            };
        }
        catch (Exception e)
        {
            if (isNotification) return null;
            return new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object?> { ["code"] = -32603, ["message"] = "Internal error: " + e.Message }
            };
        }
    }

    // ── Handshake ─────────────────────────────────────────────────────

    private Dictionary<string, object?> HandleInitialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?>(),
            ["resources"] = new Dictionary<string, object?>
            {
                ["subscribe"] = false,
                ["listChanged"] = false
            }
        },
        ["serverInfo"] = new Dictionary<string, object?>
        {
            ["name"] = "shape-mcp",
            ["title"] = $"Shape Backend ({_serviceName})",
            ["version"] = "1.0.0"
        }
    };

    // ── Tools ─────────────────────────────────────────────────────────

    private Dictionary<string, object?> HandleToolsList()
    {
        var tools = new LlmToolGenerator().GenerateAnthropic(
            _config.Runtime.Entities, _config.Masks, _config.Runtime.Commands);

        var role = _authContext.GetValueOrDefault("role")?.ToString() ?? "";
        var visible = tools.Where(t => IsToolVisible(t["name"]?.ToString() ?? "", role)).ToList();

        return new Dictionary<string, object?> { ["tools"] = visible };
    }

    private Dictionary<string, object?> HandleToolsCall(Dictionary<string, object?> @params)
    {
        var name = @params.GetValueOrDefault("name")?.ToString() ?? "";
        var arguments = @params.TryGetValue("arguments", out var a) && a is Dictionary<string, object?> ad
            ? ad
            : new Dictionary<string, object?>();

        if (!_toolIndex.TryGetValue(name, out var entry))
            return ErrorResult($"Unknown tool '{name}'. Use tools/list to see available tools.");

        try
        {
            object payload = entry.Kind switch
            {
                "read" => CallReadMask(entry.Mask, arguments),
                "mutate" => CallMutate(entry.Mask, arguments),
                "command" => CallCommand(entry.Mask, arguments),
                _ => throw new McpException(-32602, "Unknown tool kind: " + entry.Kind)
            };
            return SuccessResult(payload);
        }
        catch (ApiException e)
        {
            // Pass Shape's self-documenting envelope through as the tool's
            // error content — agent gets `expected` / `example` / `hint` in
            // the same response.
            var envelope = new Dictionary<string, object?>
            {
                ["code"] = e.ApiCode,
                ["message"] = e.Message
            };
            if (e.Details != null)
                foreach (var (k, v) in e.Details) envelope[k] = v;
            return ErrorResult(JsonSerializer.Serialize(envelope, PrettyJson));
        }
    }

    private Dictionary<string, object?> CallReadMask(string maskName, Dictionary<string, object?> arguments)
    {
        using var database = new Database(_config.Db);
        var registry = new MaskRegistry(_config.Masks);
        var validators = new ValidatorRegistry(_config.Validators);
        var executor = new BatchExecutor(database, registry, validators, _config.Tenancy, _config.Scopes);

        var queryResult = executor.Execute(
            new List<Dictionary<string, object?>>
            {
                new() { ["ref"] = "mcp", ["sql"] = maskName, ["params"] = arguments }
            },
            _authContext);

        var first = (queryResult["results"] as List<Dictionary<string, object?>>)?.FirstOrDefault();
        if (first == null)
            return new Dictionary<string, object?> { ["rows"] = new List<object?>(), ["count"] = 0 };

        return new Dictionary<string, object?>
        {
            ["columns"] = (first["meta"] as Dictionary<string, object?>)?["columns"] ?? new List<string>(),
            ["rows"] = first["rows"] ?? new List<object?[]>(),
            ["count"] = first["count"] ?? 0
        };
    }

    private Dictionary<string, object?> CallMutate(string table, Dictionary<string, object?> arguments)
    {
        var action = arguments.GetValueOrDefault("action")?.ToString() ?? "";
        var mutation = new Dictionary<string, object?>
        {
            ["ref"] = "mcp",
            ["table"] = table,
            ["action"] = action
        };
        if (arguments.TryGetValue("data", out var data) && data is Dictionary<string, object?>)
            mutation["data"] = data;
        if (arguments.TryGetValue("filter", out var filter) && filter is Dictionary<string, object?>)
            mutation["filter"] = filter;

        using var database = new Database(_config.Db);
        var registry = new MaskRegistry(_config.Masks);
        var validators = new ValidatorRegistry(_config.Validators);
        var executor = new BatchExecutor(database, registry, validators, _config.Tenancy, _config.Scopes);

        var result = executor.ExecuteMutations(
            new List<Dictionary<string, object?>> { mutation }, _authContext, useTransaction: false);
        return (result["results"] as List<Dictionary<string, object?>>)?.FirstOrDefault()
            ?? new Dictionary<string, object?> { ["action"] = action, ["table"] = table, ["affected"] = 0 };
    }

    private Dictionary<string, object?> CallCommand(string commandName, Dictionary<string, object?> arguments)
    {
        using var database = new Database(_config.Db);
        var registry = new MaskRegistry(_config.Masks);
        var validators = new ValidatorRegistry(_config.Validators);
        var batch = new BatchExecutor(database, registry, validators, _config.Tenancy, _config.Scopes);
        var executor = new CommandExecutor(database, batch, _config.Commands, _config.CommandRoles);
        return executor.Execute(commandName, arguments, _authContext);
    }

    // ── Resources ─────────────────────────────────────────────────────

    private Dictionary<string, object?> HandleResourcesList()
    {
        var resources = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["uri"] = "shape://README.md",
                ["name"] = "Backend overview",
                ["description"] = "Narrated, human-readable guide to every entity, mask, command, and auth flow in this backend. Attach as context once per session.",
                ["mimeType"] = "text/markdown"
            },
            new()
            {
                ["uri"] = "shape://manifest",
                ["name"] = "Machine-readable manifest",
                ["description"] = "Full Shape manifest: entities, fields, masks, roles, soft-delete columns, descriptions.",
                ["mimeType"] = "application/json"
            },
            new()
            {
                ["uri"] = "shape://tools",
                ["name"] = "Tool list as JSON",
                ["description"] = "The same tools as tools/list, in Anthropic-tool-call format.",
                ["mimeType"] = "application/json"
            }
        };

        foreach (var meta in _config.Runtime.Entities)
        {
            resources.Add(new Dictionary<string, object?>
            {
                ["uri"] = "shape://entities/" + meta.Table,
                ["name"] = "Entity: " + meta.Table,
                ["description"] = $"Field-level detail, FKs, validators, and roles for `{meta.Table}`.",
                ["mimeType"] = "application/json"
            });
        }

        return new Dictionary<string, object?> { ["resources"] = resources };
    }

    private Dictionary<string, object?> HandleResourceTemplatesList() => new()
    {
        ["resourceTemplates"] = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["uriTemplate"] = "shape://entities/{table}",
                ["name"] = "Entity drill-down",
                ["description"] = "Per-entity field, FK, validator, and role detail.",
                ["mimeType"] = "application/json"
            }
        }
    };

    private Dictionary<string, object?> HandleResourcesRead(Dictionary<string, object?> @params)
    {
        var uri = @params.GetValueOrDefault("uri")?.ToString() ?? "";

        if (uri == "shape://README.md")
        {
            var text = ReadmeBuilder.Build(
                _config.Runtime.Entities, _config.Masks, _config.Runtime.Commands, _config.Auth, _serviceName);
            return ResourceText(uri, "text/markdown", text);
        }

        // SECURITY-REVIEW M8: the resource surface must be role-filtered the same
        // way tools/list is. Otherwise a low-priv (or, on driver:none, anonymous)
        // caller reads the full admin/secret-table map from shape://manifest /
        // shape://tools / shape://entities and defeats the mask role gate.
        var role = _authContext.GetValueOrDefault("role")?.ToString() ?? "";
        var visibleMasks = VisibleMasks(role);
        var visibleCommands = _config.Runtime.Commands.Where(c => IsCommandAllowed(c, role)).ToList();
        var visibleEntities = _config.Runtime.Entities
            .Where(e => visibleMasks.Keys.Any(m => m == e.Table || m.StartsWith(e.Table + ".", StringComparison.Ordinal)))
            .ToList();

        if (uri == "shape://manifest")
        {
            var manifest = new ManifestGenerator().Generate(
                visibleEntities, visibleMasks, visibleCommands);
            return ResourceText(uri, "application/json", JsonSerializer.Serialize(manifest, PrettyJson));
        }

        if (uri == "shape://tools")
        {
            var tools = new LlmToolGenerator().GenerateAnthropic(
                visibleEntities, visibleMasks, visibleCommands);
            return ResourceText(uri, "application/json",
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["tools"] = tools }, PrettyJson));
        }

        const string entityPrefix = "shape://entities/";
        if (uri.StartsWith(entityPrefix, StringComparison.Ordinal))
        {
            var tableName = uri[entityPrefix.Length..];
            var meta = visibleEntities.FirstOrDefault(e => e.Table == tableName);
            if (meta == null)
                throw new McpException(-32602, $"Unknown entity: {tableName}");
            var payload = DescribeEntity(meta, visibleMasks);
            return ResourceText(uri, "application/json", JsonSerializer.Serialize(payload, PrettyJson));
        }

        throw new McpException(-32602, $"Unknown resource URI: {uri}");
    }

    private static Dictionary<string, object?> DescribeEntity(EntityMeta meta, Dictionary<string, MaskConfig> masks)
    {
        var fields = meta.Fields.Select(f => new Dictionary<string, object?>
        {
            ["name"] = f.Name,
            ["type"] = f.DbType,
            ["nullable"] = f.Nullable,
            ["primary"] = f.IsPrimary,
            // Manual-strategy PK reads as auto-assigned (§2.4) — agents must not send it.
            ["autoIncrement"] = f.IsAutoIncrement || (f.IsPrimary && meta.PkStrategy == "manual"),
            ["fk"] = f.FkEntityType?.Name,
            ["fkColumn"] = f.FkColumn,
            ["validators"] = f.Validators,
            ["hasDefault"] = f.HasDefault,
            ["default"] = f.DefaultValue
        }).ToList();

        var entityMasks = new Dictionary<string, object?>();
        foreach (var (name, mask) in masks)
        {
            if (!name.StartsWith(meta.Table + ".", StringComparison.Ordinal) && name != meta.Table)
                continue;
            entityMasks[name] = new Dictionary<string, object?>
            {
                ["params"] = mask.Params?.Keys.ToList() ?? new List<string>(),
                ["writable"] = mask.Writable,
                ["description"] = mask.Description
            };
        }

        return new Dictionary<string, object?>
        {
            ["table"] = meta.Table,
            ["class"] = meta.ClrType.Name,
            ["writable"] = meta.Writable,
            ["softDelete"] = meta.SoftDeleteColumn,
            ["roles"] = new Dictionary<string, object?>
            {
                ["read"] = meta.ReadRoles,
                ["write"] = meta.WriteRoles
            },
            ["fields"] = fields,
            ["masks"] = entityMasks
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private void IndexTools()
    {
        foreach (var (name, mask) in _config.Masks)
        {
            if (mask.Writable)
                _toolIndex[ToolName(name) + "_mutate"] = new ToolEntry(name, "mutate");
            else
                _toolIndex[ToolName(name)] = new ToolEntry(name, "read");
        }
        foreach (var cmd in _config.Runtime.Commands)
            _toolIndex[ToolName(cmd)] = new ToolEntry(cmd, "command");
    }

    private static string ToolName(string maskName)
    {
        var name = maskName.Replace('.', '_');
        name = Regex.Replace(name, "[^A-Za-z0-9_-]", "_");
        return name.Length > 64 ? name[..64] : name;
    }

    private bool IsToolVisible(string toolName, string role)
    {
        if (!_toolIndex.TryGetValue(toolName, out var entry))
            return false;
        if (entry.Kind == "command")
            return IsCommandAllowed(entry.Mask, role);
        if (!_config.Masks.TryGetValue(entry.Mask, out var mask))
            return true;
        var allowed = mask.Roles;
        if (allowed == null || allowed.Count == 0)
            return true;
        return role.Length > 0 && allowed.Contains(role);
    }

    /// <summary>
    /// SECURITY-REVIEW M9: a command is visible/invocable only when the caller's
    /// role satisfies its <see cref="ShapeConfig.CommandRoles"/> entry. No entry
    /// = unrestricted (mirrors the mask-role default).
    /// </summary>
    private bool IsCommandAllowed(string commandName, string role)
    {
        var allowed = _config.CommandRoles?.GetValueOrDefault(commandName);
        if (allowed == null || allowed.Count == 0)
            return true;
        return role.Length > 0 && allowed.Contains(role);
    }

    /// <summary>
    /// The subset of masks the given role may see — same gate as
    /// <see cref="MaskRegistry.Resolve"/> (empty/absent Roles = open). Used to
    /// role-filter the MCP resource surface (SECURITY-REVIEW M8).
    /// </summary>
    private Dictionary<string, MaskConfig> VisibleMasks(string role)
    {
        var result = new Dictionary<string, MaskConfig>();
        foreach (var (name, mask) in _config.Masks)
        {
            var allowed = mask.Roles;
            if (allowed == null || allowed.Count == 0 || (role.Length > 0 && allowed.Contains(role)))
                result[name] = mask;
        }
        return result;
    }

    private static Dictionary<string, object?> SuccessResult(object payload)
    {
        var text = payload is string s ? s : JsonSerializer.Serialize(payload, PrettyJson);
        return new Dictionary<string, object?>
        {
            ["content"] = new List<Dictionary<string, object?>>
            {
                new() { ["type"] = "text", ["text"] = text }
            },
            ["isError"] = false
        };
    }

    private static Dictionary<string, object?> ErrorResult(string text) => new()
    {
        ["content"] = new List<Dictionary<string, object?>>
        {
            new() { ["type"] = "text", ["text"] = text }
        },
        ["isError"] = true
    };

    private static Dictionary<string, object?> ResourceText(string uri, string mime, string text) => new()
    {
        ["contents"] = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["uri"] = uri,
                ["mimeType"] = mime,
                ["text"] = text
            }
        }
    };

    private sealed record ToolEntry(string Mask, string Kind);
}
