using Shape;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class ShapeAppFromEnvTests
{
    [Fact]
    public void FromEnv_loads_json_overlays_env_and_invokes_customize()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), "shape_env_" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={dbFile}";
        var configDir = Path.Combine(Path.GetTempPath(), "shape-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);

        try
        {
            using (var bootstrap = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = connectionString }))
            {
                bootstrap.Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL)", new());
            }

            var configPath = Path.Combine(configDir, "shape.json");
            File.WriteAllText(configPath, $$"""
            {
                "serviceName": "env-test",
                "db": {
                    "driver": "sqlite",
                    "connectionString": {{System.Text.Json.JsonSerializer.Serialize(connectionString)}}
                },
                "auth": "jwt",
                "access": { "orders": ["admin"] }
            }
            """);

            var customizeRan = false;
            var cfg = ShapeApp.FromEnv(configPath, customize: c =>
            {
                customizeRan = true;
                c.Commands ??= new();
                c.Commands["custom.cmd"] = (p, ctx) => new() { ["ok"] = true };
            });

            Assert.True(customizeRan);
            Assert.Equal("env-test", cfg.ServiceName);
            Assert.Equal("jwt", cfg.Auth.Driver);
            // JwtSecretMaterializer ran — secret is now non-empty (auto-generated under the config dir).
            Assert.False(string.IsNullOrEmpty(cfg.Auth.JwtSecret));
            Assert.True(File.Exists(Path.Combine(configDir, ".shape", "jwt-secret")));
            // Customize callback's command merged into final config.
            Assert.True(cfg.Commands!.ContainsKey("custom.cmd"));
            // Generated CRUD masks for `orders` got admin role stamped.
            Assert.Contains("admin", cfg.Masks["orders.list"].Roles!);
        }
        finally
        {
            if (File.Exists(dbFile)) File.Delete(dbFile);
            if (Directory.Exists(configDir)) Directory.Delete(configDir, recursive: true);
        }
    }
}
