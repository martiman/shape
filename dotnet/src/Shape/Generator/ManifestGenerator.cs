using Shape.Core;
using Shape.Metadata;

namespace Shape.Generator;

/// <summary>
/// Renders the <c>?manifest</c> response — the document agents and admin UIs
/// fetch to discover entities, masks, validators, and commands. Includes
/// soft-delete and audit-column metadata when detected, and per-mask
/// description/defaultLimit when set.
/// </summary>
public sealed class ManifestGenerator
{
    public Dictionary<string, object?> Generate(
        IEnumerable<EntityMeta> entities,
        Dictionary<string, MaskConfig> masks,
        IEnumerable<string> commands)
    {
        var entityList = new List<Dictionary<string, object?>>();
        var claimedMasks = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            var fields = new List<Dictionary<string, object?>>();
            foreach (var field in entity.Fields)
            {
                var item = new Dictionary<string, object?>
                {
                    ["name"] = field.Name,
                    ["type"] = DisplayType(field),
                    ["nullable"] = field.Nullable
                };

                if (field.IsPrimary)
                    item["primary"] = true;
                // Unified "generated" flag (§2.4): a manual-strategy PK is presented
                // exactly like an auto-increment column — the server assigns it, the
                // client never sends it. Agents reading the manifest key off this.
                if (field.IsAutoIncrement || (field.IsPrimary && entity.PkStrategy == "manual"))
                    item["autoIncrement"] = true;
                if (field.IsTenantColumn)
                    item["readOnly"] = true;
                if (field.IsFk)
                    item["fk"] = field.FkEntityType?.Name;
                if (field.Validators.Count > 0)
                    item["validators"] = field.Validators;
                if (field.EnumValues is { Count: > 0 })
                    item["enum"] = field.EnumValues;
                if (field.HasDefault)
                    item["default"] = field.DefaultValue;
                if (!string.IsNullOrEmpty(field.Description))
                    item["description"] = field.Description;

                fields.Add(item);
            }

            var entityMasks = new Dictionary<string, object?>();
            foreach (var (name, mask) in masks)
            {
                if (!name.StartsWith(entity.Table + ".", StringComparison.Ordinal) &&
                    !string.Equals(name, entity.Table, StringComparison.Ordinal))
                {
                    continue;
                }

                claimedMasks.Add(name);
                entityMasks[name] = MaskSummary(mask);
            }

            var entry = new Dictionary<string, object?>
            {
                ["name"] = entity.ClrType.Name,
                ["table"] = entity.Table,
                ["writable"] = entity.Writable,
                ["fields"] = fields,
                ["masks"] = entityMasks,
                ["roles"] = new Dictionary<string, object?>
                {
                    ["read"] = entity.ReadRoles,
                    ["write"] = entity.WriteRoles
                }
            };

            if (entity.SoftDeleteColumn != null)
                entry["softDelete"] = entity.SoftDeleteColumn;
            if (entity.AuditColumns.Count > 0)
                entry["audit"] = new Dictionary<string, object?>(
                    entity.AuditColumns.ToDictionary(p => p.Key, p => (object?)p.Value));
            if (!string.IsNullOrEmpty(entity.Description))
                entry["description"] = entity.Description;
            if (entity.UniqueConstraints.Count > 0)
                entry["uniqueConstraints"] = entity.UniqueConstraints;
            if (entity.TenantScoped)
                entry["tenantScoped"] = true;
            // Dimension scopes the entity carries (ROW-SCOPES-PLAN §2.5) — the
            // cross-runtime camelCase key, mirroring `tenantScoped`.
            if (entity.ScopeColumns is { Count: > 0 })
                entry["rowScopes"] = entity.ScopeColumns.ToList();

            // Surface the resolved writable mode explicitly when it deviates
            // from the default. Lets agents reading the manifest see the
            // honest contract: an "append_only" entity won't accept UPDATEs
            // even though `writable` is true.
            var effective = entity.EffectiveWritableMode();
            if (effective != "full" && effective != "none")
                entry["writableMode"] = effective;

            entityList.Add(entry);
        }

