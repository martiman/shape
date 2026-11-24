using System.Text;
using System.Text.RegularExpressions;
using Shape.Auth;
using Shape.Core;
using Shape.Metadata;

namespace Shape.Mcp;

/// <summary>
/// Builds the <c>shape://README.md</c> resource — a narrated, human-readable
/// overview of the entire backend that an agent can attach as context with
/// one click in Claude Desktop / Cursor / Cline.
///
/// Most MCP servers expose raw tools and let the LLM figure things out from
/// names. Shape exposes a <em>guided tour</em>: which entities exist, what
/// they relate to, what's writable, what's soft-deleted, what commands exist,
/// how auth works.
/// </summary>
public static class ReadmeBuilder
{
    public static string Build(
        IEnumerable<EntityMeta> entities,
        Dictionary<string, MaskConfig> masks,
        IEnumerable<string> commands,
        AuthConfig authConfig,
        string serviceName = "shape")
    {
        var entityList = entities.ToList();
        var commandList = commands.ToList();

        var writableTables = new List<string>();
        var softDeleteTables = new List<string>();
        var entityRows = new List<string>();

        foreach (var meta in entityList)
        {
            if (meta.Writable) writableTables.Add(meta.Table);
            if (!string.IsNullOrEmpty(meta.SoftDeleteColumn)) softDeleteTables.Add(meta.Table);
            entityRows.Add(RenderEntity(meta, masks));
        }

        var standalone = CollectStandaloneMasks(entityList, masks);

        var sb = new StringBuilder();
        sb.AppendLine($"# {serviceName} Backend").AppendLine();
        sb.AppendLine(IntroBlurb(entityList.Count, writableTables.Count, softDeleteTables.Count, standalone.Count, commandList.Count));
        sb.AppendLine();

        sb.AppendLine("## How to call this backend").AppendLine();
        sb.AppendLine(CallingGuide(authConfig.Driver));
        sb.AppendLine();

        if (entityList.Count > 0)
        {
            sb.AppendLine($"## Entities ({entityList.Count})").AppendLine();
            foreach (var row in entityRows) sb.AppendLine(row);
        }

        if (standalone.Count > 0)
        {
            sb.AppendLine($"## Custom masks ({standalone.Count})").AppendLine();
            foreach (var (name, info) in standalone)
            {
                var desc = !string.IsNullOrEmpty(info.Description) ? info.Description : "Hand-written mask.";
                sb.AppendLine($"- **`{name}`** — {desc}");
                if (info.Params is { Count: > 0 } p)
                    sb.AppendLine($"  - Params: {string.Join(", ", p.Keys)}");
            }
            sb.AppendLine();
        }

        if (commandList.Count > 0)
        {
            sb.AppendLine($"## Commands ({commandList.Count})").AppendLine();
            foreach (var cmd in commandList)
                sb.AppendLine($"- `{cmd}` — custom business-logic operation. Call via the `{CommandToolName(cmd)}` tool.");
            sb.AppendLine();
        }

        sb.AppendLine("## Notes for agents").AppendLine();
        sb.Append(AgentNotes(softDeleteTables));

        return sb.ToString();
    }

    private static string RenderEntity(EntityMeta meta, Dictionary<string, MaskConfig> masks)
    {
        var table = meta.Table;
        var writable = meta.Writable ? " *(writable)*" : "";
        var soft = !string.IsNullOrEmpty(meta.SoftDeleteColumn) ? " *(soft-delete via `deleted_at`)*" : "";

        var lines = new List<string> { $"### `{table}`{writable}{soft}" };

        var cols = new List<string>();
        foreach (var f in meta.Fields)
        {
            var marker = "";
            if (f.IsPrimary) marker += " (pk)";
            if (f.IsFk) marker += $" (fk → {f.FkEntityType?.Name ?? "?"})";
            cols.Add($"`{f.Name}`{marker}");
        }
        if (cols.Count > 0)
            lines.Add("- Columns: " + string.Join(", ", cols));

        if (meta.ReadRoles.Count > 0 || meta.WriteRoles.Count > 0)
        {
            var roles = new List<string>();
            if (meta.ReadRoles.Count > 0) roles.Add("read = " + string.Join("/", meta.ReadRoles));
            if (meta.WriteRoles.Count > 0) roles.Add("write = " + string.Join("/", meta.WriteRoles));
            lines.Add("- Roles: " + string.Join(", ", roles));
        }

        if (meta.TenantScoped)
            lines.Add($"- Tenant scope: limited to the caller's tenant via `{meta.TenantColumn ?? "tenant_id"}`.");

        var entityMasks = masks
            .Where(m => m.Key.StartsWith(table + ".", StringComparison.Ordinal) || m.Key == table)
            .ToList();
        if (entityMasks.Count > 0)
        {
            lines.Add("- Endpoints:");
            foreach (var (name, mask) in entityMasks)
            {
                var desc = !string.IsNullOrEmpty(mask.Description) ? " — " + mask.Description : "";
                lines.Add($"  - `{name}`{desc}");
            }
        }

        return string.Join("\n", lines);
    }

