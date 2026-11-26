using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Shape.Auth;
using Shape.Core;
using Shape.Http;
using Shape.Generator;
using Shape.Mcp;
using Shape.Pdf;
using Shape.Upload;

namespace Shape;

public static class ShapeRouter
{
    public static void MapShape(this IEndpointRouteBuilder app, ShapeConfig config)
    {
        var dispatcher = new OpDispatcher(
            config.Db, config.Auth, config.Masks, config.Validators, config.Commands,
            config.Runtime.AuditLogTable, config.CommandRoles, config.AuditDiff);
        var manifestGen = new ManifestGenerator();
        var openApiGen = new OpenApiGenerator();
        var uploadManager = new UploadManager(config.Uploads);
        var mcpServer = new McpServer(config);

        var endpoint = NormalizeEndpoint(config.Endpoint);
        var catchAll = endpoint == "/" ? "/{**shapePath}" : endpoint.TrimEnd('/') + "/{**shapePath}";

        app.MapGet(endpoint, context => HandleGet(context, config, dispatcher, manifestGen, openApiGen, mcpServer, uploadManager, null));
        app.MapGet(catchAll, context =>
        {
            var path = context.Request.RouteValues.TryGetValue("shapePath", out var value) ? value?.ToString() : null;
            return HandleGet(context, config, dispatcher, manifestGen, openApiGen, mcpServer, uploadManager, path);
        });

        app.MapPost(endpoint, context => HandlePost(context, config, dispatcher, manifestGen, openApiGen, mcpServer));
        app.MapPost(catchAll, context =>
        {
            var path = context.Request.RouteValues.TryGetValue("shapePath", out var value) ? value?.ToString() : null;
            return HandlePostPath(context, config, dispatcher, uploadManager, path);
        });

        app.MapMethods(catchAll, new[] { "DELETE" }, context =>
        {
            var path = context.Request.RouteValues.TryGetValue("shapePath", out var value) ? value?.ToString() : null;
            return FatalHandler.RunSafelyAsync(context, async () =>
            {
                var pathInfo = (path ?? string.Empty).Trim('/');
                if (!pathInfo.StartsWith("files/", StringComparison.Ordinal))
                    throw new ApiException("BAD_REQUEST", "Only GET and POST are allowed.", 405);
                await HandleFile(context, dispatcher, uploadManager, "DELETE", pathInfo["files/".Length..]);
            });
        });

        app.MapMethods(endpoint, new[] { "OPTIONS" }, HttpTransport.HandleOptionsAsync);
    }

    private static Task HandlePostPath(
        HttpContext context,
        ShapeConfig config,
        OpDispatcher dispatcher,
        UploadManager uploadManager,
        string? path)
    {
        return FatalHandler.RunSafelyAsync(context, async () =>
        {
            var pathInfo = (path ?? string.Empty).Trim('/');

            // Upload endpoint: POST /upload/<name> multipart/form-data
            if (pathInfo.StartsWith("upload/", StringComparison.Ordinal))
            {
                var name = pathInfo["upload/".Length..];
                using var database = dispatcher.CreateDatabase();
                var authContext = dispatcher.BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));

                if (!context.Request.HasFormContentType)
                {
                    throw new ApiException(
                        "UPLOAD_VALIDATION_ERROR",
                        "Upload requests must use multipart/form-data.",
                        400);
                }

                var form = await context.Request.ReadFormAsync();
                var service = new AttachmentService(uploadManager, dispatcher.CreateExecutor(database));
                var result = service.IsAttachmentEntry(name)
                    ? await service.UploadAsync(
                        name,
                        form.Files,
                        form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal),
                        authContext)
                    : await uploadManager.HandleAsync(name, form.Files, authContext);
                await JsonResponse.SuccessAsync(context, result);
                return;
            }

