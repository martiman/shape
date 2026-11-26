using Shape.Auth;
using Shape.Metadata;
using Shape.Upload;

namespace Shape.Core;

public sealed class ShapeConfig
{
    public string ServiceName { get; set; } = "shape";
    public string Endpoint { get; set; } = "/";

    public DatabaseConfig Db { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public Dictionary<string, MaskConfig> Masks { get; set; } = new();
    public Dictionary<string, ValidatorConfig>? Validators { get; set; }
    public Dictionary<string, Func<Dictionary<string, object?>, CommandContext, Dictionary<string, object?>>>? Commands { get; set; }

    /// <summary>
    /// Per-command role allowlist (SECURITY-REVIEW M9). Keyed by command name;
    /// a caller's role must be in the list to list or invoke that command
    /// (enforced in <see cref="CommandExecutor"/> and the MCP tool surface).
    /// A command with no entry is unrestricted — mirrors the mask-role default
    /// (no <c>Roles</c> = open) so existing commands keep working. Set an entry
    /// (e.g. <c>["admin"]</c>) to lock a privileged command down.
    /// </summary>
    public Dictionary<string, List<string>>? CommandRoles { get; set; }

    public TenancyConfig? Tenancy { get; set; }

    /// <summary>
    /// Row-level security by dimension (ROW-SCOPES-PLAN): map of discriminator
    /// column → <see cref="ScopeConfig"/>. A table is scoped iff it carries the
    /// column (no strict sweep); predicates AND together with tenancy.
    /// </summary>
    public Dictionary<string, ScopeConfig>? Scopes { get; set; }

    /// <summary>
    /// Roles allowed to read the manifest / openapi document. <c>null</c> = anyone (no auth check),
    /// empty list = any authenticated user.
    /// </summary>
    public List<string>? ManifestRoles { get; set; }

    /// <summary>
    /// When true, <c>GET {endpoint}?docs</c> serves an interactive API-reference
    /// page (Scalar viewer over the existing <c>?openapi</c> document). The page
    /// carries no schema data and is served without auth — the spec fetch stays
    /// gated by <see cref="ManifestRoles"/>; the page prompts for a Bearer token
    /// on 401/403. See <see cref="Http.DocsUiPage"/>.
    /// </summary>
    public bool DocsUi { get; set; }

    /// <summary>
    /// Viewer bundle URL for the docs page. Default: the Scalar CDN build
    /// (<see cref="Http.DocsUiPage.DefaultAssetUrl"/>). Point it at a
    /// self-hosted copy for air-gapped deployments.
    /// </summary>
    public string? DocsUiAssetUrl { get; set; }

    /// <summary>
    /// <c>"strict"</c> = secure-by-default boot preset
    /// (TISCLOUD-SHAPE-EXTRACTION-PLAN §4): the build fails with
    /// <c>CONFIG_INVALID</c> when <see cref="Auth"/> has <c>Driver = "none"</c>,
    /// when any mask carries an empty <c>Roles</c> list (reachable by any
    /// authenticated role — or anonymously with driver none), or when
    /// <see cref="ManifestRoles"/> is unset. Replaces app-local "block
    /// anonymous /api" middleware. Null/absent = no check.
    /// </summary>
    public string? Security { get; set; }

    /// <summary>Upload endpoints — keyed by name; route is <c>POST /upload/&lt;name&gt;</c>.</summary>
    public Dictionary<string, UploadEntry>? Uploads { get; set; }

    /// <summary>
    /// When <c>true</c>, <see cref="Shape.ShapeApp.FromDatabase(ShapeConfig)"/> generates admin-only
    /// <c>&lt;table&gt;.adminList</c>/<c>&lt;table&gt;.adminGet</c> masks for tables in
    /// <see cref="SecretTables"/>.
    /// </summary>
    public bool AdminMasks { get; set; }

    /// <summary>When <c>true</c>, <see cref="Shape.ShapeApp.FromDatabase(ShapeConfig)"/> wires <see cref="Shape.Audit.AuditLogger"/> if an <c>audit_log</c> table is detected.</summary>
    public bool Audit { get; set; } = true;

    /// <summary>
    /// Audit Tier 2: record before/after VALUES in <c>audit_log</c> (needs nullable <c>old_values</c> /
    /// <c>new_values</c> TEXT columns). <c>null</c> = off (Tier 1: column names only).
    /// </summary>
    public AuditDiffConfig? AuditDiff { get; set; }

    /// <summary>
    /// E-mail notifications (rules = mask + recipients, outbox, SMTP). Run from cron with
    /// <c>--notify</c> (see <see cref="Shape.Notify.NotifyRunner"/>). <c>null</c> = off.
    /// </summary>
    public NotificationsConfig? Notifications { get; set; }

    /// <summary>
    /// PDF documents (declarative layout filled from masks run in the caller's context); route
    /// <c>GET /pdf/&lt;name&gt;?&lt;params&gt;</c>. Each document is a spec dictionary — see <see cref="Shape.Pdf.DocumentService"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>>? Documents { get; set; }

    /// <summary>Tables exposed only via <see cref="AdminMasks"/>. Defaults: <c>users</c>, <c>refresh_tokens</c>, <c>audit_log</c>, <c>notification_outbox</c>.</summary>
    public List<string> SecretTables { get; set; } = new() { "users", "refresh_tokens", "audit_log", "notification_outbox" };

    /// <summary>When set, restricts <c>FromDatabase</c> to only these tables (catalog scan skipped).</summary>
    public List<string>? IncludeTables { get; set; }

    /// <summary>Tables to skip during <c>FromDatabase</c> introspection.</summary>
    public List<string>? ExcludeTables { get; set; }

    /// <summary>
    /// Per-table role allowlist for auto-generated CRUD masks. Keys are table
    /// names; values are the roles allowed to hit every generated mask for that
    /// table (<c>.list</c>, <c>.get</c>, <c>.by{Fk}</c>, <c>.with{Rel}</c>, the
    /// writable mask). When this map is non-empty:
    /// <list type="bullet">
    ///   <item>Tables not present get NO generated masks. Manual <see cref="Masks"/> entries are unaffected.</item>
    ///   <item>In <see cref="Shape.ShapeApp.FromDatabase(ShapeConfig)"/>, the same set narrows the catalog scan.</item>
    ///   <item>In <see cref="Shape.ShapeApp.FromDatabase(ShapeConfig)"/>, listed tables are flagged writable (DB-first introspection defaults to read-only).</item>
    /// </list>
    /// Use <see cref="AccessByRole"/> for the role-first shorthand
    /// (<c>{ "admin": ["t1", "t2"] }</c>); both forms can coexist and are merged.
    /// </summary>
    public Dictionary<string, List<string>>? Access { get; set; }

    /// <summary>
    /// Role-first shorthand for <see cref="Access"/>. <c>{ "admin": ["t1", "t2"] }</c>
    /// is equivalent to <c>{ "t1": ["admin"], "t2": ["admin"] }</c>. Useful when one
    /// role covers many tables — common in admin-only backoffice deployments.
    /// </summary>
    public Dictionary<string, List<string>>? AccessByRole { get; set; }

    /// <summary>When <c>true</c> (default), schema-derived <c>notEmpty</c>+<c>maxLen:N</c> rules merge with manual validators.</summary>
    public bool AutoValidators { get; set; } = true;

    /// <summary>
    /// Primary-key assignment strategy per table — how inserts obtain the PK on
    /// legacy schemas whose int PK is not IDENTITY/AUTO_INCREMENT (the framework
    /// then allocates <c>MAX(pk)+1</c> atomically; see MANUAL-PK-PLAN.md).
    /// Keys are table names, <c>"*"</c> is the database-wide default; values are
    /// <c>"auto"</c> (detect from schema — the default), <c>"manual"</c> (force
    /// framework allocation; build-time error on composite/non-int PKs), or
    /// <c>"db"</c> (trust identity/trigger/sequence). <c>null</c> ≙ <c>{"*": "auto"}</c>.
    /// JSON config accepts a plain string shorthand (<c>"pkAssignment": "manual"</c>).
    /// </summary>
    public Dictionary<string, string>? PkAssignment { get; set; }

    /// <summary>
    /// Default cap applied to <c>.list</c> and <c>.by{Fk}</c> masks generated by
    /// <see cref="Shape.ShapeApp"/>. <c>0</c> disables.
    /// </summary>
    public int DefaultListLimit { get; set; } = 100;

    /// <summary>
    /// Shape-aware runtime data populated by <see cref="Shape.ShapeApp"/>. Empty when the runtime
    /// is used without entities (raw mask configuration only).
    /// </summary>
    public ShapeRuntime Runtime { get; set; } = new();
}

public sealed class ShapeRuntime
{
    public List<EntityMeta> Entities { get; set; } = new();
    public Dictionary<string, MaskConfig> GeneratedMasks { get; set; } = new();
    public List<string> Commands { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>Detected name of the audit_log table (currently only literal <c>"audit_log"</c>) — null when not present.</summary>
    public string? AuditLogTable { get; set; }

    /// <summary>Admin masks generated by <see cref="Generator.AdminMaskGenerator"/> for <see cref="ShapeConfig.SecretTables"/>.</summary>
    public Dictionary<string, MaskConfig> AdminMasks { get; set; } = new();
}


/// <summary>Opt-in audit before/after values (see <see cref="ShapeConfig.AuditDiff"/>).</summary>
public sealed class AuditDiffConfig
{
    /// <summary>Tables to capture values for; <c>null</c> = every table.</summary>
    public List<string>? Tables { get; set; }

