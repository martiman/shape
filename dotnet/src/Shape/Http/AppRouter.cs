using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Shape.Core;

namespace Shape.Http;

public static class AppRouter
{
    public static void MapShapeRuntime(this IEndpointRouteBuilder app, ShapeConfig config)
    {
        var dispatcher = new OpDispatcher(config.Db, config.Auth, config.Masks, config.Validators, config.Commands,
            auditLogTable: null, commandRoles: config.CommandRoles);

        app.MapGet(config.Endpoint, () => Results.Json(new Dictionary<string, object?>
        {
            ["service"] = config.ServiceName,
            ["status"] = "ok",
            ["endpoint"] = config.Endpoint,
            ["message"] = "Shape runtime is running. Send POST JSON with op and payload."
        }));

        app.MapPost(config.Endpoint, (HttpContext context) => FatalHandler.RunSafelyAsync(context, async () =>
        {
            var (op, payload) = await RequestEnvelope.ReadAsync(context);
            var (result, status) = dispatcher.Dispatch(context, op, payload);
            await JsonResponse.SuccessAsync(context, result, status);
        }));
    }
}
