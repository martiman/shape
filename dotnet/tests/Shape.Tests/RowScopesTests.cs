using System.Text.Json;
using Shape;
using Shape.Attributes;
using Shape.Auth;
using Shape.Core;
using Shape.Generator;
using Shape.Http;
using Shape.Mcp;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

// Tenant + one dimension (department_id) — the common ERP shape.
[Entity("rs_books", Writable = true)]
[Access(Roles = new[] { "user", "manager", "controller", "superadmin" })]
public sealed class RsBook
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    public int TenantId { get; set; }
    public int DepartmentId { get; set; }

    [Column(Length = 100)]
    public string Title { get; set; } = "";
}

// Child of rs_books — FK gives the harness a by{Fk} mask and the
// nested-relations recursion something to stamp.
[Entity("rs_notes", Writable = true)]
[Access(Roles = new[] { "user", "manager", "controller", "superadmin" })]
public sealed class RsNote
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Fk(typeof(RsBook))]
    public int BookId { get; set; }

    public int TenantId { get; set; }
    public int DepartmentId { get; set; }

    [Column(Length = 200)]
    public string Body { get; set; } = "";
}

// Soft-deletable scoped table — DELETE becomes UPDATE and must still guard.
[Entity("rs_docs", Writable = true)]
[Access(Roles = new[] { "user", "manager", "controller", "superadmin" })]
public sealed class RsDoc
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    public int TenantId { get; set; }
    public int DepartmentId { get; set; }

    [Column(Length = 100)]
    public string Title { get; set; } = "";

    public DateTime? DeletedAt { get; set; }
}

// Nullable dimension column — boot warning by default, error on opt-in.
[Entity("rs_nullable", Writable = true)]
[Access(Roles = new[] { "user" })]
public sealed class RsNullableDept
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    public int TenantId { get; set; }
    public int? DepartmentId { get; set; }

    [Column(Length = 100)]
    public string Title { get; set; } = "";
}

/// <summary>
/// Row-level security by dimension (ROW-SCOPES-PLAN §4): the department scope
/// composes with tenancy — predicates AND together, dimension-unscoped roles
/// take the wrap path, multi-valued claims bind as IN, writes stamp/guard with
/// SCOPE_MISMATCH / SCOPE_REQUIRED, and pre-feature tokens are rejected.
/// </summary>
public sealed class RowScopesTests
{
    private static readonly TenancyConfig Tenancy =
        new() { Column = "tenant_id", UnscopedRoles = new() { "superadmin" } };

    private static Dictionary<string, ScopeConfig> DeptScope(bool multi = false, params string[] unscoped) =>
        new(StringComparer.Ordinal)
        {
            ["department_id"] = new ScopeConfig
            {
                Multi = multi,
                UnscopedRoles = unscoped.ToList()
            }
        };

    private sealed class Harness : IDisposable
    {
        private readonly string _dbFile;
        public Database Db { get; }
        public BatchExecutor Executor { get; }
        public ShapeConfig Config { get; }

        public Harness(Dictionary<string, ScopeConfig> scopes)
        {
            _dbFile = Path.Combine(Path.GetTempPath(), $"shape-rowscopes-{Guid.NewGuid():N}.db");
            var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={_dbFile}" };
            Db = new Database(dbConfig);
            Db.Exec("CREATE TABLE rs_books (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INTEGER NOT NULL, department_id INTEGER NOT NULL, title TEXT NOT NULL)", new());
            Db.Exec("CREATE TABLE rs_notes (id INTEGER PRIMARY KEY AUTOINCREMENT, book_id INTEGER NOT NULL, tenant_id INTEGER NOT NULL, department_id INTEGER NOT NULL, body TEXT NOT NULL)", new());
            Db.Exec("CREATE TABLE rs_docs (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INTEGER NOT NULL, department_id INTEGER NOT NULL, title TEXT NOT NULL, deleted_at TEXT NULL)", new());

            var roles = new List<string> { "user", "manager", "controller", "superadmin" };
            Config = ShapeApp.FromTypes(new[] { typeof(RsBook), typeof(RsNote), typeof(RsDoc) }, new ShapeConfig
            {
                Db = dbConfig,
                Tenancy = Tenancy,
                Scopes = scopes,
                // Tenancy auto-exposes the `tenants` registry into the access map,
                // which flips mask generation into allowlist mode — so every
                // served business table must be listed explicitly (as ExampleCorp
                // does via AccessByRole). Without this, rs_books gets no masks.
                Access = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["rs_books"] = new(roles),
                    ["rs_notes"] = new(roles),
                    ["rs_docs"] = new(roles)
                }
            });
            Executor = new BatchExecutor(Db, new MaskRegistry(Config.Masks), new ValidatorRegistry(), Tenancy, scopes);
        }

        public Dictionary<string, object?> Mutate(
            Dictionary<string, object?> ctx,
            string table,
            string action,
            Dictionary<string, object?>? data = null,
            Dictionary<string, object?>? filter = null,
            Dictionary<string, object?>? relations = null)
        {
            var mutation = new Dictionary<string, object?> { ["table"] = table, ["action"] = action };
            if (data != null) mutation["data"] = data;
            if (filter != null) mutation["filter"] = filter;
            if (relations != null) mutation["relations"] = relations;
            var res = Executor.ExecuteMutations(new List<Dictionary<string, object?>> { mutation }, ctx);
            return ((List<Dictionary<string, object?>>)res["results"]!)[0];
        }