    private static List<KeyValuePair<string, MaskConfig>> CollectStandaloneMasks(
        List<EntityMeta> entities, Dictionary<string, MaskConfig> masks)
    {
        var entityTables = entities.Select(e => e.Table).ToHashSet(StringComparer.Ordinal);
        var standalone = new List<KeyValuePair<string, MaskConfig>>();
        foreach (var (name, mask) in masks)
        {
            var dot = name.IndexOf('.');
            var table = dot < 0 ? name : name[..dot];
            if (!entityTables.Contains(table))
                standalone.Add(new KeyValuePair<string, MaskConfig>(name, mask));
        }
        return standalone;
    }

    private static string IntroBlurb(int entityCount, int writableCount, int softCount, int standaloneCount, int commandCount)
    {
        var bits = new List<string> { $"{entityCount} introspected entities ({writableCount} writable, {softCount} soft-deletable)" };
        if (standaloneCount > 0) bits.Add($"{standaloneCount} hand-written masks");
        if (commandCount > 0) bits.Add($"{commandCount} custom commands");
        return $"Live overview of this Shape backend. Surface area: {string.Join(", ", bits)}.";
    }

    private static string CallingGuide(string authDriver)
    {
        var auth = authDriver == "jwt"
            ? "**Auth:** JWT. Call the `auth_token` tool (or POST `op:'auth.token'`) to obtain an access token; pass it as `Authorization: Bearer <token>` for subsequent calls."
            : "**Auth:** none configured. Calls are unauthenticated.";

        return
            "- **Read** an entity: call the `<table>_list`, `<table>_get`, `<table>_getMany`, or `<table>_by<FK>` tool.\n" +
            "- **Write** an entity: call the `<table>_mutate` tool with `action: 'insert' | 'update' | 'delete'` and either `data` or `filter`.\n" +
            "- **Errors** carry `expected` + `example` + `hint` — when a call fails, the response tells you exactly what it should have looked like; correct and retry once, do not re-fetch the manifest.\n" +
            "- **Soft-delete tables** automatically filter `deleted_at IS NULL` on reads. DELETE on those tables sets `deleted_at` instead of removing the row. To restore: `update` with `data: { deleted_at: null }`.\n\n" +
            auth;
    }

    private static string AgentNotes(List<string> softDeleteTables)
    {
        var lines = new List<string>
        {
            "- All tools enforce role-based authorization at runtime — a tool you can list is one your role can call.",
            "- Mask names are stable across versions; rely on them for orchestration. Tool names mirror mask names with `.` replaced by `_`.",
            "- Tenant-scoped tables are automatically filtered by the caller's tenant; unscoped roles receive the unscoped variants when configured.",
            "- Reads return rows in column-major (`columns` + `rows`) format unless the tool description says otherwise.",
            "- Writes return `{ ref, action, table, affected }` per mutation.",
            "- Errors carry `expected` / `example` / `hint` — read them and retry once. **Do not** re-fetch this README or the manifest after a parameter error."
        };
        if (softDeleteTables.Count > 0)
            lines.Add("- Soft-deletable tables: " + string.Join(", ", softDeleteTables) + ".");
        lines.Add("");
        lines.Add("## Framework reference");
        lines.Add("");
        lines.Add("This README describes *this specific backend*. For framework-level documentation — Shape conventions, error envelope shape, MCP protocol, soft-delete semantics, common mistakes — read **`AGENTS.md`** in the Shape repository. It is the single canonical entry point for agents working with Shape and stays under 5k tokens by design.");
        return string.Join("\n", lines) + "\n";
    }

    private static string CommandToolName(string command) =>
        Regex.Replace(command.Replace('.', '_'), "[^A-Za-z0-9_-]", "_");
}
