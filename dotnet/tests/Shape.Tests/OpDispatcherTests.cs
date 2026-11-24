using Microsoft.AspNetCore.Http;
using Shape.Auth;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class OpDispatcherTests
{
    private static OpDispatcher BuildDispatcher() => new(
        new DatabaseConfig { ConnectionString = "Server=unused;Database=unused;User=u;Password=p", Driver = "mysql" },
        new AuthConfig { Driver = "none" },
        new Dictionary<string, MaskConfig>(),
        validators: null,
        commands: null);

    [Fact]
    public void Unknown_op_throws_NOT_FOUND()
    {
        var dispatcher = BuildDispatcher();

        var ex = Assert.Throws<ApiException>(() =>
            dispatcher.Dispatch(new DefaultHttpContext(), "totally.unknown", new Dictionary<string, object?>()));

        Assert.Equal("NOT_FOUND", ex.ApiCode);
        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public void Auth_token_with_missing_credentials_throws_AUTH_VALIDATION_ERROR()
    {
        var dispatcher = BuildDispatcher();

        var ex = Assert.Throws<ApiException>(() =>
            dispatcher.Dispatch(new DefaultHttpContext(), "auth.token", new Dictionary<string, object?>()));

        Assert.Equal("AUTH_VALIDATION_ERROR", ex.ApiCode);
    }

    [Fact]
    public void Auth_login_alias_is_routed_like_auth_token()
    {
        var dispatcher = BuildDispatcher();

        var ex = Assert.Throws<ApiException>(() =>
            dispatcher.Dispatch(new DefaultHttpContext(), "auth.login", new Dictionary<string, object?>()));

        Assert.Equal("AUTH_VALIDATION_ERROR", ex.ApiCode);
    }

    [Fact]
    public void Auth_refresh_with_missing_token_throws_AUTH_VALIDATION_ERROR()
    {
        var dispatcher = BuildDispatcher();

        var ex = Assert.Throws<ApiException>(() =>
            dispatcher.Dispatch(new DefaultHttpContext(), "auth.refresh", new Dictionary<string, object?>()));

        Assert.Equal("AUTH_VALIDATION_ERROR", ex.ApiCode);
    }

    [Fact]
    public void Auth_admin_endpoints_require_jwt_driver()
    {
        var dispatcher = BuildDispatcher();

        var ex = Assert.Throws<ApiException>(() =>
            dispatcher.Dispatch(new DefaultHttpContext(), "auth.admin.create-user", new Dictionary<string, object?>()));

        Assert.Equal("AUTH_CONFIG_ERROR", ex.ApiCode);
    }

    [Fact]
    public void Dispatcher_exposes_runtime_handles()
    {
        var dispatcher = BuildDispatcher();

        Assert.NotNull(dispatcher.Guard);
        Assert.NotNull(dispatcher.AdminAuth);
        Assert.NotNull(dispatcher.Masks);
        Assert.NotNull(dispatcher.Validators);
    }
}