        public long Insert(Dictionary<string, object?> ctx, Dictionary<string, object?> data) =>
            Convert.ToInt64(Mutate(ctx, "rs_books", "insert", data)["id"]);

        public Dictionary<string, object?> Query(
            Dictionary<string, object?> ctx,
            string maskName,
            Dictionary<string, object?>? parameters = null,
            Dictionary<string, object?>? options = null,
            BatchExecutor.PrecompiledFilter? filter = null)
        {
            var query = new Dictionary<string, object?>
            {
                ["ref"] = "r",
                ["sql"] = maskName,
                ["params"] = parameters ?? new Dictionary<string, object?>()
            };
            if (options != null) query["options"] = options;
            // The OData $filter travels the trusted out-of-band channel — never
            // the client-controlled options map (SECURITY-REVIEW C1).
            var precompiled = filter != null
                ? new Dictionary<string, BatchExecutor.PrecompiledFilter> { ["r"] = filter }
                : null;
            var res = Executor.Execute(new List<Dictionary<string, object?>> { query }, ctx, precompiled);
            return ((List<Dictionary<string, object?>>)res["results"]!)[0];
        }

        public int ListCount(Dictionary<string, object?> ctx) =>
            Convert.ToInt32(Query(ctx, "rs_books.list")["count"]);

