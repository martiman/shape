using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class ValidatorGeneratorMaxLenTests
{
    private sealed class Book { }

    private static EntityMeta BookMeta(List<string> validators) => new()
    {
        ClrType = typeof(Book),
        Table = "books",
        Fields = new()
        {
            new FieldMeta
            {
                Name = "title",
                ClrType = typeof(string),
                ClrTypeName = "String",
                DbType = "varchar",
                Length = 100,
                Validators = validators
            }
        }
    };

    [Fact]
    public void MaxLen_rule_rejects_strings_longer_than_limit()
    {
        var gen = new ValidatorGenerator();
        var validators = gen.Generate(new[] { BookMeta(new() { "maxLen:5" }) });

        var fn = validators["books.insert"].Validators["title"];

        Assert.Null(fn("hi"));            // within limit
        Assert.Null(fn("hello"));         // exact limit
        Assert.Null(fn(null));            // null skipped
        var error = fn("too-long-here");
        Assert.NotNull(error);
        Assert.Contains("at most 5", error);
    }

    [Fact]
    public void MaxLen_with_invalid_arg_is_a_noop()
    {
        var gen = new ValidatorGenerator();
        var validators = gen.Generate(new[] { BookMeta(new() { "maxLen:not-a-number" }) });

        // No validator entry should be added for the unrecognized rule.
        Assert.False(validators.ContainsKey("books.insert"));
    }

    [Fact]
    public void Combined_notEmpty_and_maxLen_chain()
    {
        var gen = new ValidatorGenerator();
        var validators = gen.Generate(new[] { BookMeta(new() { "notEmpty", "maxLen:10" }) });

        var fn = validators["books.insert"].Validators["title"];

        Assert.NotNull(fn("")); // notEmpty trips first
        Assert.Null(fn("ok"));
        Assert.NotNull(fn("0123456789X")); // 11 chars trips maxLen
    }
}