    /// <summary><c>column</c> or <c>table.column</c> entries whose values are recorded as <c>[redacted]</c>.</summary>
    public List<string> Exclude { get; set; } = new();
}


/// <summary>The <c>notifications</c> block: transport, outbox table and rules.</summary>
public sealed class NotificationsConfig
{
    public NotificationTransport Transport { get; set; } = new();
    public string OutboxTable { get; set; } = "notification_outbox";
    public int MaxAttempts { get; set; } = 3;
    public Dictionary<string, NotificationRule> Rules { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Whether any rule reacts to events (so a change listener is wired).</summary>
    public bool HasEventRules => Rules.Values.Any(r => r.On != null);
}

public sealed class NotificationTransport
{
    /// <summary><c>log</c> (default, writes to the console), <c>smtp</c>.</summary>
    public string Driver { get; set; } = "log";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary><c>tls</c> (STARTTLS), <c>ssl</c> (implicit TLS) or <c>none</c>.</summary>
    public string Encryption { get; set; } = "tls";
    public string? User { get; set; }
    public string? Pass { get; set; }
    public string From { get; set; } = "";
    public string FromName { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>One notification rule: run a mask, mail the rows (see <c>Notifier</c>).</summary>
public sealed class NotificationRule
{
    public string Mask { get; set; } = "";
    public Dictionary<string, object?> Params { get; set; } = new();
    public NotificationRecipients To { get; set; } = new();
    /// <summary>Column that identifies an item (default <c>id</c>); feeds the de-duplication fingerprint.</summary>
    public string Key { get; set; } = "id";
    public int RepeatDays { get; set; } = 7;
    public int MaxRows { get; set; } = 50;
    public string Subject { get; set; } = "{rule}: {count}";
    public string Intro { get; set; } = "";
    public string Outro { get; set; } = "";
    public List<string>? Columns { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    /// <summary>
    /// Event trigger: when set, the rule is also evaluated right after a committed mutation of these tables/actions
    /// (see <c>Notifier.OnChanges</c>); the cron run still catches up.
    /// </summary>
    public NotificationTrigger? On { get; set; }
    /// <summary><c>now</c> (default): deliver right after the event; <c>later</c>: leave it pending for the next cron run.</summary>
    public string Send { get; set; } = "now";
}

/// <summary>Which committed changes trigger a rule. Empty <see cref="Actions"/> = any of insert/update/delete.</summary>
public sealed class NotificationTrigger
{
    public List<string> Tables { get; set; } = new();
    public List<string> Actions { get; set; } = new();
}

/// <summary>
/// Exactly one of <see cref="Users"/> (run the mask AS each user, with their own claims),
/// <see cref="Column"/> (rows grouped by an e-mail column) or <see cref="Emails"/> (fixed list); the last two need <see cref="RunAs"/>.
/// </summary>
public sealed class NotificationRecipients
{
    public UserRecipients? Users { get; set; }
    public string? Column { get; set; }
    public List<string>? Emails { get; set; }
    /// <summary>Context the mask runs in for <c>Column</c> / <c>Emails</c>, e.g. <c>{ ["role"] = "admin" }</c>.</summary>
    public Dictionary<string, object?>? RunAs { get; set; }
}

public sealed class UserRecipients
{
    public List<string> Roles { get; set; } = new();
    /// <summary>Users whose value in this users column is 0 are skipped.</summary>
    public string? OptoutColumn { get; set; }
}