        public void Dispose()
        {
            Db.Dispose();
            if (File.Exists(_dbFile)) File.Delete(_dbFile);
        }
    }

    private static Dictionary<string, object?> Ctx(string role, object? tenant, object? dept) =>
        new() { ["role"] = role, ["tenant_id"] = tenant, ["department_id"] = dept };

    [Fact]
    public void Four_quadrant_isolation_matrix()
    {
        // A global admin is unscoped PER scope: superadmin must be listed in the
        // dimension's unscoped roles too, else it stays department-scoped and
        // needs a department claim (unscoped-ness is not transitive across scopes).
        using var h = new Harness(DeptScope(unscoped: new[] { "controller", "superadmin" }));
        var t1d1 = Ctx("user", 1, 10);
        var t1d2 = Ctx("user", 1, 20);
        var t2d1 = Ctx("user", 2, 10);
        var controllerT1 = Ctx("controller", 1, null);   // dimension-unscoped, tenant-scoped
        var superadmin = new Dictionary<string, object?> { ["role"] = "superadmin", ["tenant_id"] = null };

        // One row per (tenant, department) quadrant — each stamped from its own claims.
        h.Insert(t1d1, new() { ["title"] = "t1d1" });
        h.Insert(t1d2, new() { ["title"] = "t1d2" });
        h.Insert(t2d1, new() { ["title"] = "t2d1" });
        h.Insert(superadmin, new() { ["title"] = "t2d2", ["tenant_id"] = 2, ["department_id"] = 20 });

        // Every insert landed in the intended quadrant.
        Assert.Equal(4, Convert.ToInt32(h.Db.Run("SELECT COUNT(*) AS c FROM rs_books", new()).Rows[0][0]));

        // Scoped user sees exactly its own quadrant.
        Assert.Equal(1, h.ListCount(t1d1));
        // Dimension-unscoped controller sees BOTH departments of tenant 1 — and no tenant 2.
        Assert.Equal(2, h.ListCount(controllerT1));
        // Superadmin (fully unscoped) sees everything.
        Assert.Equal(4, h.ListCount(superadmin));
    }

    [Fact]
    public void Get_across_dimension_is_invisible()
    {
        using var h = new Harness(DeptScope());
        var t1d1 = Ctx("user", 1, 10);
        var t1d2 = Ctx("user", 1, 20);
        var otherDeptId = h.Insert(t1d2, new() { ["title"] = "other dept" });

        // Same tenant, different department → the compiled predicate excludes it.
        var res = h.Executor.Execute(new List<Dictionary<string, object?>>
        {
            new() { ["ref"] = "r", ["sql"] = "rs_books.get", ["params"] = new Dictionary<string, object?> { ["id"] = otherDeptId } }
        }, t1d1);
        var r = ((List<Dictionary<string, object?>>)res["results"]!)[0];
        Assert.Equal(0, Convert.ToInt32(r["count"]));
    }

    [Fact]
    public void Cross_department_update_and_delete_affect_zero_rows()
    {
        using var h = new Harness(DeptScope());
        var t1d1 = Ctx("user", 1, 10);
        var t1d2 = Ctx("user", 1, 20);
        var d2Id = h.Insert(t1d2, new() { ["title"] = "dept 20 row" });

        var upd = h.Executor.ExecuteMutations(new List<Dictionary<string, object?>>
        {
            new() { ["table"] = "rs_books", ["action"] = "update",
                    ["filter"] = new Dictionary<string, object?> { ["id"] = d2Id },
                    ["data"] = new Dictionary<string, object?> { ["title"] = "hijacked" } }
        }, t1d1);
        Assert.Equal(0, Convert.ToInt32(((List<Dictionary<string, object?>>)upd["results"]!)[0]["affected"]));

        var del = h.Executor.ExecuteMutations(new List<Dictionary<string, object?>>
        {
            new() { ["table"] = "rs_books", ["action"] = "delete",
                    ["filter"] = new Dictionary<string, object?> { ["id"] = d2Id } }
        }, t1d1);
        Assert.Equal(0, Convert.ToInt32(((List<Dictionary<string, object?>>)del["results"]!)[0]["affected"]));

        // The row is untouched.
        Assert.Equal("dept 20 row", h.Db.Run("SELECT title FROM rs_books WHERE id = @id",
            new Dictionary<string, object?> { ["id"] = d2Id }).Rows[0][0]?.ToString());
    }

    [Fact]
    public void Insert_with_foreign_department_is_rejected()
    {
        using var h = new Harness(DeptScope());
        var ex = Assert.Throws<ApiException>(() =>
            h.Insert(Ctx("user", 1, 10), new() { ["title"] = "x", ["department_id"] = 99 }));
        Assert.Equal("VALIDATION_ERROR", ex.ApiCode);
        Assert.Equal("SCOPE_MISMATCH", ex.Details?.GetValueOrDefault("detail"));
        Assert.Equal("department_id", ex.Details?.GetValueOrDefault("scope"));
    }

    [Fact]
    public void Multi_valued_membership_reads_all_and_guards_writes()
    {
        using var h = new Harness(DeptScope(multi: true));
        var member = new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["tenant_id"] = 1,
            ["department_id"] = new List<object?> { 10, 20 }
        };

        // Reads both memberships (IN predicate).
        h.Insert(member, new() { ["title"] = "in-10", ["department_id"] = 10 });
        h.Insert(member, new() { ["title"] = "in-20", ["department_id"] = 20 });
        Assert.Equal(2, h.ListCount(member));

        // Insert outside the membership set → SCOPE_MISMATCH.
        var mismatch = Assert.Throws<ApiException>(() =>
            h.Insert(member, new() { ["title"] = "bad", ["department_id"] = 30 }));
        Assert.Equal("SCOPE_MISMATCH", mismatch.Details?.GetValueOrDefault("detail"));

        // Insert without the column → SCOPE_REQUIRED (framework can't pick among memberships).
        var required = Assert.Throws<ApiException>(() =>
            h.Insert(member, new() { ["title"] = "no dept" }));
        Assert.Equal("SCOPE_REQUIRED", required.Details?.GetValueOrDefault("detail"));
    }

    [Fact]
    public void Missing_scope_claim_is_rejected_as_relogin()
    {
        using var h = new Harness(DeptScope());
        // Pre-feature token: has the tenant claim but no department_id.
        var stale = new Dictionary<string, object?> { ["role"] = "user", ["tenant_id"] = 1 };
        var ex = Assert.Throws<ApiException>(() => h.ListCount(stale));
        Assert.Equal("AUTH_INVALID", ex.ApiCode);
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public void Generated_masks_carry_row_scopes_and_unscoped_variant()
    {
        using var h = new Harness(DeptScope(unscoped: "controller"));
        var list = h.Config.Masks["rs_books.list"];
        Assert.Contains("@__scope_department_id", list.Sql);
        Assert.Contains("@__tenant", list.Sql);
        // MaskConfig.RowScopes is the flexible config-side carrier; the runtime
        // registry normalizes it to the typed Dictionary — check that shape.
        var runtime = new MaskRegistry(h.Config.Masks).TryGet("rs_books.list")!;
        Assert.NotNull(runtime.RowScopes);
        Assert.True(runtime.RowScopes!.ContainsKey("department_id"));
        // Unscoped variant emitted (a role can cross the department boundary),
        // and it is predicate-free so the wrap path can add only what applies.
        Assert.False(string.IsNullOrEmpty(list.SqlUnscoped));
        Assert.DoesNotContain("@__scope_department_id", list.SqlUnscoped!);

        // The writable mask carries the scope contract too (drives stamping).
        Assert.NotNull(new MaskRegistry(h.Config.Masks).TryGet("rs_books")!.RowScopes);
    }

    [Fact]
    public void Manifest_emits_row_scopes_and_entity_rowScopes()
    {
        using var h = new Harness(DeptScope());
        var manifest = new ManifestGenerator().Generate(
            h.Config.Runtime.Entities, h.Config.Masks, h.Config.Runtime.Commands);

        // Parity snapshot (ROW-SCOPES-PHP-PLAN §2.3): the wire shapes must be
        // byte-identical across stacks — object keyed by column on masks,
        // plain column array on entities.
        var json = System.Text.Json.JsonSerializer.Serialize(manifest);
        Assert.Contains("\"row_scopes\":{\"department_id\":{\"multi\":false}}", json);
        Assert.Contains("\"rowScopes\":[\"department_id\"]", json);
    }

    [Fact]
    public void Manifest_multi_scope_emits_multi_true()
    {
        using var h = new Harness(DeptScope(multi: true));
        var manifest = new ManifestGenerator().Generate(
            h.Config.Runtime.Entities, h.Config.Masks, h.Config.Runtime.Commands);
        var json = System.Text.Json.JsonSerializer.Serialize(manifest);
        Assert.Contains("\"row_scopes\":{\"department_id\":{\"multi\":true}}", json);
    }

    [Fact]
    public void ByFk_getMany_and_count_respect_the_dimension()
    {
        using var h = new Harness(DeptScope());
        var t1d1 = Ctx("user", 1, 10);
        var t1d2 = Ctx("user", 1, 20);

        var bookD1 = h.Insert(t1d1, new() { ["title"] = "d1 book" });
        var bookD2 = h.Insert(t1d2, new() { ["title"] = "d2 book" });
        // Notes in both departments attached to the d1 book (dept follows the
        // note's own claim, not the parent's).
        h.Mutate(t1d1, "rs_notes", "insert", new() { ["book_id"] = bookD1, ["body"] = "n-d1" });
        h.Mutate(t1d2, "rs_notes", "insert", new() { ["book_id"] = bookD1, ["body"] = "n-d2" });

        // by{Fk}: only the caller's department's notes are visible.
        var byFk = h.Query(t1d1, "rs_notes.byBookId", new() { ["book_id"] = bookD1 });
        Assert.Equal(1, Convert.ToInt32(byFk["count"]));

        // getMany: cross-department ids silently drop out of the IN result.
        var getMany = h.Query(t1d1, "rs_books.getMany", new() { ["ids"] = new List<object?> { bookD1, bookD2 } });
        Assert.Equal(1, Convert.ToInt32(getMany["count"]));

        // COUNT wraps the executed (scoped) SQL — total_count is per-dimension.
        var counted = h.Query(t1d1, "rs_books.list", options: new() { ["count"] = true, ["top"] = 10 });
        Assert.Equal(1, Convert.ToInt32(counted["total_count"]));
    }

    [Fact]
    public void OData_filter_composes_with_and_never_replaces_the_predicate()
    {
        using var h = new Harness(DeptScope(unscoped: new[] { "controller", "superadmin" }));
        var t1d1 = Ctx("user", 1, 10);
        var t1d2 = Ctx("user", 1, 20);
        h.Insert(t1d1, new() { ["title"] = "target" });
        h.Insert(t1d2, new() { ["title"] = "target" });

        var (filterSql, filterParams) = ODataFilter.Compile(
            "title eq 'target'",
            new[] { "id", "tenant_id", "department_id", "title" },
            h.Db.QuoteIdentifier);
        var filter = new BatchExecutor.PrecompiledFilter(filterSql, filterParams);

        // Scoped caller: filter matches both rows, the dimension predicate keeps one.
        Assert.Equal(1, Convert.ToInt32(h.Query(t1d1, "rs_books.list", filter: filter)["count"]));

        // A $filter aimed straight at the scope column composes (AND), never
        // replaces: asking for department 20 as a department-10 user yields 0.
        var (bypassSql, bypassParams) = ODataFilter.Compile(
            "department_id eq 20",
            new[] { "id", "tenant_id", "department_id", "title" },
            h.Db.QuoteIdentifier);
        var bypass = h.Query(t1d1, "rs_books.list",
            filter: new BatchExecutor.PrecompiledFilter(bypassSql, bypassParams));
        Assert.Equal(0, Convert.ToInt32(bypass["count"]));

        // CloneMask regression: the wrap path (dimension-unscoped controller →
        // SqlUnscoped + tenant wrap) then the $filter wrap — RowScopes must
        // survive both clones and the tenant predicate must still apply.
        Assert.Equal(2, Convert.ToInt32(h.Query(Ctx("controller", 1, null), "rs_books.list", filter: filter)["count"]));
    }

    [Fact]
    public void Client_supplied_scope_param_is_rejected_as_reserved()
    {
        using var h = new Harness(DeptScope());
        var ex = Assert.Throws<ApiException>(() =>
            h.Query(Ctx("user", 1, 10), "rs_books.list", new() { ["__scope_department_id"] = 99 }));
        Assert.Equal("PARAM_VALIDATION_ERROR", ex.ApiCode);
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Update_moving_a_row_across_the_dimension_is_rejected()
    {
        using var h = new Harness(DeptScope());
        var t1d1 = Ctx("user", 1, 10);
        var id = h.Insert(t1d1, new() { ["title"] = "stay" });

        var ex = Assert.Throws<ApiException>(() => h.Mutate(t1d1, "rs_books", "update",
            data: new() { ["title"] = "moved", ["department_id"] = 20 },
            filter: new() { ["id"] = id }));
        Assert.Equal("VALIDATION_ERROR", ex.ApiCode);
        Assert.Equal("SCOPE_MISMATCH", ex.Details?.GetValueOrDefault("detail"));
        Assert.Equal("department_id", ex.Details?.GetValueOrDefault("scope"));
    }

    [Fact]
    public void Soft_delete_keeps_the_dimension_guard()
    {
        using var h = new Harness(DeptScope());
        var t1d1 = Ctx("user", 1, 10);
        var t1d2 = Ctx("user", 1, 20);
        var d2Doc = Convert.ToInt64(h.Mutate(t1d2, "rs_docs", "insert", new() { ["title"] = "other dept doc" })["id"]);
        var ownDoc = Convert.ToInt64(h.Mutate(t1d1, "rs_docs", "insert", new() { ["title"] = "own doc" })["id"]);

        // Cross-department soft delete: affected 0, no tombstone.
        var cross = h.Mutate(t1d1, "rs_docs", "delete", filter: new() { ["id"] = d2Doc });
        Assert.Equal(0, Convert.ToInt32(cross["affected"]));
        Assert.True(h.Db.Run("SELECT deleted_at FROM rs_docs WHERE id = @id",
            new Dictionary<string, object?> { ["id"] = d2Doc }).Rows[0][0] is null);

        // Own row: soft-deleted (row survives, tombstone set) and hidden from reads.
        var own = h.Mutate(t1d1, "rs_docs", "delete", filter: new() { ["id"] = ownDoc });
        Assert.Equal(1, Convert.ToInt32(own["affected"]));
        Assert.False(h.Db.Run("SELECT deleted_at FROM rs_docs WHERE id = @id",
            new Dictionary<string, object?> { ["id"] = ownDoc }).Rows[0][0] is null);
        Assert.Equal(0, Convert.ToInt32(h.Query(t1d1, "rs_docs.list")["count"]));
    }

    [Fact]
    public void Nested_relations_children_are_stamped_with_the_dimension()
    {
        using var h = new Harness(DeptScope());
        var t1d1 = Ctx("user", 1, 10);

        var result = h.Mutate(t1d1, "rs_books", "insert",
            data: new() { ["title"] = "parent" },
            relations: new()
            {
                ["rs_notes"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["body"] = "child note" }
                }
            });
        Assert.Equal(1, Convert.ToInt32(result["affected"]));

        var note = h.Db.Run("SELECT tenant_id, department_id FROM rs_notes", new());
        Assert.Single(note.Rows);
        Assert.Equal(1L, Convert.ToInt64(note.Rows[0][0]));
        Assert.Equal(10L, Convert.ToInt64(note.Rows[0][1]));
    }

    [Fact]
    public void Multi_valued_update_and_delete_bind_the_membership_as_IN()
    {
        using var h = new Harness(DeptScope(multi: true, "superadmin"));
        var member = new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["tenant_id"] = 1,
            ["department_id"] = new List<object?> { 10, 20 }
        };
        var superadmin = new Dictionary<string, object?> { ["role"] = "superadmin", ["tenant_id"] = null };

        var inD10 = Convert.ToInt64(h.Mutate(member, "rs_books", "insert",
            new() { ["title"] = "mine", ["department_id"] = 10 })["id"]);
        var inD30 = Convert.ToInt64(h.Mutate(superadmin, "rs_books", "insert",
            new() { ["title"] = "foreign", ["tenant_id"] = 1, ["department_id"] = 30 })["id"]);

        // Outside the membership: invisible to update/delete (affected 0).
        Assert.Equal(0, Convert.ToInt32(h.Mutate(member, "rs_books", "update",
            data: new() { ["title"] = "hijack" }, filter: new() { ["id"] = inD30 })["affected"]));
        Assert.Equal(0, Convert.ToInt32(h.Mutate(member, "rs_books", "delete",
            filter: new() { ["id"] = inD30 })["affected"]));

        // Inside: update without touching the scope column works (no
        // SCOPE_REQUIRED on update — only inserts must pick a membership).
        Assert.Equal(1, Convert.ToInt32(h.Mutate(member, "rs_books", "update",
            data: new() { ["title"] = "renamed" }, filter: new() { ["id"] = inD10 })["affected"]));
    }

    [Fact]
    public void JsonElement_array_claim_unwraps_and_binds_as_IN_list()
    {
        using var h = new Harness(DeptScope(multi: true));
        var seed = new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["tenant_id"] = 1,
            ["department_id"] = new List<object?> { 10, 20 }
        };
        h.Mutate(seed, "rs_books", "insert", new() { ["title"] = "a", ["department_id"] = 10 });
        h.Mutate(seed, "rs_books", "insert", new() { ["title"] = "b", ["department_id"] = 20 });

        // Claim arrives as a raw JsonElement array (as JWT payloads produce).
        using var doc = JsonDocument.Parse("[10, 20]");
        var jsonCtx = new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["tenant_id"] = 1,
            ["department_id"] = doc.RootElement
        };
        Assert.Equal(2, h.ListCount(jsonCtx));
    }

    [Fact]
    public void RewriteColonParams_handles_scope_placeholders()
    {
        Assert.Equal(
            "SELECT * FROM t WHERE department_id = @__scope_department_id",
            MaskRegistry.RewriteColonParams("SELECT * FROM t WHERE department_id = :__scope_department_id"));
        // Already-Dapper placeholders pass through untouched.
        const string dapperStyle = "SELECT * FROM t WHERE department_id = @__scope_department_id";
        Assert.Equal(dapperStyle, MaskRegistry.RewriteColonParams(dapperStyle));
    }

    [Fact]
    public void Mcp_tools_list_is_unchanged_by_row_scopes()
    {
        using var scoped = new Harness(DeptScope());
        using var unscoped = new Harness(new Dictionary<string, ScopeConfig>(StringComparer.Ordinal));

        static List<string> ToolNames(ShapeConfig config)
        {
            var server = new McpServer(config, new Dictionary<string, object?> { ["role"] = "user" });
            var response = server.Handle(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/list"
            });
            var result = (Dictionary<string, object?>)response!["result"]!;
            var tools = (List<Dictionary<string, object?>>)result["tools"]!;
            return tools.Select(t => t["name"]?.ToString() ?? "").OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        // Role-filtering is unchanged: a row-scoped deployment lists exactly
        // the same tools as an unscoped one (scoping happens inside execution).
        Assert.Equal(ToolNames(unscoped.Config), ToolNames(scoped.Config));
    }

    [Fact]
    public void OpenApi_treats_scope_columns_like_the_tenant_column()
    {
        using var h = new Harness(DeptScope());
        var spec = new OpenApiGenerator().Generate(
            h.Config.Runtime.Entities,
            h.Config.Masks,
            h.Config.Runtime.Commands,
            h.Config.Auth,
            h.Config.ServiceName);
        // Serialize the way the runtime actually emits it (JsonResponse.JsonOptions
        // uses UnsafeRelaxedJsonEscaping — the byte-for-byte wire output shared
        // with PHP). STJ's default encoder would escape the apostrophe in
        // "caller's" to ', which the real HTTP response never does.
        var json = System.Text.Json.JsonSerializer.Serialize(spec, JsonResponse.JsonOptions);

        // Per-scope description sentence on read masks (ROW-SCOPES-PLAN §2.5).
        Assert.Contains("Rows are automatically limited to the caller's department_id.", json);
        // Insert schema keeps the column (optional, claim-stamped)…
        Assert.Contains("Stamped from the caller's department_id claim", json);

        // …while the update schema excludes it (a row can't move dimensions).
        var paths = (Dictionary<string, object?>)spec["paths"]!;
        var updatePath = (Dictionary<string, object?>)paths["/api?op=mutation&action=update&table=rs_books"]!;
        var updateJson = System.Text.Json.JsonSerializer.Serialize(updatePath);
        Assert.DoesNotContain("department_id", updateJson);
    }
}

