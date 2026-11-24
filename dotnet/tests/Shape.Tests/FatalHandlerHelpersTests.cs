using MySqlConnector;
using Shape.Http;
using Xunit;

namespace Shape.Tests;

public sealed class FatalHandlerHelpersTests
{
    [Fact]
    public void MessageHint_for_OutOfMemory_mentions_top_and_pagination()
    {
        var hint = FatalHandler.MessageHint(new OutOfMemoryException());
        Assert.Contains("top", hint);
        Assert.Contains("paginate", hint);
    }

    [Fact]
    public void MessageHint_for_Timeout_mentions_timeout()
    {
        var hint = FatalHandler.MessageHint(new TimeoutException());
        Assert.Contains("timeout", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MessageHint_for_DbException_with_deadlock_message_says_retry()
    {
        // MySqlException is the closest concrete DbException we already depend on.
        // Build it via reflection to avoid the SDK-only constructor surface.
        var ex = (MySqlException)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(MySqlException));
        typeof(Exception).GetField("_message",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(ex, "Deadlock found when trying to get lock");

        var hint = FatalHandler.MessageHint(ex);
        Assert.Contains("deadlock", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MessageHint_default_points_at_logs()
    {
        var hint = FatalHandler.MessageHint(new ApplicationException("nothing specific"));
        Assert.Contains("server log", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MessageHint_for_InvalidOperation_with_transaction_text_says_transaction()
    {
        var hint = FatalHandler.MessageHint(new InvalidOperationException("Cannot commit transaction after dispose"));
        Assert.Contains("Transaction state", hint);
    }

    [Theory]
    [InlineData(
        "Failure at /Users/me/dev/proj/src/Shape/Core/X.cs line 1",
        "Failure at X.cs line 1")]
    [InlineData(
        @"Failure at C:\repo\src\Shape\Core\Database.cs near line 99",
        "Failure at Database.cs near line 99")]
    [InlineData(
        "Loaded /opt/app/bin/Shape.dll at startup",
        "Loaded Shape.dll at startup")]
    [InlineData(
        "no path to scrub here",
        "no path to scrub here")]
    public void SanitizeMessage_strips_absolute_paths_keeps_basenames(string input, string expected)
    {
        Assert.Equal(expected, FatalHandler.SanitizeMessage(input));
    }

    [Fact]
    public void SanitizeMessage_handles_null_and_empty()
    {
        Assert.Equal("", FatalHandler.SanitizeMessage(""));
        Assert.Null(FatalHandler.SanitizeMessage(null!));
    }

    [Fact]
    public void Register_is_idempotent()
    {
        // Calling twice must not throw and must not register handlers twice.
        // (We can't directly observe the handler list, but the call should be safe.)
        FatalHandler.Register();
        FatalHandler.Register();
        FatalHandler.Register();
    }
}
