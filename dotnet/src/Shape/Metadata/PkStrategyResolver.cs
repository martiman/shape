using Shape.Core;

namespace Shape.Metadata;

/// <summary>
/// Resolves each entity's PK assignment strategy from <see cref="ShapeConfig.PkAssignment"/>
/// plus the introspected schema (MANUAL-PK-PLAN.md §2.2). Runs once at build time in
/// <see cref="Shape.ShapeApp"/>; the decision travels to the runtime in the writable
/// mask (<see cref="MaskConfig.Pk"/> / <see cref="MaskConfig.PkStrategy"/>).
/// </summary>
public static class PkStrategyResolver
{
    public const string Auto = "auto";
    public const string Db = "db";
    public const string Manual = "manual";

    /// <summary>
    /// Column types the auto rule accepts as an allocatable int-family PK.
    /// decimal/numeric qualify only with scale 0 (legacy MSSQL schemas often
    /// use <c>numeric(9,0)</c> PKs) — checked separately in <see cref="IsIntFamilyPk"/>.
    /// </summary>
    private static readonly HashSet<string> IntTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "integer", "bigint", "smallint", "tinyint", "mediumint"
    };

    /// <summary>Reject config values outside <c>auto|db|manual</c> — fail at boot, not on first insert.</summary>
    public static void Validate(IReadOnlyDictionary<string, string>? pkAssignment)
    {
        if (pkAssignment == null) return;
        foreach (var (table, mode) in pkAssignment)
        {
            if (mode is Auto or Db or Manual) continue;
            throw new ApiException(
                "CONFIG_INVALID",
                $"PkAssignment['{table}'] has invalid value '{mode}'.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = "one of: 'auto', 'db', 'manual'",
                    ["hint"] = "Use '*' as the database-wide default key and table names for per-table overrides."
                });
        }
    }

    /// <summary>Configured mode for one table: exact key wins, then <c>"*"</c>, then <c>"auto"</c>.</summary>
    public static string ModeFor(IReadOnlyDictionary<string, string>? pkAssignment, string table)
    {
        if (pkAssignment == null) return Auto;
        if (pkAssignment.TryGetValue(table, out var mode)) return mode;
        return pkAssignment.GetValueOrDefault("*", Auto);
    }

    /// <summary>
    /// True when the config forces <c>manual</c> for <paramref name="table"/> (exact key
    /// or <c>"*"</c>). The auth insert paths (users / refresh_tokens) use this instead of
    /// <see cref="Apply"/> — auto-detection needs introspection, which the per-request
    /// auth path deliberately avoids.
    /// </summary>
    public static bool IsForcedManual(IReadOnlyDictionary<string, string>? pkAssignment, string table)
        => ModeFor(pkAssignment, table) == Manual;

    /// <summary>Int family per plan §2.2: int/bigint/smallint/tinyint (+ spellings) or a scale-0 decimal/numeric.</summary>
    public static bool IsIntFamilyPk(FieldMeta field)
    {
        var type = field.DbType ?? "";
        if (IntTypes.Contains(type)) return true;
        return (type.Equals("decimal", StringComparison.OrdinalIgnoreCase)
                || type.Equals("numeric", StringComparison.OrdinalIgnoreCase))
               && field.Scale == 0;
    }

    /// <summary>
    /// Stamp <see cref="EntityMeta.PkStrategy"/> on every entity. Auto rule: single-column
    /// int-family PK that is neither auto-increment nor defaulted → <c>manual</c> (nothing
    /// assigns it, so the framework must); anything else → <c>db</c>. Forcing <c>manual</c>
    /// onto a composite / non-int / missing PK throws — fail loud at build time, never guess.
    /// A <c>[GeneratedId]</c>-seeded <c>"manual"</c> counts as forced unless the config
    /// explicitly overrides the table with <c>db</c>/<c>manual</c>.
    /// </summary>
    public static void Apply(IEnumerable<EntityMeta> entities, IReadOnlyDictionary<string, string>? pkAssignment)
    {
        Validate(pkAssignment);

        foreach (var entity in entities)
        {
            var mode = ModeFor(pkAssignment, entity.Table);
            if (mode == Auto && entity.PkStrategy == Manual)
                mode = Manual; // [GeneratedId] attribute — validate like an explicit force

            var pks = entity.Fields.Where(f => f.IsPrimary).ToList();

            if (mode == Manual)
            {
                if (pks.Count != 1 || !IsIntFamilyPk(pks[0]))
                {
                    var reason = pks.Count == 0 ? "has no primary key"
                        : pks.Count > 1 ? $"has a composite primary key ({string.Join(", ", pks.Select(p => p.Name))})"
                        : $"primary key '{pks[0].Name}' is not an int-family column ({pks[0].DbType})";
                    throw new ApiException(
                        "CONFIG_INVALID",
                        $"PkAssignment forces 'manual' on table '{entity.Table}', but it {reason}. Manual MAX(pk)+1 allocation needs a single int-family PK.",
                        500,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["table"] = entity.Table,
                            ["hint"] = "Use 'db' for this table (identity/trigger/sequence/client assigns) or fix the schema."
                        });
                }
                entity.PkStrategy = Manual;
            }
            else if (mode == Db)
            {
                entity.PkStrategy = Db;
            }
            else
            {
                var pk = pks.Count == 1 ? pks[0] : null;
                entity.PkStrategy = pk != null && IsIntFamilyPk(pk) && !pk.IsAutoIncrement && !pk.HasDefault
                    ? Manual
                    : Db;
            }

            // A manual PK behaves like a generated column externally — clients never
            // send it. Drop any schema-derived required rule (defensive: int-family
            // columns don't get notEmpty today, but the contract must not depend on that).
            if (entity.PkStrategy == Manual)
                entity.Fields.SingleOrDefault(f => f.IsPrimary)?.Validators.Remove("notEmpty");
        }
    }
}
