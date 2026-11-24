namespace Shape.Metadata;

/// <summary>
/// Derives validator rules from column metadata so devs do not have to
/// repeat what the schema already encodes:
/// <list type="bullet">
///   <item>NOT NULL string column (no default, not auto-increment) → <c>notEmpty</c></item>
///   <item><c>VARCHAR(N)</c> / <c>CHAR(N)</c> → <c>maxLen:N</c></item>
/// </list>
/// Manual validators from app config still apply on top — they extend the
/// auto-derived chain rather than replacing it.
/// </summary>
public static class AutoValidators
{
    private static readonly HashSet<string> StringTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "varchar", "char", "text", "tinytext", "mediumtext", "longtext",
        "character", "character varying",
        // SQL Server Unicode family — nvarchar is *the* default string type there.
        // nvarchar(max) reports CHARACTER_MAXIMUM_LENGTH = -1, which the MSSQL
        // introspector already normalizes to Length = null (no maxLen rule).
        "nvarchar", "nchar", "ntext"
    };

    /// <summary>
    /// Returns the auto-derived rule chain for a single field. Empty when the
    /// field's <c>DbType</c> isn't a string family.
    /// </summary>
    public static List<string> FromField(FieldMeta field)
    {
        var rules = new List<string>();
        // Tenant + scope discriminators are framework-stamped (never client-
        // required) — see ROW-SCOPES-PLAN §2.4.
        if (field.IsTenantColumn || field.IsScopeColumn)
            return rules;

        if (!StringTypes.Contains(field.DbType ?? ""))
            return rules;

        var required = !field.Nullable && !field.IsAutoIncrement && !field.HasDefault;
        if (required)
            rules.Add("notEmpty");

        if (field.Length is int len && len > 0)
            rules.Add("maxLen:" + len);

        return rules;
    }
}
