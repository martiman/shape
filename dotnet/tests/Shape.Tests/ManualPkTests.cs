using Shape.Config;
using Shape.Core;
using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

/// <summary>
/// Manual primary-key assignment (MANUAL-PK-PLAN.md): allocating-INSERT statement
/// shapes per driver, strategy resolution (auto rule + forced-manual guards),
/// config plumbing, mask/manifest emission, and the SQLite end-to-end pipeline.
/// </summary>
public sealed class ManualPkTests
{
    // ── N1.1: statement shape per driver ────────────────────────────────

    [Fact]
    public void Mssql_allocating_insert_uses_output_isnull_and_range_lock()
    {
        Assert.Equal(
            "INSERT INTO [invoices] ([invoice_id], [title], [total]) OUTPUT INSERTED.[invoice_id] " +
            "SELECT ISNULL(MAX([invoice_id]), 0) + 1, @title, @total FROM [invoices] WITH (UPDLOCK, HOLDLOCK)",
            Database.InsertAllocatingPkSql(DbDriver.Mssql, "invoices", "invoice_id", new[] { "title", "total" }));
    }

    [Fact]
    public void Pgsql_allocating_insert_uses_coalesce_and_trailing_returning()
    {
        Assert.Equal(
            "INSERT INTO \"invoices\" (\"id\", \"title\") " +
            "SELECT COALESCE(MAX(\"id\"), 0) + 1, @title FROM \"invoices\" RETURNING \"id\"",
            Database.InsertAllocatingPkSql(DbDriver.Pgsql, "invoices", "id", new[] { "title" }));
    }

    [Fact]
    public void Mysql_and_sqlite_allocating_inserts_have_no_returning_clause()
    {
        Assert.Equal(
            "INSERT INTO `invoices` (`id`, `title`) " +
            "SELECT COALESCE(MAX(`id`), 0) + 1, @title FROM `invoices`",
            Database.InsertAllocatingPkSql(DbDriver.Mysql, "invoices", "id", new[] { "title" }));

        Assert.Equal(
            "INSERT INTO \"invoices\" (\"id\", \"title\") " +
            "SELECT COALESCE(MAX(\"id\"), 0) + 1, @title FROM \"invoices\"",
            Database.InsertAllocatingPkSql(DbDriver.Sqlite, "invoices", "id", new[] { "title" }));
    }

    [Fact]
    public void Allocating_insert_supports_a_pk_only_row()
    {
        // Empty-columns edge: a row consisting of the allocated PK alone.
        Assert.Equal(
            "INSERT INTO [t] ([id]) OUTPUT INSERTED.[id] " +
            "SELECT ISNULL(MAX([id]), 0) + 1 FROM [t] WITH (UPDLOCK, HOLDLOCK)",
            Database.InsertAllocatingPkSql(DbDriver.Mssql, "t", "id", Array.Empty<string>()));
    }

