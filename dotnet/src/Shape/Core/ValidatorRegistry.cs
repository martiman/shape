namespace Shape.Core;

public sealed class ValidatorRegistry
{
    private readonly Dictionary<string, ValidatorConfig> _config;

    public ValidatorRegistry(Dictionary<string, ValidatorConfig>? config = null)
    {
        _config = config ?? new();
    }

    public Dictionary<string, Func<object?, string?>> GetValidators(string table, string action)
    {
        var key = $"{table}.{action}";
        return _config.TryGetValue(key, out var cfg) ? cfg.Validators : new();
    }

    public Action<Dictionary<string, object?>>? GetBeforeHook(string table, string action)
    {
        var key = $"{table}.{action}";
        return _config.TryGetValue(key, out var cfg) ? cfg.Hooks?.Before : null;
    }

    public Action<Dictionary<string, object?>>? GetAfterHook(string table, string action)
    {
        var key = $"{table}.{action}";
        return _config.TryGetValue(key, out var cfg) ? cfg.Hooks?.After : null;
    }

    public string? Validate(string table, string action, string field, object? value)
    {
        var validators = GetValidators(table, action);
        if (!validators.TryGetValue(field, out var validator))
            return null;

        try
        {
            return validator(value);
        }
        catch (Exception e)
        {
            return "Validator error: " + e.Message;
        }
    }
}

public sealed class ValidatorConfig
{
    public Dictionary<string, Func<object?, string?>> Validators { get; set; } = new();
    public HooksConfig? Hooks { get; set; }
}

public sealed class HooksConfig
{
    public Action<Dictionary<string, object?>>? Before { get; set; }
    public Action<Dictionary<string, object?>>? After { get; set; }
}
