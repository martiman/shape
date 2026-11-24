using Shape.Attributes;
using Shape.Auth;
using Shape.Core;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

[Entity("pa_deals", Writable = true)]
[Access(Roles = new[] { "user", "admin" }, Delete = new[] { "manager" })]
public sealed class PerActionDeal01
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Column(Length = 100)]
    public string Title { get; set; } = "";
}

[Entity("pa_notes", Writable = true)]
[Access(Roles = new[] { "user", "admin" })]
public sealed class PerActionNote01
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Column(Length = 100)]
    public string Body { get; set; } = "";
}

/// <summary>
/// Per-operation write roles (MULTI-TENANCY-SIMPLE-PLAN S10–S12): the
/// <c>[Access(Create/Update/Delete)]</c> split resolves via the action →
/// Write → Roles cascade, the writable mask's <c>roles</c> gate widens to the
/// union, <c>roles_by_action</c> rides the mask, and <c>BatchExecutor</c>
/// narrows per mutation action.
/// </summary>
public sealed class RolesByActionTests
{
    [Fact]
    public void EntityReader_resolves_per_action_cascade()
    {
        var meta = new EntityReader().ReadTypes(new[] { typeof(PerActionDeal01) }).Single();

        Assert.Equal(new[] { "user", "admin" }, meta.WriteRolesByAction["create"]);
        Assert.Equal(new[] { "user", "admin" }, meta.WriteRolesByAction["update"]);
        Assert.Equal(new[] { "manager" }, meta.WriteRolesByAction["delete"]);
    }

    [Fact]
    public void Flat_access_yields_no_roles_by_action()
    {
        var meta = new EntityReader().ReadTypes(new[] { typeof(PerActionNote01) }).Single();
        Assert.Empty(meta.WriteRolesByAction);

        var masks = new Generator.MaskGenerator().Generate(
            new EntityReader().ReadTypes(new[] { typeof(PerActionNote01) }));
        Assert.Null(masks["pa_notes"].RolesByAction);
        Assert.Equal(new[] { "user", "admin" }, masks["pa_notes"].Roles);
    }

    [Fact]
    public void Writable_mask_carries_union_gate_and_roles_by_action()
    {
        var masks = new Generator.MaskGenerator().Generate(
            new EntityReader().ReadTypes(new[] { typeof(PerActionDeal01) }));

        var writable = masks["pa_deals"];
        // Union gate: mask-level roles must admit the delete-only manager too.
        Assert.Equal(new[] { "user", "admin", "manager" }, writable.Roles);
        Assert.NotNull(writable.RolesByAction);
        Assert.Equal(new[] { "manager" }, writable.RolesByAction!["delete"]);
    }