    [Fact]
    public void Retryable_race_detection_covers_sqlite_constraint_and_pg_sqlstate()
    {
        Assert.True(Database.IsPkAllocationRace(
            new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed", 19)));
        Assert.False(Database.IsPkAllocationRace(
            new Microsoft.Data.Sqlite.SqliteException("disk I/O error", 10)));
        Assert.True(Database.IsPkAllocationRace(new FakeDbException("23505")));
        Assert.False(Database.IsPkAllocationRace(new FakeDbException("42P01")));
    }

    private sealed class FakeDbException : System.Data.Common.DbException
    {
        private readonly string _sqlState;
        public FakeDbException(string sqlState) => _sqlState = sqlState;
        public override string? SqlState => _sqlState;
    }

    // ── N2.2: strategy resolver ─────────────────────────────────────────

    private static FieldMeta Field(
        string name, string dbType, bool pk = false, bool autoIncrement = false,
        bool hasDefault = false, int? scale = null) => new()
    {
        Name = name,
        ClrType = typeof(long),
        ClrTypeName = "Int64",
        DbType = dbType,
        IsPrimary = pk,
        IsAutoIncrement = autoIncrement,
        HasDefault = hasDefault,
        Scale = scale
    };

    private static EntityMeta Entity(string table, params FieldMeta[] fields) => new()
    {
        ClrType = typeof(object),
        Table = table,
        Fields = fields.ToList()
    };

    [Theory]
    [InlineData("int")]
    [InlineData("bigint")]
    [InlineData("smallint")]
    [InlineData("tinyint")]
    public void Auto_rule_detects_manual_for_plain_int_family_pk(string dbType)
    {
        var entity = Entity("t", Field("id", dbType, pk: true), Field("name", "varchar"));
        PkStrategyResolver.Apply(new[] { entity }, null);
        Assert.Equal("manual", entity.PkStrategy);
    }

    [Fact]
    public void Auto_rule_includes_scale_zero_decimals_and_excludes_scaled_ones()
    {
        // Legacy MSSQL schemas often use numeric(9,0) PKs.
        var numericPk = Entity("a", Field("id", "numeric", pk: true, scale: 0));
        var scaledPk = Entity("b", Field("id", "decimal", pk: true, scale: 2));
        PkStrategyResolver.Apply(new[] { numericPk, scaledPk }, null);
        Assert.Equal("manual", numericPk.PkStrategy);
        Assert.Equal("db", scaledPk.PkStrategy);
    }

    [Fact]
    public void Auto_rule_leaves_identity_defaulted_composite_and_string_pks_to_the_db()
    {
        var identity = Entity("a", Field("id", "int", pk: true, autoIncrement: true));
        var defaulted = Entity("b", Field("id", "int", pk: true, hasDefault: true));
        var composite = Entity("c", Field("a", "int", pk: true), Field("b", "int", pk: true));
        var stringPk = Entity("d", Field("code", "varchar", pk: true));
        PkStrategyResolver.Apply(new[] { identity, defaulted, composite, stringPk }, null);
        Assert.All(new[] { identity, defaulted, composite, stringPk },
            e => Assert.Equal("db", e.PkStrategy));
    }

    [Fact]
    public void Per_table_override_beats_the_star_default()
    {
        var invoices = Entity("invoices", Field("id", "int", pk: true, autoIncrement: true));
        var orders = Entity("orders", Field("id", "int", pk: true, autoIncrement: true));
        var map = new Dictionary<string, string> { ["*"] = "db", ["invoices"] = "manual" };
        PkStrategyResolver.Apply(new[] { invoices, orders }, map);
        // Forced manual wins even over an identity column (abandoned-identity case).
        Assert.Equal("manual", invoices.PkStrategy);
        Assert.Equal("db", orders.PkStrategy);
    }

    [Fact]
    public void Forcing_manual_on_composite_or_non_int_pk_fails_loud_at_build_time()
    {
        var composite = Entity("t", Field("a", "int", pk: true), Field("b", "int", pk: true));
        var ex = Assert.Throws<ApiException>(() => PkStrategyResolver.Apply(
            new[] { composite }, new Dictionary<string, string> { ["t"] = "manual" }));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("composite", ex.Message);

        var stringPk = Entity("t", Field("code", "varchar", pk: true));
        ex = Assert.Throws<ApiException>(() => PkStrategyResolver.Apply(
            new[] { stringPk }, new Dictionary<string, string> { ["t"] = "manual" }));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("int-family", ex.Message);

        var noPk = Entity("t", Field("name", "varchar"));
        ex = Assert.Throws<ApiException>(() => PkStrategyResolver.Apply(
            new[] { noPk }, new Dictionary<string, string> { ["*"] = "manual" }));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
    }

    [Fact]
    public void Invalid_mode_values_are_rejected()
    {
        var ex = Assert.Throws<ApiException>(() => PkStrategyResolver.Validate(
            new Dictionary<string, string> { ["*"] = "yes" }));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
    }

    [Fact]
    public void IsForcedManual_only_honors_explicit_manual()
    {
        var map = new Dictionary<string, string> { ["*"] = "auto", ["users"] = "manual" };
        Assert.True(PkStrategyResolver.IsForcedManual(map, "users"));
        Assert.False(PkStrategyResolver.IsForcedManual(map, "refresh_tokens"));
        Assert.False(PkStrategyResolver.IsForcedManual(null, "users"));
        Assert.True(PkStrategyResolver.IsForcedManual(
            new Dictionary<string, string> { ["*"] = "manual" }, "refresh_tokens"));
    }

    // ── N2.1: config plumbing ───────────────────────────────────────────

    private static string WriteTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shape-pk-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ConfigLoader_accepts_string_shorthand_and_object_form()
    {
        var shorthand = WriteTempConfig("""{ "pkAssignment": "manual" }""");
        var objectForm = WriteTempConfig("""{ "pk_assignment": { "*": "auto", "invoices": "manual" } }""");
        try
        {
            var cfg = ConfigLoader.Load(shorthand);
            Assert.Equal("manual", cfg.PkAssignment!["*"]);

            cfg = ConfigLoader.Load(objectForm); // PHP-parity snake_case spelling
            Assert.Equal("auto", cfg.PkAssignment!["*"]);
            Assert.Equal("manual", cfg.PkAssignment["invoices"]);
        }
        finally
        {
            File.Delete(shorthand);
            File.Delete(objectForm);
        }
    }

    [Fact]
    public void ConfigLoader_rejects_invalid_pk_assignment_values()
    {
        var path = WriteTempConfig("""{ "pkAssignment": { "t": "sometimes" } }""");
        try
        {
            var ex = Assert.Throws<ApiException>(() => ConfigLoader.Load(path));
            Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        }
        finally { File.Delete(path); }
    }

    // ── N2.3 / N3.3: mask + manifest + OpenAPI emission ─────────────────

    private static EntityMeta WritableManualEntity()
    {
        var entity = Entity("invoices",
            Field("id", "int", pk: true),
            new FieldMeta { Name = "title", ClrType = typeof(string), ClrTypeName = "String", DbType = "varchar", Length = 50 });
        return new EntityMeta
        {
            ClrType = entity.ClrType,
            Table = entity.Table,
            Writable = true,
            Fields = entity.Fields,
            PkStrategy = "manual"
        };
    }

    [Fact]
    public void Writable_mask_carries_pk_and_pk_strategy()
    {
        var masks = new MaskGenerator("sqlite").Generate(new[] { WritableManualEntity() });
        Assert.Equal("id", masks["invoices"].Pk);
        Assert.Equal("manual", masks["invoices"].PkStrategy);

        // db-assigned PK: `pk` still emitted (the §2.5 fix needs it), strategy absent.
        var dbEntity = WritableManualEntity();
        dbEntity.PkStrategy = "db";
        masks = new MaskGenerator("sqlite").Generate(new[] { dbEntity });
        Assert.Equal("id", masks["invoices"].Pk);
        Assert.Null(masks["invoices"].PkStrategy);
    }

    [Fact]
    public void Manifest_reports_manual_pk_as_generated_and_emits_php_identical_keys()
    {
        var entity = WritableManualEntity();
        var masks = new MaskGenerator("sqlite").Generate(new[] { entity });
        var manifest = new ManifestGenerator().Generate(new[] { entity }, masks, Array.Empty<string>());

        var entities = (List<Dictionary<string, object?>>)manifest["entities"]!;
        var fields = (List<Dictionary<string, object?>>)entities[0]["fields"]!;
        var idField = fields.Single(f => (string)f["name"]! == "id");
        Assert.Equal(true, idField["autoIncrement"]); // §2.4: externally generated

        var entityMasks = (Dictionary<string, object?>)entities[0]["masks"]!;
        var writable = (Dictionary<string, object?>)entityMasks["invoices"]!;
        Assert.Equal("id", writable["pk"]);
        Assert.Equal("manual", writable["pk_strategy"]);
    }

    [Fact]
    public void OpenApi_insert_schema_omits_the_manual_pk()
    {
        var entity = WritableManualEntity();
        var spec = new OpenApiGenerator().Generate(
            new[] { entity }, new Dictionary<string, MaskConfig>(), Array.Empty<string>(),
            new Shape.Auth.AuthConfig(), "svc");

        var paths = (Dictionary<string, object?>)spec["paths"]!;
        var insertPath = (Dictionary<string, object?>)paths["/api?op=mutation&action=insert&table=invoices"]!;
        var json = System.Text.Json.JsonSerializer.Serialize(insertPath);
        Assert.Contains("title", json);
        Assert.DoesNotContain("\"id\"", json);
    }

    // ── N3.2: validators — manual PK is not required ────────────────────

    [Fact]
    public void Resolver_strips_required_rule_from_a_manual_pk()
    {
        var pk = Field("id", "int", pk: true);
        pk.Validators.Add("notEmpty"); // defensive: never emitted for ints today
        var entity = Entity("t", pk);
        PkStrategyResolver.Apply(new[] { entity }, new Dictionary<string, string> { ["t"] = "manual" });
        Assert.DoesNotContain("notEmpty", pk.Validators);
    }

    // ── N3.1 / N5.1: SQLite end-to-end pipeline ─────────────────────────

    private static (ShapeConfig Config, string DbFile) BuildSqliteApp()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), "shape_manual_pk_" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={dbFile}";

        using (var bootstrap = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = connectionString }))
        {
            // `INT` (not `INTEGER`) PRIMARY KEY: no rowid alias, nothing assigns the
            // id — the exact legacy shape the auto rule must catch.
            bootstrap.Exec(@"CREATE TABLE invoices (
              id INT NOT NULL PRIMARY KEY,
              title TEXT NOT NULL
            )", new());
            bootstrap.Exec(@"CREATE TABLE orders (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              title TEXT NOT NULL
            )", new());
        }

        var config = ShapeApp.FromDatabase(new ShapeConfig
        {
            Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = connectionString },
            Access = new() { ["invoices"] = new(), ["orders"] = new() },
            Audit = false
        });
        return (config, dbFile);
    }

    private static Dictionary<string, object?> Insert(
        BatchExecutor executor, string table, Dictionary<string, object?> data)
    {
        var result = executor.ExecuteMutations(new List<Dictionary<string, object?>>
        {
            new() { ["table"] = table, ["action"] = "insert", ["data"] = data }
        });
        return ((List<Dictionary<string, object?>>)result["results"]!)[0];
    }

    [Fact]
    public void Sqlite_pipeline_allocates_sequential_ids_and_passes_explicit_ids_through()
    {
        var (config, dbFile) = BuildSqliteApp();
        try
        {
            // Auto-detection: plain INT PK → manual; AUTOINCREMENT rowid → db.
            Assert.Equal("manual", config.Runtime.Entities.Single(e => e.Table == "invoices").PkStrategy);
            Assert.Equal("db", config.Runtime.Entities.Single(e => e.Table == "orders").PkStrategy);
            Assert.Equal("manual", config.Masks["invoices"].PkStrategy);
            Assert.Equal("id", config.Masks["invoices"].Pk);
            Assert.Null(config.Masks["orders"].PkStrategy);

            using var db = new Database(config.Db);
            var executor = new BatchExecutor(db, new MaskRegistry(config.Masks), new ValidatorRegistry(config.Validators));

            // Sequential allocation from an empty table: 1, 2, 3 — id in each response.
            foreach (var expected in new long[] { 1, 2, 3 })
            {
                var result = Insert(executor, "invoices", new() { ["title"] = "row " + expected });
                Assert.Equal(1, result["affected"]);
                Assert.Equal(expected, result["id"]);
            }

            // Explicit client-supplied id wins verbatim (import/migration flow).
            Insert(executor, "invoices", new() { ["id"] = 42, ["title"] = "imported" });
            var stored = db.Run("SELECT id FROM invoices WHERE title = 'imported'", new());
            Assert.Equal(42L, Convert.ToInt64(stored.Rows[0][0]));

            // A null id counts as absent (PHP isset() parity) → allocation resumes at MAX+1.
            var afterExplicit = Insert(executor, "invoices", new() { ["id"] = null, ["title"] = "next" });
            Assert.Equal(43L, afterExplicit["id"]);

            // The db-strategy table keeps the regular last_insert_rowid path.
            var order = Insert(executor, "orders", new() { ["title"] = "o1" });
            Assert.Equal(1L, order["id"]);

            // Validator contract: the generated insert validators never require the
            // manual PK — the allocation inserts above already prove it, but pin the
            // config shape too.
            if (config.Validators!.TryGetValue("invoices.insert", out var v))
                Assert.False(v.Validators.ContainsKey("id"));
        }
        finally { File.Delete(dbFile); }
    }

    [Fact]
    public void Sqlite_allocation_works_inside_a_caller_owned_transaction()
    {
        var (config, dbFile) = BuildSqliteApp();
        try
        {
            using var db = new Database(config.Db);
            var executor = new BatchExecutor(db, new MaskRegistry(config.Masks), new ValidatorRegistry(config.Validators));

            var result = executor.ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new() { ["ref"] = "a", ["table"] = "invoices", ["action"] = "insert", ["data"] = new Dictionary<string, object?> { ["title"] = "a" } },
                new() { ["ref"] = "b", ["table"] = "invoices", ["action"] = "insert", ["data"] = new Dictionary<string, object?> { ["title"] = "b" } }
            }, useTransaction: true);

            var results = (List<Dictionary<string, object?>>)result["results"]!;
            Assert.Equal(1L, results[0]["id"]);
            Assert.Equal(2L, results[1]["id"]);
            Assert.False(db.IsInTransaction); // committed, not leaked
        }
        finally { File.Delete(dbFile); }
    }

    [Fact]
    public void Manual_pk_users_table_routes_auth_inserts_through_the_allocator()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), "shape_manual_pk_auth_" + Guid.NewGuid().ToString("N") + ".db");
        var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={dbFile}" };
        try
        {
            using (var bootstrap = new Database(dbConfig))
            {
                bootstrap.Exec(@"CREATE TABLE users (
                  id INT NOT NULL PRIMARY KEY,
                  email TEXT NOT NULL UNIQUE,
                  password_hash TEXT NOT NULL,
                  display_name TEXT,
                  role TEXT,
                  is_active INT NOT NULL DEFAULT 1
                )", new());
            }

            var authConfig = new Shape.Auth.AuthConfig
            {
                Driver = "jwt",
                JwtSecret = "test-secret-32-bytes-of-keymaterial",
                PkAssignment = new() { ["users"] = "manual" }
            };
            var login = new Shape.Auth.LoginService(dbConfig, authConfig, new Shape.Auth.JwtIssuer(authConfig));

            var first = login.CreateUser(new() { ["email"] = "a@example.com", ["password"] = "password1" });
            var second = login.CreateUser(new() { ["email"] = "b@example.com", ["password"] = "password1" });
            Assert.Equal(1, first["id"]);
            Assert.Equal(2, second["id"]);
        }
        finally { File.Delete(dbFile); }
    }
}