/// <summary>
/// Auth integration: dimension claims are server-issued at login, re-read on
/// refresh, normalized per role by JwtGuard, and pre-feature tokens are
/// rejected with a re-login signal (ROW-SCOPES-PLAN §2.2).
/// </summary>
public sealed class RowScopesAuthTests
{
    private sealed class AuthHarness : IDisposable
    {
        private readonly string _dbFile;
        public DatabaseConfig DbConfig { get; }
        public AuthConfig Auth { get; }
        public LoginService Service { get; }
        public JwtGuard Guard { get; }

        public AuthHarness(Dictionary<string, ScopeConfig> scopes)
        {
            _dbFile = Path.Combine(Path.GetTempPath(), $"shape-rowscopes-auth-{Guid.NewGuid():N}.db");
            DbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={_dbFile}" };
            using (var db = new Database(DbConfig))
            {
                db.Exec("CREATE TABLE tenants (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1)", new());
                db.Exec(@"CREATE TABLE users (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  tenant_id INTEGER NOT NULL,
                  department_id INTEGER NULL,
                  email TEXT NOT NULL,
                  password_hash TEXT NOT NULL,
                  display_name TEXT,
                  role TEXT NOT NULL,
                  is_active INTEGER NOT NULL DEFAULT 1
                )", new());
                db.Exec(@"CREATE TABLE refresh_tokens (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  user_id INTEGER NOT NULL,
                  token_hash TEXT NOT NULL,
                  expires_at TEXT NOT NULL,
                  revoked_at TEXT,
                  replaced_by_token_id INTEGER
                )", new());
                db.Exec("CREATE TABLE user_departments (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, department_id INTEGER NOT NULL)", new());
                db.Exec("INSERT INTO tenants (id, name, is_active) VALUES (1, 'Acme', 1)", new());

                var hash = BCrypt.Net.BCrypt.HashPassword("password123");
                db.Exec("INSERT INTO users (tenant_id, department_id, email, password_hash, role) VALUES (1, 10, 'rep@acme.test', @h, 'user')",
                    new Dictionary<string, object?> { ["h"] = hash });
                db.Exec("INSERT INTO users (tenant_id, department_id, email, password_hash, role) VALUES (1, 10, 'controller@acme.test', @h, 'controller')",
                    new Dictionary<string, object?> { ["h"] = hash });
                db.Exec("INSERT INTO users (tenant_id, department_id, email, password_hash, role) VALUES (1, NULL, 'multi@acme.test', @h, 'user')",
                    new Dictionary<string, object?> { ["h"] = hash });
                db.Exec("INSERT INTO user_departments (user_id, department_id) VALUES (3, 10), (3, 20)", new());
            }

            Auth = new AuthConfig
            {
                Driver = "jwt",
                JwtSecret = "row-scopes-test-secret-me-long-enough",
                Tenancy = new TenancyConfig { UnscopedRoles = new() { "superadmin" } },
                Scopes = scopes
            };
            Service = new LoginService(DbConfig, Auth, new JwtIssuer(Auth));
            Guard = new JwtGuard(Auth);
        }