        var result = new Dictionary<string, object?>
        {
            ["entities"] = entityList,
            ["commands"] = commands.ToList()
        };

        var standaloneMasks = new Dictionary<string, object?>();
        foreach (var (name, mask) in masks)
        {
            if (claimedMasks.Contains(name))
                continue;

            var summary = MaskSummary(mask);
            summary["roles"] = mask.Roles ?? new List<string>();
            standaloneMasks[name] = summary;
        }

        if (standaloneMasks.Count > 0)
            result["masks"] = standaloneMasks;

        // Uploads block is reserved for Phase 3; emit an empty map so downstream
        // tooling can rely on its presence.
        result["uploads"] = new Dictionary<string, object?>();

        return result;
    }

    private static Dictionary<string, object?> MaskSummary(MaskConfig mask)
    {
        var summary = new Dictionary<string, object?>
        {
            ["params"] = mask.Params?.Keys.ToList() ?? new List<string>(),
            ["writable"] = mask.Writable
        };
        if (!string.IsNullOrEmpty(mask.Description))
            summary["description"] = mask.Description;
        if (mask.DefaultLimit > 0)
            summary["defaultLimit"] = mask.DefaultLimit;
        if (!string.IsNullOrEmpty(mask.SoftDelete))
            summary["softDelete"] = mask.SoftDelete;
        if (mask.Audit is { Count: > 0 })
            summary["audit"] = new Dictionary<string, object?>(
                mask.Audit.ToDictionary(p => p.Key, p => (object?)p.Value));
        if (mask.Relations is { Count: > 0 })
            summary["relations"] = mask.Relations.ToDictionary(
                p => p.Key,
                p => (object?)new Dictionary<string, object?> { ["fk_columns"] = p.Value.FkColumns });
        if (!string.IsNullOrEmpty(mask.WritableMode))
            summary["writableMode"] = mask.WritableMode;
        if (mask.TenantScope != null)
            summary["tenant_scope"] = mask.TenantScope;
        // Dimension scoping carrier (ROW-SCOPES-PLAN §2.5) — snake_case key
        // shared with PHP: { "<column>": { "multi": bool } }.
        if (mask.RowScopes is Dictionary<string, RowScopeMeta> rowScopes && rowScopes.Count > 0)
            summary["row_scopes"] = rowScopes.ToDictionary(
                p => p.Key,
                p => (object?)new Dictionary<string, object?> { ["multi"] = p.Value.Multi });
        // SECURITY-REVIEW M10: never emit `sql_unscoped`. It is the predicate-free
        // cross-tenant query text (and full column list) for scoped/admin masks;
        // exposing it in the manifest — which is reachable unauthenticated in some
        // configs — leaks internal query structure. The PHP manifest already omits
        // all raw SQL; this keeps the two runtimes aligned.
        // Keys byte-identical to the PHP runtime (`pk`, `pk_strategy`) so either
        // stack can read the other's manifest.
        if (!string.IsNullOrEmpty(mask.Pk))
            summary["pk"] = mask.Pk;
        if (!string.IsNullOrEmpty(mask.PkStrategy))
            summary["pk_strategy"] = mask.PkStrategy;
        // Per-operation write roles (cross-runtime snake_case).
        if (mask.RolesByAction is { Count: > 0 })
            summary["roles_by_action"] = mask.RolesByAction.ToDictionary(
                p => p.Key, p => (object?)p.Value);
        return summary;
    }

    private static string DisplayType(FieldMeta field)
    {
        var type = field.DbType;
        if (field.Precision.HasValue && field.Scale.HasValue)
            type += $"({field.Precision.Value},{field.Scale.Value})";
        else if (field.Length.HasValue)
            type += $"({field.Length.Value})";

        if (field.Unsigned)
            type += " unsigned";

        return type;
    }
}