    [Fact]
    public void Executor_narrows_write_roles_per_action()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"shape-peraction-{Guid.NewGuid():N}.db");
        var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={dbFile}" };
        try
        {
            using var db = new Database(dbConfig);
            db.Exec("CREATE TABLE pa_deals (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL)", new());

            var masks = new Generator.MaskGenerator().Generate(
                new EntityReader().ReadTypes(new[] { typeof(PerActionDeal01) }));
            var executor = new BatchExecutor(db, new MaskRegistry(masks), validators: null);

            var user = new Dictionary<string, object?> { ["role"] = "user" };
            var manager = new Dictionary<string, object?> { ["role"] = "manager" };

            // user may create…
            var created = executor.ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "pa_deals",
                    ["action"] = "insert",
                    ["data"] = new Dictionary<string, object?> { ["title"] = "Big deal" }
                }
            }, user);
            var results = (List<Dictionary<string, object?>>)created["results"]!;
            var newId = results[0]["id"];
            Assert.NotNull(newId);

            // …but not delete.
            var ex = Assert.Throws<ApiException>(() =>
                executor.ExecuteMutations(new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["table"] = "pa_deals",
                        ["action"] = "delete",
                        ["filter"] = new Dictionary<string, object?> { ["id"] = newId }
                    }
                }, user));
            Assert.Equal("AUTH_FORBIDDEN", ex.ApiCode);
            Assert.Equal(403, ex.Status);

            // manager deletes (passes the union mask gate + the delete list)…
            var deleted = executor.ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "pa_deals",
                    ["action"] = "delete",
                    ["filter"] = new Dictionary<string, object?> { ["id"] = newId }
                }
            }, manager);
            var deleteResults = (List<Dictionary<string, object?>>)deleted["results"]!;
            Assert.Equal(1, deleteResults[0]["affected"]);

            // …but cannot create (create resolved to Roles, manager not in them).
            var ex2 = Assert.Throws<ApiException>(() =>
                executor.ExecuteMutations(new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["table"] = "pa_deals",
                        ["action"] = "insert",
                        ["data"] = new Dictionary<string, object?> { ["title"] = "Nope" }
                    }
                }, manager));
            Assert.Equal("AUTH_FORBIDDEN", ex2.ApiCode);
        }
        finally
        {
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }

    [Fact]
    public void Manifest_carries_roles_by_action()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(PerActionDeal01) });
        var masks = new Generator.MaskGenerator().Generate(entities);
        var manifest = new Generator.ManifestGenerator().Generate(entities, masks, new List<string>());

        var entityList = (List<Dictionary<string, object?>>)manifest["entities"]!;
        var deal = entityList.Single(e => (string?)e["table"] == "pa_deals");
        var dealMasks = (Dictionary<string, object?>)deal["masks"]!;
        var writable = (Dictionary<string, object?>)dealMasks["pa_deals"]!;
        var rolesByAction = Assert.IsType<Dictionary<string, object?>>(writable["roles_by_action"]);
        Assert.Equal(new List<string> { "manager" }, rolesByAction["delete"]);
    }

    [Fact]
    public void CreateUser_stamps_configured_tenant_column()
    {
        // Regression: the auth path must honor a renamed discriminator column
        // (tenancy.column = org_id) — SELECT alias + INSERT column both used a
        // literal tenant_id before.
        var dbFile = Path.Combine(Path.GetTempPath(), $"shape-orgcol-{Guid.NewGuid():N}.db");
        var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={dbFile}" };
        try
        {
            using (var db = new Database(dbConfig))
            {
                db.Exec("CREATE TABLE orgs (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1)", new());
                db.Exec("INSERT INTO orgs (id, name) VALUES (7, 'Acme')", new());
                db.Exec(@"CREATE TABLE users (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  org_id INTEGER NOT NULL,
                  email TEXT NOT NULL UNIQUE,
                  password_hash TEXT NOT NULL,
                  display_name TEXT,
                  role TEXT NOT NULL,
                  is_active INTEGER NOT NULL DEFAULT 1
                )", new());
                db.Exec("CREATE TABLE refresh_tokens (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, token_hash TEXT NOT NULL, expires_at TEXT NOT NULL, revoked_at TEXT)", new());
            }

            var auth = new AuthConfig
            {
                Driver = "jwt",
                JwtSecret = "test-secret-must-be-long-enough",
                Tenancy = new TenancyConfig
                {
                    Table = "orgs",
                    Column = "org_id",
                    UnscopedRoles = new() { "superadmin" }
                }
            };
            var svc = new LoginService(dbConfig, auth, new JwtIssuer(auth));

            var superadmin = new Dictionary<string, object?> { ["role"] = "superadmin", ["tenant_id"] = null };
            var created = svc.CreateUser(new Dictionary<string, object?>
            {
                ["email"] = "alice@acme.test",
                ["password"] = "password123",
                ["tenant_id"] = 7
            }, superadmin);
            Assert.NotNull(created["id"]);

            using var verify = new Database(dbConfig);
            var row = verify.Run("SELECT org_id FROM users WHERE email = 'alice@acme.test'", new());
            Assert.Equal(7L, Convert.ToInt64(row.Rows[0][0]));

            // Login round-trips through the renamed column too (SELECT alias).
            var token = svc.Login(new Dictionary<string, object?>
            {
                ["email"] = "alice@acme.test",
                ["password"] = "password123"
            });
            Assert.NotNull(token["access_token"]);
        }
        finally
        {
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }
}
