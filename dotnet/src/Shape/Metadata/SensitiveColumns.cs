namespace Shape.Metadata;

/// <summary>
/// Central policy for columns that must not be exposed or written through the
/// generic auto-CRUD surface. Two tiers:
/// <list type="bullet">
///   <item><see cref="IsReadSensitive"/> — secret material (password / token /
///   secret / salt families). Stripped from admin read masks.</item>
///   <item><see cref="IsWriteSensitive"/> — the read-sensitive set PLUS
///   privilege / account-state columns (role, is_active, is_admin, …). Excluded
///   from a writable mask's <c>writable_columns</c> allowlist so a client cannot
///   escalate its own role or overwrite a password hash through the generic
///   <c>mutation</c> op.</item>
/// </list>
/// Name-heuristic by necessity (DB-first mode has no per-field access metadata);
/// it is a safe default, not the last line of defense — the allowlist itself
/// rejects any column not explicitly listed.
/// </summary>
public static class SensitiveColumns
{
    private static readonly HashSet<string> ReadExactBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "secret", "password", "salt"
    };

    private static readonly string[] ReadBlockedPrefixes =
    {
        "password_", "secret_", "salt_", "mfa_", "totp_"
    };

    private static readonly string[] ReadBlockedSuffixes =
    {
        "_hash", "_secret", "_token"
    };

    /// <summary>
    /// Privilege / account-state columns that are legitimate to read but never
    /// legitimate for a caller to set through auto-CRUD. Writing them is the
    /// role-escalation / account-takeover vector (SECURITY-REVIEW H1).
    /// </summary>
    private static readonly HashSet<string> WriteExactBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "role", "roles", "is_active", "active", "is_admin", "is_superuser",
        "is_staff", "is_superadmin", "is_root", "superuser", "admin",
        "permissions", "perms", "scopes", "email_verified", "verified"
    };

    /// <summary>True when the column holds secret material and must be hidden from reads.</summary>
    public static bool IsReadSensitive(string name)
    {
        var lower = name.ToLowerInvariant();
        if (ReadExactBlocked.Contains(lower)) return true;
        foreach (var prefix in ReadBlockedPrefixes)
            if (lower.StartsWith(prefix, StringComparison.Ordinal)) return true;
        foreach (var suffix in ReadBlockedSuffixes)
            if (lower.EndsWith(suffix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// True when the column must be excluded from an auto-generated writable
    /// allowlist: secret material OR a privilege/account-state column.
    /// </summary>
    public static bool IsWriteSensitive(string name)
    {
        if (IsReadSensitive(name)) return true;
        return WriteExactBlocked.Contains(name.ToLowerInvariant());
    }
}
