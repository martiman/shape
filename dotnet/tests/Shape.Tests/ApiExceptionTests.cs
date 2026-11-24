using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class ApiExceptionTests
{
    [Fact]
    public void Default_ctor_sets_basic_fields()
    {
        var ex = new ApiException("VALIDATION_ERROR", "boom");

        Assert.Equal("VALIDATION_ERROR", ex.ApiCode);
        Assert.Equal("boom", ex.Message);
        Assert.Equal(400, ex.Status);
        Assert.Null(ex.Ref);
        Assert.Null(ex.Details);
    }

    [Fact]
    public void Details_ctor_carries_details_and_inner()
    {
        var inner = new InvalidOperationException("inner");
        var details = new Dictionary<string, object?>
        {
            ["expected"] = "int",
            ["example"] = 42
        };

        var ex = new ApiException("PARAM_VALIDATION_ERROR", "bad param", 400, "q1", details, inner);

        Assert.Equal("PARAM_VALIDATION_ERROR", ex.ApiCode);
        Assert.Equal("q1", ex.Ref);
        Assert.NotNull(ex.Details);
        Assert.Equal("int", ex.Details!["expected"]);
        Assert.Equal(42, ex.Details["example"]);
        Assert.Same(inner, ex.InnerException);
    }
}