            throw new ApiException("NOT_FOUND", $"Unknown POST path '{pathInfo}'.", 404);
        });
    }

    /// <summary>
    /// GET streams an attachment, DELETE removes it. A row the caller may not see answers 404
    /// exactly like a missing one.
    /// </summary>
    private static async Task HandleFile(
        HttpContext context,
        OpDispatcher dispatcher,
        UploadManager uploadManager,
        string method,
        string rest)
    {
        var parts = rest.Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || uploadManager.Entry(parts[0]) == null)
            throw new ApiException("NOT_FOUND", "Use /files/<upload>/<id>.", 404);

        var name = parts[0];
        object id = long.TryParse(parts[1], out var n) && parts[1].All(char.IsDigit) ? n : parts[1];

        using var database = dispatcher.CreateDatabase();
        var authContext = dispatcher.BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));
        var service = new AttachmentService(uploadManager, dispatcher.CreateExecutor(database));

        if (method == "DELETE")
        {
            service.Delete(name, id, authContext);
            context.Response.StatusCode = 204;
            return;
        }

        var opened = service.Open(name, id, authContext);
        await using var stream = opened.Stream;
        // attachment + nosniff: never rendered in the app origin. RFC 5987 filename* plus an ASCII fallback.
        var ascii = System.Text.RegularExpressions.Regex.Replace(opened.Filename, "[^A-Za-z0-9._-]+", "_");
        context.Response.StatusCode = 200;
        context.Response.ContentType = opened.MimeType.Length > 0 ? opened.MimeType : "application/octet-stream";
        context.Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(opened.Filename)}";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "private, no-store";
        context.Response.Headers["ETag"] = $"\"{opened.Sha256}\"";
        context.Response.ContentLength = opened.Size;
        await stream.CopyToAsync(context.Response.Body);
    }

    private static async Task HandlePdf(HttpContext context, ShapeConfig config, OpDispatcher dispatcher, string name)
    {
        if (config.Documents == null || config.Documents.Count == 0 || name.Length == 0)
            throw new ApiException("NOT_FOUND", config.Documents == null || config.Documents.Count == 0
                ? "No documents are configured." : "Use /pdf/<name>?<params>.", 404);

        using var database = dispatcher.CreateDatabase();
        var authContext = dispatcher.BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));
        var service = new DocumentService(dispatcher.CreateExecutor(database), config.Documents);
        var query = context.Request.Query.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString(), StringComparer.Ordinal);
        var doc = service.Render(name, query, authContext);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/pdf";
        context.Response.Headers["Content-Disposition"] = $"inline; filename=\"{doc.Filename}\"";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "private, no-store";
        context.Response.ContentLength = doc.Pdf.Length;
        await context.Response.Body.WriteAsync(doc.Pdf);
    }

    private static Task HandleGet(
        HttpContext context,
        ShapeConfig config,
        OpDispatcher dispatcher,
        ManifestGenerator manifestGen,
        OpenApiGenerator openApiGen,
        McpServer mcpServer,
        UploadManager uploadManager,
        string? path)
    {
        return FatalHandler.RunSafelyAsync(context, async () =>
        {
            var pathInfo = (path ?? string.Empty).Trim('/');

            // PDF documents: GET /pdf/<name>?<params>; the data comes from masks run in the caller's context.
            if (pathInfo.StartsWith("pdf/", StringComparison.Ordinal))
            {
                await HandlePdf(context, config, dispatcher, pathInfo["pdf/".Length..]);
                return;
            }

            // Private attachments: GET /files/<upload>/<id>, authorised through the attachment table's scoped .get mask.
            if (pathInfo.StartsWith("files/", StringComparison.Ordinal))
            {
                await HandleFile(context, dispatcher, uploadManager, "GET", pathInfo["files/".Length..]);
                return;
            }

            if (string.IsNullOrEmpty(pathInfo) && context.Request.Query.ContainsKey("mcp"))
            {
                await HttpTransport.HandleGetAsync(context, config.ServiceName);
                return;
            }

            if (string.IsNullOrEmpty(pathInfo) && context.Request.Query.ContainsKey("docs"))
            {
                if (!config.DocsUi)
                    throw new ApiException("NOT_FOUND", "The docs UI is not enabled.", 404, null,
                        new Dictionary<string, object?>
                        {
                            ["hint"] = "Set ShapeConfig.DocsUi = true (\"docs_ui\": true in JSON config) to serve the interactive API reference."
                        });
                // The page holds no schema data — the ?openapi fetch it makes is
                // what manifest_roles gates; the page prompts for a token on 401.
                context.Response.ContentType = "text/html; charset=utf-8";
                // SECURITY-REVIEW L8: baseline hardening headers on the HTML page.
                // (No strict CSP — the viewer loads a CDN bundle; a nosniff +
                // frame + referrer set is the safe, non-breaking subset.)
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                await context.Response.WriteAsync(DocsUiPage.Render(
                    config.ServiceName, config.Endpoint + "?openapi", config.DocsUiAssetUrl));
                return;
            }

            if (string.IsNullOrEmpty(pathInfo) && context.Request.Query.ContainsKey("manifest"))
            {
                AuthorizeManifest(context, config, dispatcher);
                await JsonResponse.SuccessAsync(context,
                    manifestGen.Generate(config.Runtime.Entities, config.Masks, config.Runtime.Commands));
                return;
            }

            if (string.IsNullOrEmpty(pathInfo) && context.Request.Query.ContainsKey("openapi"))
            {
                AuthorizeManifest(context, config, dispatcher);
                await JsonResponse.SuccessAsync(context,
                    openApiGen.Generate(config.Runtime.Entities, config.Masks, config.Runtime.Commands, config.Auth, config.ServiceName));
                return;
            }

            // Generic mask GET: /_/<mask-name>?<param>=<value>&...
            // Each query string entry maps to a mask param. The mask must be
            // declared in config; the role gate runs as usual.
            if (pathInfo.StartsWith("_/", StringComparison.Ordinal))
            {
                await HandleGenericMaskGet(context, dispatcher, pathInfo["_/".Length..]);
                return;
            }

            if (!string.IsNullOrEmpty(pathInfo))
            {
                await HandleODataGet(context, config, dispatcher, pathInfo);
                return;
            }

            var health = new Dictionary<string, object?>
            {
                ["service"] = config.ServiceName,
                ["status"] = "ok",
                ["engine"] = "shape",
                ["endpoint"] = config.Endpoint,
                ["manifest"] = config.Endpoint + "?manifest",
                ["openapi"] = config.Endpoint + "?openapi",
                ["message"] = "Shape backend is running. Supports OData GET and POST JSON."
            };
            if (config.DocsUi)
                health["docs"] = config.Endpoint + "?docs";
            await JsonResponse.SuccessAsync(context, health);
        });
    }

    private static Task HandlePost(
        HttpContext context,
        ShapeConfig config,
        OpDispatcher dispatcher,
        ManifestGenerator manifestGen,
        OpenApiGenerator openApiGen,
        McpServer mcpServer)
    {
        return FatalHandler.RunSafelyAsync(context, async () =>
        {
            // POST /api?mcp — JSON-RPC over HTTP. The body is a JSON-RPC envelope,
            // not Shape's `{op, payload}`, so dispatch before RequestEnvelope.ReadAsync
            // gets a chance to reject it.
            if (context.Request.Query.ContainsKey("mcp"))
            {
                using var database = dispatcher.CreateDatabase();
                var authContext = dispatcher.BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));
                await HttpTransport.HandlePostAsync(context, mcpServer, authContext);
                return;
            }

            var (op, payload) = await RequestEnvelope.ReadAsync(context);

            if (op is "manifest" or "openapi")
            {
                AuthorizeManifest(context, config, dispatcher);
                var shapeResult = op == "openapi"
                    ? openApiGen.Generate(config.Runtime.Entities, config.Masks, config.Runtime.Commands, config.Auth, config.ServiceName)
                    : manifestGen.Generate(config.Runtime.Entities, config.Masks, config.Runtime.Commands);
                await JsonResponse.SuccessAsync(context, shapeResult);
                return;
            }

            var (result, status) = dispatcher.Dispatch(context, op, payload);
            await JsonResponse.SuccessAsync(context, result, status);
        });
    }

    private static async Task HandleODataGet(HttpContext context, ShapeConfig config, OpDispatcher dispatcher, string pathInfo)
    {
        var parsed = ParseODataPath(pathInfo);
        var entity = parsed.Entity;
        var id = parsed.Id;
        var related = parsed.Related;
        var options = ParseODataQuery(context.Request.Query);

        using var database = dispatcher.CreateDatabase();
        var authContext = dispatcher.BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));

        var executor = new BatchExecutor(database, dispatcher.Masks, dispatcher.Validators, dispatcher.AuthConfig.Tenancy, dispatcher.AuthConfig.Scopes);

        string maskName;
        var parameters = new Dictionary<string, object?>();

        if (id != null && related != null)
        {
            var fkField = FindFkField(config.Runtime.Entities, related, entity)
                ?? throw new ApiException("NOT_FOUND", $"No relationship from '{related}' to '{entity}'.", 404);
            maskName = related + ".by" + PascalCase(fkField);
            parameters[fkField] = id;
        }
        else if (id != null)
        {
            maskName = entity + ".get";
            parameters[FindPkName(config.Runtime.Entities, entity)] = id;
        }
        else
        {
            maskName = entity + ".list";
        }

        var queryItem = new Dictionary<string, object?>
        {
            ["ref"] = entity,
            ["sql"] = maskName,
            ["params"] = parameters
        };

        var queryOptions = new Dictionary<string, object?>();
        if (options.OrderBy.Count > 0)
        {
            queryOptions["orderby"] = options.OrderBy
                .Select(order => new Dictionary<string, object?> { ["field"] = order.Field, ["dir"] = order.Dir })
                .Cast<object?>()
                .ToList();
        }
        if (options.Top.HasValue) queryOptions["top"] = options.Top.Value;
        if (options.Skip.HasValue) queryOptions["skip"] = options.Skip.Value;
        if (options.Count) queryOptions["count"] = true;

        // $filter: compile against the entity's column whitelist; BatchExecutor
        // wraps the resolved mask SQL with WHERE before pagination push-down.
        // The compiled fragment travels the TRUSTED `precompiledFilters` channel,
        // never the client-controllable query `options` map (that path was an
        // SQL-injection vector via a client-supplied `__filterSql` key).
        Dictionary<string, BatchExecutor.PrecompiledFilter>? precompiledFilters = null;
        if (!string.IsNullOrEmpty(options.Filter))
        {
            var meta = config.Runtime.Entities.FirstOrDefault(e => e.Table == entity);
            if (meta == null)
                throw new ApiException("NOT_FOUND",
                    $"Entity '{entity}' is not declared; $filter requires a declared entity.", 404);
            var allowedColumns = meta.Fields.Select(f => f.Name).ToList();
            var (filterSql, filterParams) = ODataFilter.Compile(
                options.Filter, allowedColumns, database.QuoteIdentifier);
            precompiledFilters = new Dictionary<string, BatchExecutor.PrecompiledFilter>
            {
                [(string)queryItem["ref"]!] = new BatchExecutor.PrecompiledFilter(filterSql, filterParams)
            };
        }

        if (queryOptions.Count > 0) queryItem["options"] = queryOptions;

        var result = executor.Execute(new List<Dictionary<string, object?>> { queryItem }, authContext, precompiledFilters);
        var queryResult = (result["results"] as List<Dictionary<string, object?>>)?.FirstOrDefault()
            ?? new Dictionary<string, object?>();

        var columns = (queryResult["meta"] as Dictionary<string, object?>)?["columns"] as List<string> ?? new List<string>();
        var rows = queryResult["rows"] as List<object?[]> ?? new List<object?[]>();

        var items = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var item = new Dictionary<string, object?>();
            for (var i = 0; i < columns.Count; i++)
            {
                if (options.Select.Count == 0 || options.Select.Contains(columns[i], StringComparer.Ordinal))
                    item[columns[i]] = i < row.Length ? row[i] : null;
            }

            items.Add(item);
        }

        if (id != null && related == null)
        {
            var single = items.FirstOrDefault();
            if (single == null)
                throw new ApiException("NOT_FOUND", $"Entity '{entity}' with id '{id}' not found.", 404);
            await JsonResponse.SuccessAsync(context, single);
            return;
        }

        var response = new Dictionary<string, object?> { ["value"] = items };
        if (options.Count)
            response["@odata.count"] = queryResult.TryGetValue("total_count", out var totalCount) ? totalCount : items.Count;

        var scopeTable = related ?? entity;
        // Same predicate as the `public` cache-control boot error: tenant scope
        // OR any row-scope dimension makes the response caller-dependent.
        var scopeMeta = config.Runtime.Entities.FirstOrDefault(e => e.Table == scopeTable);
        var varyAuthorization = scopeMeta?.TenantScoped == true || scopeMeta?.ScopeColumns is { Count: > 0 };
        await EmitWithCacheHeaders(context, response, MaskCacheControl(config, "list", entity), varyAuthorization);
    }

    /// <summary>
    /// Run an arbitrary mask by name. Query-string entries map to mask params.
    /// The mask must be registered in config; <see cref="MaskRegistry.Resolve"/>
    /// runs the role gate.
    /// </summary>
    private static async Task HandleGenericMaskGet(HttpContext context, OpDispatcher dispatcher, string maskName)
    {
        if (string.IsNullOrEmpty(maskName))
            throw new ApiException("NOT_FOUND", "Mask name is required after '/_/'.", 404);

        using var database = dispatcher.CreateDatabase();
        var authContext = dispatcher.BuildGuard(database).Authorize(AdminAuth.NormalizeHeaders(context));

        var queryParams = new Dictionary<string, object?>();
        foreach (var (key, value) in context.Request.Query)
        {
            if (key.StartsWith("$", StringComparison.Ordinal)) continue; // reserved for OData/system options
            queryParams[key] = value.ToString();
        }

        var executor = new BatchExecutor(database, dispatcher.Masks, dispatcher.Validators, dispatcher.AuthConfig.Tenancy, dispatcher.AuthConfig.Scopes);
        var result = executor.Execute(
            new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["ref"] = maskName,
                    ["sql"] = maskName,
                    ["params"] = queryParams
                }
            },
            authContext);

        await JsonResponse.SuccessAsync(context, result);
    }

    private static async Task EmitWithCacheHeaders(HttpContext context, object body, string? cacheControl, bool varyAuthorization = false)
    {
        if (varyAuthorization)
            context.Response.Headers["Vary"] = "Authorization";

        var bytes = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(body, JsonResponse.JsonOptions));

        if (CacheHeaders.ShouldRespond304(context, bytes, cacheControl))
            return; // 304 already set; body intentionally skipped

        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(bytes);
    }

    /// <summary>
    /// Pick the most specific mask's <c>CacheControl</c> for an OData GET path.
    /// Order: <c>{entity}.{kind}</c> mask, then standalone mask matching the
    /// entity name. Returns <c>null</c> when nothing matches.
    /// </summary>
    private static string? MaskCacheControl(ShapeConfig config, string kind, string entity)
    {
        if (config.Masks.TryGetValue($"{entity}.{kind}", out var mask) && !string.IsNullOrEmpty(mask.CacheControl))
            return mask.CacheControl;
        if (config.Masks.TryGetValue(entity, out var bare) && !string.IsNullOrEmpty(bare.CacheControl))
            return bare.CacheControl;
        return null;
    }

    private static void AuthorizeManifest(HttpContext context, ShapeConfig config, OpDispatcher dispatcher)
    {
        if (config.ManifestRoles == null)
            return;

        var authContext = dispatcher.Guard.Authorize(AdminAuth.NormalizeHeaders(context));

        if (config.ManifestRoles.Count == 0)
            return;

        var role = authContext.TryGetValue("role", out var currentRole) ? currentRole?.ToString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(role) || !config.ManifestRoles.Contains(role))
            throw new ApiException("AUTH_FORBIDDEN", "Your role cannot access the manifest.", 403);
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "/";
        return endpoint.StartsWith('/') ? endpoint : "/" + endpoint;
    }

    private static string? FindFkField(IEnumerable<Metadata.EntityMeta> entities, string relatedTable, string parentTable)
    {
        foreach (var entity in entities)
        {
            if (!string.Equals(entity.Table, relatedTable, StringComparison.Ordinal))
                continue;

            foreach (var field in entity.Fields.Where(field => field.IsFk))
            {
                var target = entities.FirstOrDefault(candidate => candidate.ClrType == field.FkEntityType);
                if (target != null && string.Equals(target.Table, parentTable, StringComparison.Ordinal))
                    return field.Name;
            }
        }
        return null;
    }

    private static string FindPkName(IEnumerable<Metadata.EntityMeta> entities, string table)
        => entities.FirstOrDefault(entity => entity.Table == table)?.PrimaryKey()?.Name ?? "id";

    private static string PascalCase(string value)
        => string.Concat(value
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static (string Entity, object? Id, string? Related) ParseODataPath(string path)
    {
        if (System.Text.RegularExpressions.Regex.Match(path, @"^(\w+)\/([^\/]+)\/(\w+)$") is { Success: true } restRelated)
            return (restRelated.Groups[1].Value, ParseId(restRelated.Groups[2].Value), restRelated.Groups[3].Value);
        if (System.Text.RegularExpressions.Regex.Match(path, @"^(\w+)\(([^)]+)\)\/(\w+)$") is { Success: true } odataRelated)
            return (odataRelated.Groups[1].Value, ParseId(odataRelated.Groups[2].Value), odataRelated.Groups[3].Value);
        if (System.Text.RegularExpressions.Regex.Match(path, @"^(\w+)\/([^\/]+)$") is { Success: true } restSingle)
            return (restSingle.Groups[1].Value, ParseId(restSingle.Groups[2].Value), null);
        if (System.Text.RegularExpressions.Regex.Match(path, @"^(\w+)\(([^)]+)\)$") is { Success: true } odataSingle)
            return (odataSingle.Groups[1].Value, ParseId(odataSingle.Groups[2].Value), null);
        return (path, null, null);
    }

    private static object ParseId(string value)
        => int.TryParse(value, out var number) ? number : value;

    private sealed class ODataOptions
    {
        public List<string> Select { get; init; } = new();
        public List<(string Field, string Dir)> OrderBy { get; init; } = new();
        public int? Top { get; init; }
        public int? Skip { get; init; }
        public bool Count { get; init; }
        public string? Filter { get; init; }
    }

    private static ODataOptions ParseODataQuery(IQueryCollection query)
    {
        var select = query.TryGetValue("$select", out var selectRaw)
            ? selectRaw.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();

        var orderBy = new List<(string Field, string Dir)>();
        if (query.TryGetValue("$orderby", out var orderRaw))
        {
            foreach (var part in orderRaw.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var field = pieces.FirstOrDefault();
                var dir = pieces.Length > 1 ? pieces[1].ToLowerInvariant() : "asc";
                if (!string.IsNullOrWhiteSpace(field) && (dir == "asc" || dir == "desc"))
                    orderBy.Add((field, dir));
            }
        }

        var filterExpr = query.TryGetValue("$filter", out var filterRaw) ? filterRaw.ToString() : null;

        return new ODataOptions
        {
            Select = select,
            OrderBy = orderBy,
            Top = query.TryGetValue("$top", out var topRaw) && int.TryParse(topRaw, out var top) ? top : null,
            Skip = query.TryGetValue("$skip", out var skipRaw) && int.TryParse(skipRaw, out var skip) ? skip : null,
            Count = query.TryGetValue("$count", out var countRaw) && countRaw == "true",
            Filter = string.IsNullOrWhiteSpace(filterExpr) ? null : filterExpr
        };
    }
}
