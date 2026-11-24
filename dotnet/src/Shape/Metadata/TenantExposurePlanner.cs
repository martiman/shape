using Shape.Core;

namespace Shape.Metadata;

/// <summary>
/// Classifies which tables a brownfield database should expose under tenancy
/// (brownfield adapter guide §2 "tenant relation planning"): a table is
/// tenant-owned iff it carries the tenancy column <b>and</b> that column is a
/// real foreign key to the registry table's PK — column-name coincidence alone
/// does not qualify. Feeds the access map so only verified tenant-linked
/// business tables become API surface; the registry itself, the app's
/// internal tables, and unlinked tables stay unexposed.
///
/// This replaces the hand-written FK scanning every brownfield adapter would
/// otherwise copy. Row-level scoping of the exposed tables is unchanged — the
/// standard <see cref="TenancyConfig"/> machinery handles it.
/// </summary>
public static class TenantExposurePlanner
{
    /// <summary>
    /// Return the sorted list of tenant-owned tables.
    /// </summary>
    /// <param name="introspector">Introspector over the live schema.</param>
    /// <param name="tenancy">The tenancy config — supplies the discriminator
    /// column and registry table names.</param>
    /// <param name="excludeTables">App-internal tables to skip regardless of
    /// their FKs (e.g. ASP.NET Identity tables, the app's own token store,
    /// migration bookkeeping). Case-insensitive.</param>
    public static List<string> PlanTables(
        DatabaseIntrospector introspector,
        TenancyConfig tenancy,
        IEnumerable<string>? excludeTables = null)
    {
        if (string.IsNullOrWhiteSpace(tenancy.Column) || string.IsNullOrWhiteSpace(tenancy.Table))
            throw new ApiException(
                "CONFIG_INVALID",
                "TenantExposurePlanner requires tenancy.column and tenancy.table.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Set TenancyConfig.Column (the discriminator) and TenancyConfig.Table (the registry) before planning exposure."
                });

        var excluded = new HashSet<string>(excludeTables ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var registryPk = "Id"; // matched case-insensitively below

        var tables = new List<string>();
        foreach (var table in introspector.ListTables().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // The registry is global by definition; internal tables are the
            // app's business, not API surface.
            if (string.Equals(table, tenancy.Table, StringComparison.OrdinalIgnoreCase) || excluded.Contains(table))
                continue;

            var columns = introspector.DescribeColumns(table);
            if (!columns.Any(c => string.Equals(c.Name, tenancy.Column, StringComparison.OrdinalIgnoreCase)))
                continue;

            // The column must be a *verified* FK to the registry PK — a column
            // that merely shares the name proves nothing about ownership.
            var foreignKeys = introspector.DescribeForeignKeys(table);
            var fk = foreignKeys.FirstOrDefault(pair =>
                string.Equals(pair.Key, tenancy.Column, StringComparison.OrdinalIgnoreCase)).Value;
            if (fk == null)
                continue;
            if (!string.Equals(fk.ReferencedTable, tenancy.Table, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(fk.ReferencedColumn, registryPk, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fk.ReferencedColumn, "id", StringComparison.OrdinalIgnoreCase))
                continue;

            tables.Add(table);
        }

        tables.Sort(StringComparer.Ordinal);
        return tables;
    }
}