        public Dictionary<string, object?> Login(string email) =>
            Service.Login(new Dictionary<string, object?> { ["email"] = email, ["password"] = "password123" });

        public Dictionary<string, object?> Authorize(string accessToken) =>
            Guard.Authorize(new Dictionary<string, string> { ["authorization"] = "Bearer " + accessToken });

        public void Dispose()
        {
            if (File.Exists(_dbFile)) File.Delete(_dbFile);
        }
    }

    private static Dictionary<string, ScopeConfig> SingleScope(params string[] unscoped) =>
        new(StringComparer.Ordinal)
        {
            ["department_id"] = new ScopeConfig { UnscopedRoles = unscoped.ToList() }
        };

    private static Dictionary<string, ScopeConfig> MembershipScope() =>
        new(StringComparer.Ordinal)
        {
            ["department_id"] = new ScopeConfig
            {
                Multi = true,
                ClaimTable = new ClaimTableConfig { Table = "user_departments", UserColumn = "user_id" }
            }
        };

    [Fact]
    public void Claim_rides_login_and_refresh_round_trip()
    {
        using var h = new AuthHarness(SingleScope());

        var login = h.Login("rep@acme.test");
        var ctx = h.Authorize(login["access_token"]!.ToString()!);
        Assert.Equal(10L, Convert.ToInt64(ctx["department_id"]));
        Assert.Equal(1L, Convert.ToInt64(ctx["tenant_id"]));

        // Refresh re-reads the claim from the users row (rotation pair).
        var refreshed = h.Service.Refresh(new Dictionary<string, object?>
        {
            ["refresh_token"] = login["refresh_token"]
        });
        var refreshedCtx = h.Authorize(refreshed["access_token"]!.ToString()!);
        Assert.Equal(10L, Convert.ToInt64(refreshedCtx["department_id"]));
    }

