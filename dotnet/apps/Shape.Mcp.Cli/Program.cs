using Microsoft.Extensions.Configuration;
using Shape;
using Shape.Auth;
using Shape.Core;
using Shape.Mcp;

// Shape MCP stdio bridge. Configuration is loaded from (in priority order):
//
//   1. Command-line args (e.g. --Db:Driver=sqlite --Db:ConnectionString=…)
//   2. Environment variables prefixed with SHAPE_ (e.g. SHAPE__Db__Driver=sqlite)
//   3. appsettings.json in the current working directory
//
// The role passed to MCP defaults to "admin" (the user invoked this binary
// locally) but can be overridden via SHAPE_MCP_ROLE env var or --Role argument.

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables(prefix: "SHAPE__")
    .AddCommandLine(args)
    .Build();

var driver = configuration["Db:Driver"] ?? "sqlite";
var connectionString = configuration["Db:ConnectionString"]
    ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "shape.db")}";

var shapeConfig = new ShapeConfig
{
    ServiceName = configuration["ServiceName"] ?? "shape",
    Db = new DatabaseConfig { Driver = driver, ConnectionString = connectionString },
    Auth = new AuthConfig { Driver = configuration["Auth:Driver"] ?? "none" },
    AdminMasks = bool.TryParse(configuration["AdminMasks"], out var am) && am,
    Audit = !bool.TryParse(configuration["Audit"], out var au) || au
};

// Boot the runtime from the live database.
var built = ShapeApp.FromDatabase(shapeConfig);

var role = configuration["Role"]
    ?? Environment.GetEnvironmentVariable("SHAPE_MCP_ROLE")
    ?? "admin";

var server = new McpServer(built, new Dictionary<string, object?> { ["role"] = role });
var transport = new StdioTransport(server);

// Block on stdin until the parent process closes the pipe.
transport.Serve();
