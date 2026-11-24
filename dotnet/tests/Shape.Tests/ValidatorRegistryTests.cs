using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class ValidatorRegistryTests
{
    [Fact]
    public void Validate_returns_null_when_validator_passes()
    {
        var registry = new ValidatorRegistry(new Dictionary<string, ValidatorConfig>
        {
            ["orders.insert"] = new()
            {
                Validators = new()
                {
                    ["total"] = v => v is int i && i > 0 ? null : "must be > 0"
                }
            }
        });

        Assert.Null(registry.Validate("orders", "insert", "total", 100));
    }

    [Fact]
    public void Validate_returns_message_when_validator_rejects()
    {
        var registry = new ValidatorRegistry(new Dictionary<string, ValidatorConfig>
        {
            ["orders.insert"] = new()
            {
                Validators = new()
                {
                    ["total"] = v => v is int i && i > 0 ? null : "must be > 0"
                }
            }
        });

        Assert.Equal("must be > 0", registry.Validate("orders", "insert", "total", -1));
    }

    [Fact]
    public void Validate_returns_null_for_unknown_field()
    {
        var registry = new ValidatorRegistry();
        Assert.Null(registry.Validate("orders", "insert", "nonexistent", "anything"));
    }

    [Fact]
    public void Hooks_before_and_after_are_returned()
    {
        var beforeRan = false;
        var afterRan = false;
        var registry = new ValidatorRegistry(new Dictionary<string, ValidatorConfig>
        {
            ["orders.insert"] = new()
            {
                Hooks = new HooksConfig
                {
                    Before = _ => beforeRan = true,
                    After = _ => afterRan = true
                }
            }
        });

        registry.GetBeforeHook("orders", "insert")!(new Dictionary<string, object?>());
        registry.GetAfterHook("orders", "insert")!(new Dictionary<string, object?>());

        Assert.True(beforeRan);
        Assert.True(afterRan);
    }

    [Fact]
    public void Validate_catches_exception_and_returns_message()
    {
        var registry = new ValidatorRegistry(new Dictionary<string, ValidatorConfig>
        {
            ["orders.insert"] = new()
            {
                Validators = new()
                {
                    ["total"] = _ => throw new InvalidOperationException("boom")
                }
            }
        });

        var result = registry.Validate("orders", "insert", "total", 1);

        Assert.NotNull(result);
        Assert.Contains("boom", result);
    }
}