    [Fact]
    public void Unscoped_role_gets_a_null_claim()
    {
        using var h = new AuthHarness(SingleScope("controller"));
        var login = h.Login("controller@acme.test");
        var ctx = h.Authorize(login["access_token"]!.ToString()!);
        // users.department_id = 10, but the role crosses the dimension —
        // the claim is forced to null at issuance AND at guard time.
        Assert.True(ctx.ContainsKey("department_id"));
        Assert.Null(ctx["department_id"]);
    }

    [Fact]
    public void Pre_feature_token_is_rejected_with_auth_invalid()
    {
        using var h = new AuthHarness(SingleScope());
        // A token minted before `scopes` was enabled: valid signature, tenant
        // claim present, no department claim.
        var stale = new JwtIssuer(h.Auth).Issue(new Dictionary<string, object?>
        {
            ["sub"] = "1",
            ["email"] = "rep@acme.test",
            ["role"] = "user",
            ["tenant_id"] = 1
        }, 3600);

        var ex = Assert.Throws<ApiException>(() => h.Authorize(stale));
        Assert.Equal("AUTH_INVALID", ex.ApiCode);
        Assert.Equal(401, ex.Status);
        Assert.Contains("Re-login", ex.Message);
    }

    [Fact]
    public void Claim_table_membership_issues_a_list_claim()
    {
        using var h = new AuthHarness(MembershipScope());
        var login = h.Login("multi@acme.test");
        var ctx = h.Authorize(login["access_token"]!.ToString()!);

        var memberships = Assert.IsAssignableFrom<IList<object?>>(ctx["department_id"]);
        Assert.Equal(2, memberships.Count);
        Assert.Contains(memberships, m => Convert.ToInt64(m) == 10);
        Assert.Contains(memberships, m => Convert.ToInt64(m) == 20);
    }

