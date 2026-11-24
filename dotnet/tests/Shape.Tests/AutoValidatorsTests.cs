using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class AutoValidatorsTests
{
    private static FieldMeta Field(
        string dbType, bool nullable = false, bool autoIncrement = false,
        bool hasDefault = false, int? length = null) => new()
    {
        Name = "x",
        ClrType = typeof(string),
        ClrTypeName = "String",
        DbType = dbType,
        Nullable = nullable,
        IsAutoIncrement = autoIncrement,
        HasDefault = hasDefault,
        Length = length
    };

    [Fact]
    public void Non_string_types_get_no_rules()
    {
        Assert.Empty(AutoValidators.FromField(Field("int")));
        Assert.Empty(AutoValidators.FromField(Field("datetime")));
        Assert.Empty(AutoValidators.FromField(Field("tinyint")));
    }

    [Fact]
    public void Required_string_gets_notEmpty()
    {
        var rules = AutoValidators.FromField(Field("varchar"));
        Assert.Contains("notEmpty", rules);
    }

    [Theory]
    [InlineData("nvarchar")]
    [InlineData("nchar")]
    [InlineData("ntext")]
    public void Mssql_unicode_string_types_get_rules(string dbType)
    {
        var rules = AutoValidators.FromField(Field(dbType, length: 100));
        Assert.Contains("notEmpty", rules);
        Assert.Contains("maxLen:100", rules);
    }

    [Fact]
    public void Nvarchar_max_gets_no_maxLen()
    {
        // nvarchar(max) reports CHARACTER_MAXIMUM_LENGTH = -1; the MSSQL
        // introspector normalizes that to Length = null → no bogus maxLen:-1.
        var rules = AutoValidators.FromField(Field("nvarchar", length: null));
        Assert.DoesNotContain(rules, r => r.StartsWith("maxLen"));
    }

    [Fact]
    public void Nullable_string_skips_notEmpty()
    {
        var rules = AutoValidators.FromField(Field("varchar", nullable: true));
        Assert.DoesNotContain("notEmpty", rules);
    }

    [Fact]
    public void AutoIncrement_string_skips_notEmpty()
    {
        var rules = AutoValidators.FromField(Field("varchar", autoIncrement: true));
        Assert.DoesNotContain("notEmpty", rules);
    }

    [Fact]
    public void HasDefault_string_skips_notEmpty()
    {
        var rules = AutoValidators.FromField(Field("varchar", hasDefault: true));
        Assert.DoesNotContain("notEmpty", rules);
    }

    [Fact]
    public void Length_emits_maxLen_rule()
    {
        var rules = AutoValidators.FromField(Field("varchar", length: 255));
        Assert.Contains("maxLen:255", rules);
    }

    [Fact]
    public void Required_string_with_length_gets_both_rules_in_order()
    {
        var rules = AutoValidators.FromField(Field("varchar", length: 64));
        Assert.Equal(new[] { "notEmpty", "maxLen:64" }, rules);
    }

    [Theory]
    [InlineData("varchar")]
    [InlineData("CHAR")]
    [InlineData("text")]
    [InlineData("tinytext")]
    [InlineData("character varying")]
    public void Recognized_string_types_are_handled_case_insensitively(string dbType)
    {
        var rules = AutoValidators.FromField(Field(dbType, length: 10));
        Assert.Contains("maxLen:10", rules);
    }
}
