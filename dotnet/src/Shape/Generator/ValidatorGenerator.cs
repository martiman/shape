using Shape.Core;
using Shape.Metadata;

namespace Shape.Generator;

public sealed class ValidatorGenerator
{
    public Dictionary<string, ValidatorConfig> Generate(IEnumerable<EntityMeta> entities)
    {
        var validators = new Dictionary<string, ValidatorConfig>();

        foreach (var entity in entities)
        {
            var fieldValidators = BuildFieldValidators(entity);
            if (fieldValidators.Count == 0)
                continue;

            foreach (var action in new[] { "insert", "update" })
            {
                validators[$"{entity.Table}.{action}"] = new ValidatorConfig
                {
                    Validators = fieldValidators
                };
            }
        }

        return validators;
    }

    private static Dictionary<string, Func<object?, string?>> BuildFieldValidators(EntityMeta entity)
    {
        var validators = new Dictionary<string, Func<object?, string?>>();

        foreach (var field in entity.Fields)
        {
            var rules = field.Validators
                .Select(ToCheck)
                .Where(check => check != null)
                .Cast<(Func<object?, bool> fn, string message)>()
                .ToList();

            // Enum closed-value-set check from [Column(Values=...)].
            if (field.EnumValues is { Count: > 0 } enumSet)
            {
                var allowed = new HashSet<string>(enumSet, StringComparer.Ordinal);
                var enumMsg = $"{{field}} must be one of: {string.Join(", ", enumSet)}";
                rules.Add((value => value is null || (value is string s && allowed.Contains(s)), enumMsg));
            }

            if (rules.Count == 0)
                continue;

            validators[field.Name] = value =>
            {
                foreach (var (fn, message) in rules)
                {
                    if (!fn(value))
                        return message.Replace("{field}", field.Name, StringComparison.Ordinal);
                }

                return null;
            };
        }

        return validators;
    }

    private static (Func<object?, bool> fn, string message)? ToCheck(string rule)
    {
        // Parameterised rules look like "maxLen:255" — split on the first colon.
        var colon = rule.IndexOf(':');
        if (colon > 0)
        {
            var name = rule[..colon];
            var arg = rule[(colon + 1)..];
            switch (name)
            {
                case "maxLen":
                    if (int.TryParse(arg, out var max) && max > 0)
                    {
                        return (
                            value => value == null
                                || (value is string s && s.Length <= max)
                                || (value is not string && value.ToString()?.Length <= max),
                            $"{{field}} must be at most {max} characters");
                    }
                    return null;
            }
        }

        return rule switch
        {
            "positive" => (value => TryDouble(value, out var number) && number > 0, "{field} must be positive"),
            "nonNegative" => (value => TryDouble(value, out var number) && number >= 0, "{field} must be non-negative"),
            "notEmpty" => (value => value is string s && !string.IsNullOrWhiteSpace(s), "{field} is required"),
            "email" => (value => value is string s && s.Contains('@') && s.Contains('.', StringComparison.Ordinal), "{field} must be a valid email"),
            _ => null
        };
    }

    private static bool TryDouble(object? value, out double number)
    {
        if (value is null)
        {
            number = 0;
            return false;
        }

        return double.TryParse(
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out number);
    }
}