    [Fact]
    public void Multi_without_claim_table_wraps_users_row_value_into_a_list()
    {
        using var h = new AuthHarness(new Dictionary<string, ScopeConfig>(StringComparer.Ordinal)
        {
            ["department_id"] = new ScopeConfig { Multi = true }
        });
        var login = h.Login("rep@acme.test");
        var ctx = h.Authorize(login["access_token"]!.ToString()!);
        var memberships = Assert.IsAssignableFrom<IList<object?>>(ctx["department_id"]);
        Assert.Single(memberships);
        Assert.Equal(10L, Convert.ToInt64(memberships[0]));
    }
}

/// <summary>
/// Build-time contract + config validation (ROW-SCOPES-PLAN §2.5 / §2.7):
/// each CONFIG_INVALID case, the writable exemption, the relaxed warning for
/// undeclared manual masks, and the `public` cache leak guard.
/// </summary>
public sealed class RowScopesConfigTests
{
    private static ShapeConfig BaseConfig(
        Dictionary<string, ScopeConfig>? scopes = null,
        Dictionary<string, MaskConfig>? masks = null,
        TenancyConfig? tenancy = null) => new()
    {
        Auth = new AuthConfig { Driver = "none" },
        Tenancy = tenancy,
        Scopes = scopes ?? new Dictionary<string, ScopeConfig>(StringComparer.Ordinal)
        {
            ["department_id"] = new ScopeConfig { UnscopedRoles = new() { "controller" } }
        },
        Masks = masks ?? new()
    };

    [Fact]
    public void Invalid_nullable_value_fails_boot()
    {
        var config = BaseConfig(new Dictionary<string, ScopeConfig>(StringComparer.Ordinal)
        {
            ["department_id"] = new ScopeConfig { Nullable = "sometimes" }
        });
        var ex = Assert.Throws<ApiException>(() => ShapeApp.FromTypes(new[] { typeof(RsBook) }, config));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("nullable", ex.Message);
    }

    [Fact]
    public void Claim_table_without_multi_fails_boot()
    {
        var config = BaseConfig(new Dictionary<string, ScopeConfig>(StringComparer.Ordinal)
        {
            ["department_id"] = new ScopeConfig
            {
                ClaimTable = new ClaimTableConfig { Table = "user_departments" }
            }
        });
        var ex = Assert.Throws<ApiException>(() => ShapeApp.FromTypes(new[] { typeof(RsBook) }, config));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("multi", ex.Message);
    }

