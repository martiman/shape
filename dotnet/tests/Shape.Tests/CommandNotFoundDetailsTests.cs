using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class CommandNotFoundDetailsTests
{
    [Fact]
    public void Unknown_command_lists_available_in_expected()
    {
        var executor = new CommandExecutor(
            database: null!,
            executor: null!,
            handlers: new Dictionary<string, Func<Dictionary<string, object?>, CommandContext, Dictionary<string, object?>>>
            {
                ["order.process"] = (_, _) => new(),
                ["inventory.adjust"] = (_, _) => new()
            });

        var ex = Assert.Throws<ApiException>(() =>
            executor.Execute("totally.unknown", new Dictionary<string, object?>()));

        Assert.Equal("COMMAND_NOT_FOUND", ex.ApiCode);
        Assert.Equal(404, ex.Status);

        var expected = (string)ex.Details!["expected"]!;
        Assert.Contains("inventory.adjust", expected);
        Assert.Contains("order.process", expected);

        var example = (Dictionary<string, object?>)ex.Details["example"]!;
        Assert.Equal("command", example["op"]);

        var hint = (string)ex.Details["hint"]!;
        Assert.Contains("manifest", hint);
    }

    [Fact]
    public void No_commands_registered_states_so_explicitly()
    {
        var executor = new CommandExecutor(database: null!, executor: null!, handlers: null);

        var ex = Assert.Throws<ApiException>(() =>
            executor.Execute("foo", new Dictionary<string, object?>()));

        var expected = (string)ex.Details!["expected"]!;
        Assert.Contains("no commands registered", expected);
    }
}