    [Fact]
    public void Scope_colliding_with_tenancy_column_fails_boot()
    {
        var config = BaseConfig(
            new Dictionary<string, ScopeConfig>(StringComparer.Ordinal) { ["tenant_id"] = new ScopeConfig() },
            tenancy: new TenancyConfig { UnscopedRoles = new() { "superadmin" } });
        var ex = Assert.Throws<ApiException>(() => ShapeApp.FromTypes(new[] { typeof(RsBook) }, config));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("collides", ex.Message);
    }

    [Fact]
    public void Nullable_dimension_column_warns_by_default_and_errors_on_opt_in()
    {
        // Default ('warn'): boots with a warning — NULL rows are invisible.
        var warned = ShapeApp.FromTypes(new[] { typeof(RsNullableDept) }, BaseConfig());
        Assert.Contains(warned.Runtime.Warnings, w => w.Contains("nullable scope column 'department_id'"));

        // Opt-in ('error'): boot failure.
        var config = BaseConfig(new Dictionary<string, ScopeConfig>(StringComparer.Ordinal)
        {
            ["department_id"] = new ScopeConfig { Nullable = "error" }
        });
        var ex = Assert.Throws<ApiException>(() => ShapeApp.FromTypes(new[] { typeof(RsNullableDept) }, config));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("nullable scope column", ex.Message);
    }

    [Fact]
    public void Declared_row_scope_requires_its_placeholder_in_read_sql()
    {
        var config = BaseConfig(masks: new()
        {
            ["rs_books.report"] = new MaskConfig
            {
                Sql = "SELECT id, title FROM rs_books",
                Roles = new() { "user" },
                RowScopes = new List<string> { "department_id" }
            }
        });
        var ex = Assert.Throws<ApiException>(() => ShapeApp.FromTypes(new[] { typeof(RsBook) }, config));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("__scope_department_id", ex.Message);

        // With the placeholder present (PHP-style `:` accepted), it boots.
        var ok = BaseConfig(masks: new()
        {
            ["rs_books.report"] = new MaskConfig
            {
                Sql = "SELECT id, title FROM rs_books WHERE department_id = :__scope_department_id",
                Roles = new() { "user" },
                RowScopes = new List<string> { "department_id" }
            }
        });
        ShapeApp.FromTypes(new[] { typeof(RsBook) }, ok);
    }

    [Fact]
    public void Writable_mask_is_exempt_from_the_placeholder_rule()
    {
        var config = BaseConfig(masks: new()
        {
            ["rs_books"] = new MaskConfig
            {
                Sql = "SELECT id FROM rs_books LIMIT 1",
                Roles = new() { "user" },
                Writable = true,
                RowScopes = new List<string> { "department_id" }
            }
        });
        // Probe SQL has no placeholder — must NOT throw.
        ShapeApp.FromTypes(new[] { typeof(RsBook) }, config);
    }

    [Fact]
    public void Unknown_declared_scope_fails_boot()
    {
        var config = BaseConfig(masks: new()
        {
            ["rs_books.report"] = new MaskConfig
            {
                Sql = "SELECT id FROM rs_books WHERE warehouse_id = :__scope_warehouse_id",
                Roles = new() { "user" },
                RowScopes = new List<string> { "warehouse_id" }
            }
        });
        var ex = Assert.Throws<ApiException>(() => ShapeApp.FromTypes(new[] { typeof(RsBook) }, config));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("unknown row scope 'warehouse_id'", ex.Message);
    }

    [Fact]
    public void Opt_out_must_be_restricted_to_dimension_unscoped_roles()
    {
        var leaking = BaseConfig(masks: new()
        {
            ["rs_books.everything"] = new MaskConfig
            {
                Sql = "SELECT id, title FROM rs_books",
                Roles = new() { "user" },
                RowScopes = false
            }
        });
        var ex = Assert.Throws<ApiException>(() => ShapeApp.FromTypes(new[] { typeof(RsBook) }, leaking));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("opts out", ex.Message);

        // Restricted to the scope's unscoped role → fine.
        var restricted = BaseConfig(masks: new()
        {
            ["rs_books.everything"] = new MaskConfig
            {
                Sql = "SELECT id, title FROM rs_books",
                Roles = new() { "controller" },
                RowScopes = false
            }
        });
        ShapeApp.FromTypes(new[] { typeof(RsBook) }, restricted);
    }

    [Fact]
    public void Undeclared_manual_mask_on_scoped_table_is_a_warning_not_an_error()
    {
        var config = BaseConfig(masks: new()
        {
            ["rs_books.report"] = new MaskConfig
            {
                Sql = "SELECT id, title FROM rs_books",
                Roles = new() { "user" }
            }
        });
        var built = ShapeApp.FromTypes(new[] { typeof(RsBook) }, config);
        Assert.Contains(built.Runtime.Warnings, w =>
            w.Contains("rs_books.report") && w.Contains("row_scopes"));
    }

    [Fact]
    public void Public_cache_control_on_a_row_scoped_mask_fails_boot()
    {
        var config = BaseConfig(masks: new()
        {
            ["rs_books.cached"] = new MaskConfig
            {
                Sql = "SELECT id, title FROM rs_books WHERE department_id = :__scope_department_id",
                Roles = new() { "user" },
                RowScopes = new List<string> { "department_id" },
                CacheControl = "public, max-age=60"
            }
        });
        var ex = Assert.Throws<ApiException>(() => ShapeApp.FromTypes(new[] { typeof(RsBook) }, config));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("public cache-control", ex.Message);
    }
}
