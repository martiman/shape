using Shape.Auth;
using Shape.Core;
using Shape.Metadata;

namespace Shape.Generator;

/// <summary>
/// Knobs for <see cref="OpenApiGenerator"/> output.
/// </summary>
public sealed class OpenApiConfig
{
    /// <summary>
    /// <c>"get"</c> | <c>"post"</c> | <c>"both"</c>. Controls whether read masks
    /// emit GET paths, POST envelopes, or both. Mutations / auth / commands stay
    /// on POST regardless. Default <c>"get"</c> — duplicate POST envelopes are
    /// pure noise for spec consumers when GET already covers reads.
    /// </summary>
    public string Reads { get; set; } = "get";
}

/// <summary>
/// Renders the <c>?openapi</c> document. Emits auth paths, REST/OData GET
/// surface per entity, generic mask GET paths, optional POST envelopes for
/// reads, mutation paths per writable entity, command paths, plus tags and
/// <c>x-tagGroups</c> for structured navigation in Stoplight / Redoc /
/// Postman.
/// </summary>
public sealed class OpenApiGenerator
{
    public Dictionary<string, object?> Generate(
        IEnumerable<EntityMeta> entities,
        Dictionary<string, MaskConfig> masks,
        IEnumerable<string> commands,
        AuthConfig authConfig,
        string serviceName,
        OpenApiConfig? openApiConfig = null)
    {
        var entityList = entities.ToList();
        var commandList = commands.ToList();

        var reads = (openApiConfig?.Reads ?? "get").ToLowerInvariant();
        if (reads is not ("get" or "post" or "both")) reads = "get";
        var emitReadsAsGet = reads is "get" or "both";
        var emitReadsAsPost = reads is "post" or "both";

        var (tags, tagGroups) = BuildTagsAndGroups(entityList, masks, commandList);

        var paths = new Dictionary<string, object?>();
        var spec = new Dictionary<string, object?>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = char.ToUpperInvariant(serviceName[0]) + serviceName[1..] + " API",
                ["version"] = "1.0.0",
                ["description"] = $"Auto-generated OpenAPI spec for {serviceName}. Three call shapes are documented: " +
                    "(1) GET /api/<entity>[/<id>] — REST/OData reads, ETag/Cache-Control friendly; " +
                    "(2) GET /api/_/<mask>?<params> — generic mask reads; " +
                    "(3) POST /api with JSON body — batch queries, mutations, commands.\n\n" +
                    "**Tip for Postman / Insomnia / Bruno users:** switch the collection's grouping from \"Folder\" to \"Tag\" so operations cluster by entity instead of by path. Stoplight Studio and Redoc honor `x-tagGroups` automatically."
            },
            ["tags"] = tags,
            ["x-tagGroups"] = tagGroups,
            ["paths"] = paths,
            ["components"] = new Dictionary<string, object?>
            {
                ["securitySchemes"] = new Dictionary<string, object?>
                {
                    ["bearerAuth"] = new Dictionary<string, object?>
                    {
                        ["type"] = "http",
                        ["scheme"] = "bearer",
                        ["bearerFormat"] = "JWT"
                    }
                },
                ["schemas"] = BuildSchemas(entityList)
            }
        };

        AddAuthPaths(paths);

        foreach (var meta in entityList)
        {
            var entityName = TitleCase(meta.Table);
            var rowSchema = new Dictionary<string, object?> { ["$ref"] = "#/components/schemas/" + entityName };
            var pk = meta.PrimaryKey();

            if (emitReadsAsGet) AddRestGetPaths(paths, meta, rowSchema, masks);
            if (emitReadsAsPost) AddPostQueryPaths(paths, meta, rowSchema, masks, pk);
            if (meta.Writable) AddMutationPaths(paths, meta, pk);
        }

        foreach (var cmd in commandList)
            paths[$"/api?op=command&name={cmd}"] = CommandPath(cmd);

        if (emitReadsAsPost)
        {
            var claimed = ClaimedMasks(entityList, masks);
            foreach (var (name, mask) in masks)
            {
                if (claimed.Contains(name)) continue;
                var paramProps = new Dictionary<string, object?>();
                if (mask.Params != null)
                    foreach (var (pName, pDef) in mask.Params)
                        paramProps[pName] = new Dictionary<string, object?> { ["type"] = MaskParamToJsonType(pDef.Type) };
                paths[$"/api?op=query&ref={name}"] = new Dictionary<string, object?>
                {
                    ["post"] = new Dictionary<string, object?>
                    {
                        ["operationId"] = name,
                        ["summary"] = Summary(masks, name, $"Query: {name}"),
                        ["tags"] = new List<string> { "Queries" },
                        ["security"] = BearerSecurity(),
                        ["requestBody"] = QueryBody(name, paramProps),
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = QueryResponse($"Results for {name}", new Dictionary<string, object?> { ["type"] = "object" })
                        }
                    }
                };
            }
        }

        if (emitReadsAsGet)
        {
            foreach (var (name, mask) in masks)
            {
                if (mask.Writable) continue;
                AddGenericMaskGetPath(paths, name, mask, masks);
            }
        }

        return spec;
    }

    // ── Auth paths ──────────────────────────────────────────────────────

    private void AddAuthPaths(Dictionary<string, object?> paths)
    {
        paths["/api?op=auth.token"] = new Dictionary<string, object?>
        {
            ["post"] = new Dictionary<string, object?>
            {
                ["operationId"] = "auth.token",
                ["summary"] = "Login and get JWT token pair",
                ["tags"] = new List<string> { "Auth" },
                ["requestBody"] = JsonBody(new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<string> { "op", "payload" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["op"] = EnumString("auth.token"),
                        ["payload"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["required"] = new List<string> { "email", "password" },
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["email"] = new Dictionary<string, object?> { ["type"] = "string", ["format"] = "email" },
                                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                                ["tenant_id"] = new Dictionary<string, object?> { ["type"] = "integer", ["nullable"] = true }
                            }
                        }
                    }
                }),
                ["responses"] = new Dictionary<string, object?>
                {
                    ["200"] = JsonResponse("Token pair", new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["access_token"] = new Dictionary<string, object?> { ["type"] = "string" },
                            ["token_type"] = new Dictionary<string, object?> { ["type"] = "string", ["example"] = "Bearer" },
                            ["expires_in"] = new Dictionary<string, object?> { ["type"] = "integer" },
                            ["refresh_token"] = new Dictionary<string, object?> { ["type"] = "string" },
                            ["user"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object?>
                                {
                                    ["id"] = new Dictionary<string, object?> { ["type"] = "integer" },
                                    ["email"] = new Dictionary<string, object?> { ["type"] = "string" },
                                    ["display_name"] = new Dictionary<string, object?> { ["type"] = "string" },
                                    ["role"] = new Dictionary<string, object?> { ["type"] = "string" },
                                    ["tenant_id"] = new Dictionary<string, object?> { ["type"] = "integer", ["nullable"] = true }
                                }
                            }
                        }
                    }),
                    ["400"] = ErrorResponse("Invalid credentials")
                }
            }
        };

        paths["/api?op=auth.refresh"] = new Dictionary<string, object?>
        {
            ["post"] = new Dictionary<string, object?>
            {
                ["operationId"] = "auth.refresh",
                ["summary"] = "Refresh token pair",
                ["tags"] = new List<string> { "Auth" },
                ["requestBody"] = JsonBody(new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<string> { "op", "payload" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["op"] = EnumString("auth.refresh"),
                        ["payload"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["required"] = new List<string> { "refresh_token" },
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["refresh_token"] = new Dictionary<string, object?> { ["type"] = "string" }
                            }
                        }
                    }
                }),
                ["responses"] = new Dictionary<string, object?>
                {
                    ["200"] = JsonResponse("New token pair", new Dictionary<string, object?> { ["type"] = "object" }),
                    ["400"] = ErrorResponse("Invalid or expired refresh token")
                }
            }
        };

        paths["/api?op=auth.logout"] = new Dictionary<string, object?>
        {
            ["post"] = new Dictionary<string, object?>
            {
                ["operationId"] = "auth.logout",
                ["summary"] = "Revoke a refresh token",
                ["tags"] = new List<string> { "Auth" },
                ["security"] = BearerSecurity(),
                ["requestBody"] = JsonBody(new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<string> { "op", "payload" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["op"] = EnumString("auth.logout"),
                        ["payload"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["refresh_token"] = new Dictionary<string, object?> { ["type"] = "string" }
                            }
                        }
                    }
                }),
                ["responses"] = new Dictionary<string, object?>
                {
                    ["200"] = JsonResponse("Logged out", new Dictionary<string, object?> { ["type"] = "object" })
                }
            }
        };

        paths["/api?op=auth.admin.create-user"] = new Dictionary<string, object?>
        {
            ["post"] = new Dictionary<string, object?>
            {
                ["operationId"] = "auth.admin.create-user",
                ["summary"] = "Create a user (admin only)",
                ["tags"] = new List<string> { "Auth" },
                ["security"] = BearerSecurity(),
                ["requestBody"] = JsonBody(new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<string> { "op", "payload" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["op"] = EnumString("auth.admin.create-user"),
                        ["payload"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["required"] = new List<string> { "email", "password" },
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["email"] = new Dictionary<string, object?> { ["type"] = "string", ["format"] = "email" },
                                ["password"] = new Dictionary<string, object?> { ["type"] = "string" },
                                ["role"] = new Dictionary<string, object?> { ["type"] = "string" },
                                ["tenant_id"] = new Dictionary<string, object?> { ["type"] = "integer", ["nullable"] = true }
                            }
                        }
                    }
                }),
                ["responses"] = new Dictionary<string, object?>
                {
                    ["201"] = JsonResponse("User created", new Dictionary<string, object?> { ["type"] = "object" }),
                    ["403"] = ErrorResponse("Admin role required")
                }
            }
        };

        paths["/api?op=auth.admin.change-password"] = new Dictionary<string, object?>
        {
            ["post"] = new Dictionary<string, object?>
            {
                ["operationId"] = "auth.admin.change-password",
                ["summary"] = "Reset a user's password (admin only)",
                ["tags"] = new List<string> { "Auth" },
                ["security"] = BearerSecurity(),
                ["requestBody"] = JsonBody(new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<string> { "op", "payload" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["op"] = EnumString("auth.admin.change-password"),
                        ["payload"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["required"] = new List<string> { "email", "new_password" },
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["email"] = new Dictionary<string, object?> { ["type"] = "string", ["format"] = "email" },
                                ["new_password"] = new Dictionary<string, object?> { ["type"] = "string" }
                            }
                        }
                    }
                }),
                ["responses"] = new Dictionary<string, object?>
                {
                    ["200"] = JsonResponse("Password updated", new Dictionary<string, object?> { ["type"] = "object" }),
                    ["403"] = ErrorResponse("Admin role required")
                }
            }
        };

        paths["/api?manifest"] = new Dictionary<string, object?>
        {
            ["get"] = new Dictionary<string, object?>
            {
                ["operationId"] = "manifest",
                ["summary"] = "Self-describing manifest of entities, masks, validators, and commands",
                ["tags"] = new List<string> { "Manifest" },
                ["responses"] = new Dictionary<string, object?>
                {
                    ["200"] = JsonResponse("Shape manifest", new Dictionary<string, object?> { ["$ref"] = "#/components/schemas/ShapeManifest" })
                }
            }
        };
    }

    // ── REST/OData GET surface ─────────────────────────────────────────

    private void AddRestGetPaths(
        Dictionary<string, object?> paths,
        EntityMeta meta,
        Dictionary<string, object?> rowSchema,
        Dictionary<string, MaskConfig> masks)
    {
        var table = meta.Table;
        var entityName = TitleCase(table);
        var exampleColumn = ExampleColumnFor(meta);

        if (masks.TryGetValue($"{table}.list", out var listMask))
        {
            paths[$"/api/{table}"] = new Dictionary<string, object?>
            {
                ["get"] = new Dictionary<string, object?>
                {
                    ["operationId"] = $"{table}.list.rest",
                    ["summary"] = Summary(masks, $"{table}.list", $"List {table} (REST/OData; cacheable)"),
                    ["tags"] = new List<string> { entityName },
                    ["security"] = BearerSecurity(),
                    ["parameters"] = OdataQueryParameters(exampleColumn, listMask.DefaultLimit),
                    ["responses"] = new Dictionary<string, object?>
                    {
                        ["200"] = CacheableJsonResponse($"List of {table}", new Dictionary<string, object?>
                        {
                            ["type"] = "array",
                            ["items"] = rowSchema
                        }),
                        ["304"] = new Dictionary<string, object?> { ["description"] = "Not Modified (matched If-None-Match)" },
                        ["401"] = ErrorResponse("Authentication required"),
                        ["403"] = ErrorResponse("Forbidden")
                    }
                }
            };
        }

        var pk = meta.PrimaryKey();
        if (pk != null && masks.ContainsKey($"{table}.get"))
        {
            paths[$"/api/{table}/{{{pk.Name}}}"] = new Dictionary<string, object?>
            {
                ["get"] = new Dictionary<string, object?>
                {
                    ["operationId"] = $"{table}.get.rest",
                    ["summary"] = Summary(masks, $"{table}.get", $"Get {table} by {pk.Name} (REST)"),
                    ["tags"] = new List<string> { entityName },
                    ["security"] = BearerSecurity(),
                    ["parameters"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["name"] = pk.Name,
                            ["in"] = "path",
                            ["required"] = true,
                            ["schema"] = FieldToJsonSchema(pk)
                        }
                    },
                    ["responses"] = new Dictionary<string, object?>
                    {
                        ["200"] = CacheableJsonResponse($"Single {table}", rowSchema),
                        ["304"] = new Dictionary<string, object?> { ["description"] = "Not Modified (matched If-None-Match)" },
                        ["401"] = ErrorResponse("Authentication required"),
                        ["404"] = ErrorResponse("Not found")
                    }
                }
            };
        }
    }

    private void AddPostQueryPaths(
        Dictionary<string, object?> paths,
        EntityMeta meta,
        Dictionary<string, object?> rowSchema,
        Dictionary<string, MaskConfig> masks,
        FieldMeta? pk)
    {
        var table = meta.Table;
        var entityName = TitleCase(table);

        paths[$"/api?op=query&ref={table}.list"] = new Dictionary<string, object?>
        {
            ["post"] = new Dictionary<string, object?>
            {
                ["operationId"] = $"{table}.list",
                ["summary"] = Summary(masks, $"{table}.list", $"List all {table}"),
                ["tags"] = new List<string> { entityName },
                ["security"] = BearerSecurity(),
                ["requestBody"] = QueryBody($"{table}.list", new Dictionary<string, object?>()),
                ["responses"] = new Dictionary<string, object?>
                {
                    ["200"] = QueryResponse($"List of {table}", rowSchema),
                    ["401"] = ErrorResponse("Authentication required"),
                    ["403"] = ErrorResponse("Forbidden")
                }
            }
        };

        if (pk != null)
        {
            paths[$"/api?op=query&ref={table}.get"] = new Dictionary<string, object?>
            {
                ["post"] = new Dictionary<string, object?>
                {
                    ["operationId"] = $"{table}.get",
                    ["summary"] = Summary(masks, $"{table}.get", $"Get {table} by {pk.Name}"),
                    ["tags"] = new List<string> { entityName },
                    ["security"] = BearerSecurity(),
                    ["requestBody"] = QueryBody($"{table}.get", new Dictionary<string, object?>
                    {
                        [pk.Name] = FieldToJsonSchema(pk)
                    }),
                    ["responses"] = new Dictionary<string, object?>
                    {
                        ["200"] = QueryResponse($"Single {table}", rowSchema),
                        ["401"] = ErrorResponse("Authentication required")
                    }
                }
            };
        }

        foreach (var fk in meta.FkFields())
        {
            var maskName = $"{table}.by{PascalCase(fk.Name)}";
            paths[$"/api?op=query&ref={maskName}"] = new Dictionary<string, object?>
            {
                ["post"] = new Dictionary<string, object?>
                {
                    ["operationId"] = maskName,
                    ["summary"] = Summary(masks, maskName, $"Filter {table} by {fk.Name}"),
                    ["tags"] = new List<string> { entityName },
                    ["security"] = BearerSecurity(),
                    ["requestBody"] = QueryBody(maskName, new Dictionary<string, object?>
                    {
                        [fk.Name] = FieldToJsonSchema(fk)
                    }),
                    ["responses"] = new Dictionary<string, object?>
                    {
                        ["200"] = QueryResponse($"{entityName} filtered by {fk.Name}", rowSchema),
                        ["401"] = ErrorResponse("Authentication required")
                    }
                }
            };
        }
    }

    private void AddMutationPaths(
        Dictionary<string, object?> paths,
        EntityMeta meta,
        FieldMeta? pk)
    {
        var table = meta.Table;
        var entityName = TitleCase(table);

        var insertProps = new Dictionary<string, object?>();
        var insertRequired = new List<string>();
        var updateProps = new Dictionary<string, object?>();

        foreach (var field in meta.Fields)
        {
            // Server-assigned ids never appear in insert/update payload schemas —
            // identity columns and manual-strategy PKs alike (§2.4 isGenerated).
            if (field.IsAutoIncrement || (field.IsPrimary && meta.PkStrategy == "manual")) continue;

            // The tenant discriminator is stamped from the caller's session for
            // scoped roles, but an unscoped (superadmin) caller MUST supply it to
            // target a tenant on insert — so it appears in the insert body as an
            // optional field. It is never updatable (a row cannot move tenants).
            if (field.IsTenantColumn)
            {
                var tenantSchema = FieldToJsonSchema(field);
                tenantSchema["description"] = "Tenant id. Omit as a tenant-scoped user (stamped from your session); required for unscoped (cross-tenant) roles.";
                insertProps[field.Name] = tenantSchema;
                continue;
            }

            // Row-scope dimension column (ROW-SCOPES-PLAN §2.5): stamped from the
            // caller's claim on insert for single-valued scopes (so it's optional),
            // required when the scope is multi-valued (the caller picks a
            // membership); never updatable (a scoped move is rejected).
            if (field.IsScopeColumn)
            {
                var scopeSchema = FieldToJsonSchema(field);
                scopeSchema["description"] = $"Row scope. Stamped from the caller's {field.Name} claim when omitted (single-valued scopes); for multi-valued scopes, supply one of the caller's {field.Name} memberships. Never updatable.";
                insertProps[field.Name] = scopeSchema;
                continue;
            }

            var schema = FieldToJsonSchema(field);
            if (field.Validators.Count > 0)
                schema["x-validators"] = new List<string>(field.Validators);
            insertProps[field.Name] = schema;
            updateProps[field.Name] = schema;
            if (!field.Nullable && !field.HasDefault && !field.IsPrimary)
                insertRequired.Add(field.Name);
        }

        paths[$"/api?op=mutation&action=insert&table={table}"] = new Dictionary<string, object?>
        {
            ["post"] = new Dictionary<string, object?>
            {
                ["operationId"] = $"{table}.insert",
                ["summary"] = $"Insert new {table}",
                ["tags"] = new List<string> { entityName },
                ["security"] = BearerSecurity(),
                ["requestBody"] = MutationBody("insert", table, insertProps, insertRequired, null),
                ["responses"] = new Dictionary<string, object?>
                {
                    ["200"] = MutationResponse("Row inserted"),
                    ["400"] = ErrorResponse("Validation error"),
                    ["401"] = ErrorResponse("Authentication required"),
                    ["403"] = ErrorResponse("Not writable")
                }
            }
        };

        if (pk != null)
        {
            var pkFilter = new Dictionary<string, object?> { [pk.Name] = FieldToJsonSchema(pk) };

            paths[$"/api?op=mutation&action=update&table={table}"] = new Dictionary<string, object?>
            {
                ["post"] = new Dictionary<string, object?>
                {
                    ["operationId"] = $"{table}.update",
                    ["summary"] = $"Update {table}",
                    ["tags"] = new List<string> { entityName },
                    ["security"] = BearerSecurity(),
                    ["requestBody"] = MutationBody("update", table, updateProps, new List<string>(), pkFilter),
                    ["responses"] = new Dictionary<string, object?>
                    {
                        ["200"] = MutationResponse("Row updated"),
                        ["400"] = ErrorResponse("Validation error")
                    }
                }
            };

            paths[$"/api?op=mutation&action=delete&table={table}"] = new Dictionary<string, object?>
            {
                ["post"] = new Dictionary<string, object?>
                {
                    ["operationId"] = $"{table}.delete",
                    ["summary"] = $"Delete {table}",
                    ["tags"] = new List<string> { entityName },
                    ["security"] = BearerSecurity(),
                    ["requestBody"] = MutationBody("delete", table, new Dictionary<string, object?>(), new List<string>(), pkFilter),
                    ["responses"] = new Dictionary<string, object?>
                    {
                        ["200"] = MutationResponse("Row deleted")
                    }
                }
            };
        }
    }

    private Dictionary<string, object?> CommandPath(string cmd) => new()
    {
        ["post"] = new Dictionary<string, object?>
        {
            ["operationId"] = $"command.{cmd}",
            ["summary"] = $"Execute command: {cmd}",
            ["tags"] = new List<string> { "Commands" },
            ["security"] = BearerSecurity(),
            ["requestBody"] = JsonBody(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["required"] = new List<string> { "op", "payload" },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["op"] = EnumString("command"),
                    ["payload"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["required"] = new List<string> { "name" },
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["name"] = EnumString(cmd),
                            ["params"] = new Dictionary<string, object?> { ["type"] = "object" }
                        }
                    }
                }
            }),
            ["responses"] = new Dictionary<string, object?>
            {
                ["200"] = JsonResponse("Command result", new Dictionary<string, object?> { ["type"] = "object" }),
                ["400"] = ErrorResponse("Command validation error"),
                ["404"] = ErrorResponse("Command not found")
            }
        }
    };

    private void AddGenericMaskGetPath(
        Dictionary<string, object?> paths,
        string maskName,
        MaskConfig mask,
        Dictionary<string, MaskConfig> masks)
    {
        var parameters = new List<object?>();
        if (mask.Params != null)
        {
            foreach (var (pName, pDef) in mask.Params)
            {
                parameters.Add(new Dictionary<string, object?>
                {
                    ["name"] = pName,
                    ["in"] = "query",
                    ["required"] = pDef.Required,
                    ["schema"] = new Dictionary<string, object?> { ["type"] = MaskParamToJsonType(pDef.Type) }
                });
            }
        }

        paths[$"/api/_/{maskName}"] = new Dictionary<string, object?>
        {
            ["get"] = new Dictionary<string, object?>
            {
                ["operationId"] = $"mask.{maskName}.get",
                ["summary"] = Summary(masks, maskName, $"GET via generic mask: {maskName}"),
                ["tags"] = new List<string> { "Generic mask GET" },
                ["security"] = BearerSecurity(),
                ["parameters"] = parameters,
                ["responses"] = new Dictionary<string, object?>
                {
                    ["200"] = CacheableJsonResponse(
                        $"Result rows for mask {maskName}",
                        new Dictionary<string, object?> { ["type"] = "object" }),
                    ["304"] = new Dictionary<string, object?> { ["description"] = "Not Modified (matched If-None-Match)" },
                    ["401"] = ErrorResponse("Authentication required"),
                    ["403"] = ErrorResponse("Forbidden"),
                    ["404"] = ErrorResponse("Mask not found")
                }
            }
        };
    }

    // ── OData query params + cacheable response ────────────────────────

    private static List<object?> OdataQueryParameters(string? exampleColumn, int? defaultLimit)
    {
        var orderbyExample = exampleColumn != null ? $"{exampleColumn} desc" : "created_at desc";
        var filterExample = exampleColumn != null ? $"{exampleColumn} eq 'value'" : "name eq 'Acme'";
        var selectExample = exampleColumn != null ? $"id,{exampleColumn}" : "id,name";

        var topSchema = new Dictionary<string, object?> { ["type"] = "integer", ["maximum"] = 200 };
        if (defaultLimit is > 0) topSchema["default"] = defaultLimit.Value;

        return new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["name"] = "$top", ["in"] = "query",
                ["description"] = "Maximum rows to return. Capped at 200 (MAX_TOP).",
                ["schema"] = topSchema
            },
            new Dictionary<string, object?>
            {
                ["name"] = "$skip", ["in"] = "query",
                ["description"] = "Rows to skip before returning the page.",
                ["schema"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 0 }
            },
            new Dictionary<string, object?>
            {
                ["name"] = "$orderby", ["in"] = "query",
                ["description"] = "Column and direction, e.g. `created_at desc`.",
                ["schema"] = new Dictionary<string, object?> { ["type"] = "string", ["example"] = orderbyExample }
            },
            new Dictionary<string, object?>
            {
                ["name"] = "$filter", ["in"] = "query",
                ["description"] = $"OData-style filter, e.g. `{filterExample}`.",
                ["schema"] = new Dictionary<string, object?> { ["type"] = "string", ["example"] = filterExample }
            },
            new Dictionary<string, object?>
            {
                ["name"] = "$select", ["in"] = "query",
                ["description"] = "Comma-separated subset of columns.",
                ["schema"] = new Dictionary<string, object?> { ["type"] = "string", ["example"] = selectExample }
            },
            new Dictionary<string, object?>
            {
                ["name"] = "$count", ["in"] = "query",
                ["description"] = "When true, response includes `total_count` (separate COUNT query).",
                ["schema"] = new Dictionary<string, object?> { ["type"] = "boolean" }
            }
        };
    }

    private static string? ExampleColumnFor(EntityMeta meta)
    {
        string? firstNonPk = null;
        foreach (var field in meta.Fields)
        {
            if (field.IsPrimary) continue;
            firstNonPk ??= field.Name;
            if (field.IsFk) continue;
            if (field.ParamType() == "string")
                return field.Name;
        }
        return firstNonPk;
    }

    private static Dictionary<string, object?> CacheableJsonResponse(string description, Dictionary<string, object?> schema) => new()
    {
        ["description"] = description,
        ["headers"] = new Dictionary<string, object?>
        {
            ["ETag"] = new Dictionary<string, object?>
            {
                ["description"] = "Strong validator. Send back as `If-None-Match` to get 304.",
                ["schema"] = new Dictionary<string, object?> { ["type"] = "string" }
            },
            ["Cache-Control"] = new Dictionary<string, object?>
            {
                ["description"] = "Per-mask cache directive (set via mask config `CacheControl`).",
                ["schema"] = new Dictionary<string, object?> { ["type"] = "string" }
            }
        },
        ["content"] = new Dictionary<string, object?>
        {
            ["application/json"] = new Dictionary<string, object?> { ["schema"] = schema }
        }
    };

    // ── Tags + x-tagGroups ─────────────────────────────────────────────

    private static (List<object?> Tags, List<object?> Groups) BuildTagsAndGroups(
        List<EntityMeta> entities,
        Dictionary<string, MaskConfig> masks,
        List<string> commands)
    {
        var entityTags = new List<Dictionary<string, object?>>();
        var entityTagNames = new List<string>();

        foreach (var meta in entities)
        {
            var tagName = TitleCase(meta.Table);
            entityTagNames.Add(tagName);

            var hints = new List<string> { $"{meta.Fields.Count} column" + (meta.Fields.Count == 1 ? "" : "s") };
            if (!string.IsNullOrEmpty(meta.SoftDeleteColumn)) hints.Add("soft-delete");
            var mode = meta.EffectiveWritableMode();
            hints.Add(mode switch
            {
                "full" => "writable",
                "append_only" => "append-only",
                "no_delete" => "no-delete",
                _ => "read-only"
            });
            if (meta.AuditColumns.Count > 0) hints.Add("audit columns");

            // Lead with operator-supplied / DB-comment description when present.
            // Otherwise fall back to the mechanical hint summary.
            var description = !string.IsNullOrEmpty(meta.Description)
                ? $"{meta.Description} ({string.Join(", ", hints)})"
                : $"CRUD + masks for `{meta.Table}` ({string.Join(", ", hints)}).";

            entityTags.Add(new Dictionary<string, object?>
            {
                ["name"] = tagName,
                ["description"] = description
            });
        }

        entityTagNames.Sort(StringComparer.Ordinal);
        entityTags.Sort((a, b) => StringComparer.Ordinal.Compare(a["name"]?.ToString(), b["name"]?.ToString()));

        var systemTags = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Auth", ["description"] = "Login, refresh, logout — local users-table JWT pathway." },
            new() { ["name"] = "Manifest", ["description"] = "Self-describing manifest of entities, masks, validators, commands." }
        };
        var systemTagNames = new List<string> { "Auth", "Manifest" };

        var hasStandaloneReads = false;
        foreach (var (name, mask) in masks)
        {
            if (mask.Writable) continue;
            var owned = entities.Any(m =>
                name.StartsWith(m.Table + ".", StringComparison.Ordinal) || name == m.Table);
            if (!owned) { hasStandaloneReads = true; break; }
        }
        if (hasStandaloneReads)
        {
            systemTags.Add(new Dictionary<string, object?>
            {
                ["name"] = "Queries",
                ["description"] = "Hand-written read masks not owned by a specific entity."
            });
            systemTagNames.Add("Queries");
        }

        var hasReadableMasks = masks.Values.Any(m => !m.Writable);
        if (hasReadableMasks)
        {
            systemTags.Add(new Dictionary<string, object?>
            {
                ["name"] = "Generic mask GET",
                ["description"] = "Universal cacheable read path: GET /api/_/<mask>?<params>. Reaches every read mask, including admin masks for secret tables."
            });
            systemTagNames.Add("Generic mask GET");
        }

        if (commands.Count > 0)
        {
            systemTags.Add(new Dictionary<string, object?>
            {
                ["name"] = "Commands",
                ["description"] = "Business-logic operations beyond CRUD."
            });
            systemTagNames.Add("Commands");
        }

        var allTags = new List<object?>();
        foreach (var t in entityTags) allTags.Add(t);
        foreach (var t in systemTags) allTags.Add(t);

        var tagGroups = new List<object?>();
        if (entityTagNames.Count > 0)
            tagGroups.Add(new Dictionary<string, object?> { ["name"] = "Entities", ["tags"] = entityTagNames });
        if (systemTagNames.Count > 0)
            tagGroups.Add(new Dictionary<string, object?> { ["name"] = "System", ["tags"] = systemTagNames });

        return (allTags, tagGroups);
    }

    // ── Schemas ────────────────────────────────────────────────────────

    private Dictionary<string, object?> BuildSchemas(IEnumerable<EntityMeta> entities)
    {
        var schemas = new Dictionary<string, object?>();

        foreach (var meta in entities)
        {
            var name = TitleCase(meta.Table);
            var props = new Dictionary<string, object?>();
            var required = new List<string>();

            foreach (var field in meta.Fields)
            {
                var schema = FieldToJsonSchema(field);
                if (field.IsPrimary || field.IsTenantColumn || field.IsScopeColumn) schema["readOnly"] = true;
                if (field.Validators.Count > 0)
                    schema["x-validators"] = new List<string>(field.Validators);
                props[field.Name] = schema;
                if (!field.Nullable && !field.HasDefault)
                    required.Add(field.Name);
            }

            var s = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = props
            };
            if (required.Count > 0) s["required"] = required;
            schemas[name] = s;
        }

        schemas["Error"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["description"] = "Self-documenting error envelope. `expected` and `example` describe the corrected request shape; `hint` is a one-line suggestion. `ref` echoes the request's batch ref when present.",
            ["properties"] = new Dictionary<string, object?>
            {
                ["error"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<string> { "code", "message" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["code"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["message"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["ref"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["expected"] = new Dictionary<string, object?> { ["type"] = "object" },
                        ["example"] = new Dictionary<string, object?> { ["type"] = "object" },
                        ["hint"] = new Dictionary<string, object?> { ["type"] = "string" }
                    }
                }
            }
        };

        schemas["MutationResult"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["results"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["ref"] = new Dictionary<string, object?> { ["type"] = "string", ["nullable"] = true },
                            ["action"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["enum"] = new List<string> { "insert", "update", "delete" }
                            },
                            ["table"] = new Dictionary<string, object?> { ["type"] = "string" },
                            ["affected"] = new Dictionary<string, object?> { ["type"] = "integer" },
                            ["id"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer",
                                ["description"] = "Auto-generated PK on insert."
                            }
                        }
                    }
                }
            }
        };

        schemas["ShapeManifest"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["entities"] = new Dictionary<string, object?> { ["type"] = "array" },
                ["commands"] = new Dictionary<string, object?> { ["type"] = "array" },
                ["masks"] = new Dictionary<string, object?> { ["type"] = "object" },
                ["uploads"] = new Dictionary<string, object?> { ["type"] = "object" }
            }
        };

        return schemas;
    }

    private static Dictionary<string, object?> FieldToJsonSchema(FieldMeta field)
    {
        var schema = new Dictionary<string, object?>();
        var paramType = field.ParamType();

        switch (paramType)
        {
            case "int": schema["type"] = "integer"; break;
            case "float": schema["type"] = "number"; break;
            case "bool": schema["type"] = "boolean"; break;
            default: schema["type"] = "string"; break;
        }

        if (field.Nullable) schema["nullable"] = true;
        if (field.HasDefault) schema["default"] = field.DefaultValue;
        if (field.Length.HasValue && paramType == "string")
            schema["maxLength"] = field.Length.Value;

        var dbType = (field.DbType ?? "").ToLowerInvariant();
        if (dbType == "date") schema["format"] = "date";
        else if (dbType == "datetime" || dbType == "timestamp") schema["format"] = "date-time";

        if (field.Validators.Contains("email"))
            schema["format"] = "email";

        if (field.EnumValues is { Count: > 0 } values)
            schema["enum"] = new List<string>(values);

        if (field.IsFk)
            schema["x-foreignKey"] = field.FkEntityType?.Name;

        if (field.IsTenantColumn || field.IsScopeColumn)
            schema["readOnly"] = true;

        if (!string.IsNullOrEmpty(field.Description))
            schema["description"] = field.Description;

        return schema;
    }

    // ── Body / response helpers ────────────────────────────────────────

    private Dictionary<string, object?> QueryBody(string maskName, Dictionary<string, object?> paramSchemas)
    {
        var paramsSchema = paramSchemas.Count > 0
            ? new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["required"] = paramSchemas.Keys.ToList(),
                ["properties"] = paramSchemas
            }
            : new Dictionary<string, object?> { ["type"] = "object" };

        return JsonBody(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["required"] = new List<string> { "op", "payload" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["op"] = EnumString("query"),
                ["payload"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<string> { "queries" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["queries"] = new Dictionary<string, object?>
                        {
                            ["type"] = "array",
                            ["items"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["required"] = new List<string> { "ref", "sql" },
                                ["properties"] = new Dictionary<string, object?>
                                {
                                    ["ref"] = new Dictionary<string, object?> { ["type"] = "string", ["example"] = maskName },
                                    ["sql"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new List<string> { maskName } },
                                    ["params"] = paramsSchema,
                                    ["options"] = QueryOptionsSchema()
                                }
                            }
                        }
                    }
                }
            }
        });
    }

    private static Dictionary<string, object?> QueryOptionsSchema() => new()
    {
        ["type"] = "object",
        ["description"] = "Optional refinements applied after the mask runs.",
        ["properties"] = new Dictionary<string, object?>
        {
            ["orderby"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] = "List of {field, dir} entries.",
                ["items"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<string> { "field" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["field"] = new Dictionary<string, object?> { ["type"] = "string", ["example"] = "created_at" },
                        ["dir"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["enum"] = new List<string> { "asc", "desc" },
                            ["example"] = "desc",
                            ["default"] = "asc"
                        }
                    }
                }
            },
            ["top"] = new Dictionary<string, object?> { ["type"] = "integer", ["maximum"] = 200, ["example"] = 50 },
            ["skip"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 0, ["example"] = 0 },
            ["count"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "When true, response includes the unbounded `total_count`.",
                ["example"] = false
            }
        }
    };

    private Dictionary<string, object?> QueryResponse(string description, Dictionary<string, object?> rowSchema)
    {
        return JsonResponse(description, new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["results"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["ref"] = new Dictionary<string, object?> { ["type"] = "string" },
                            ["meta"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object?>
                                {
                                    ["columns"] = new Dictionary<string, object?>
                                    {
                                        ["type"] = "array",
                                        ["items"] = new Dictionary<string, object?> { ["type"] = "string" }
                                    },
                                    ["ms"] = new Dictionary<string, object?> { ["type"] = "number" },
                                    ["rows"] = new Dictionary<string, object?> { ["type"] = "integer" }
                                }
                            },
                            ["rows"] = new Dictionary<string, object?>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object?> { ["type"] = "array" }
                            },
                            ["count"] = new Dictionary<string, object?> { ["type"] = "integer" },
                            ["total_count"] = new Dictionary<string, object?> { ["type"] = "integer" }
                        }
                    }
                }
            }
        });
    }

    private Dictionary<string, object?> MutationBody(
        string action,
        string table,
        Dictionary<string, object?> dataProps,
        List<string> dataRequired,
        Dictionary<string, object?>? filterProps)
    {
        var mutation = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["required"] = new List<string> { "table", "action" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["table"] = EnumString(table),
                ["action"] = EnumString(action),
                ["ref"] = new Dictionary<string, object?> { ["type"] = "string" }
            }
        };

        var props = (Dictionary<string, object?>)mutation["properties"]!;

        if (dataProps.Count > 0)
        {
            var dataSchema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = dataProps
            };
            if (dataRequired.Count > 0) dataSchema["required"] = dataRequired;
            props["data"] = dataSchema;
        }
        if (filterProps is { Count: > 0 })
        {
            props["filter"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = filterProps
            };
        }

        return JsonBody(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["required"] = new List<string> { "op", "payload" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["op"] = EnumString("mutation"),
                ["payload"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<string> { "mutations" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["mutations"] = new Dictionary<string, object?>
                        {
                            ["type"] = "array",
                            ["items"] = mutation
                        },
                        ["transaction"] = new Dictionary<string, object?> { ["type"] = "boolean", ["default"] = false }
                    }
                }
            }
        });
    }

    private Dictionary<string, object?> MutationResponse(string description) =>
        JsonResponse(description, new Dictionary<string, object?> { ["$ref"] = "#/components/schemas/MutationResult" });

    private static Dictionary<string, object?> JsonBody(Dictionary<string, object?> schema) => new()
    {
        ["required"] = true,
        ["content"] = new Dictionary<string, object?>
        {
            ["application/json"] = new Dictionary<string, object?> { ["schema"] = schema }
        }
    };

    private static Dictionary<string, object?> JsonResponse(string description, Dictionary<string, object?> schema) => new()
    {
        ["description"] = description,
        ["content"] = new Dictionary<string, object?>
        {
            ["application/json"] = new Dictionary<string, object?> { ["schema"] = schema }
        }
    };

    private static Dictionary<string, object?> ErrorResponse(string description) =>
        JsonResponse(description, new Dictionary<string, object?> { ["$ref"] = "#/components/schemas/Error" });

    private static Dictionary<string, object?> EnumString(string singleValue) => new()
    {
        ["type"] = "string",
        ["enum"] = new List<string> { singleValue }
    };

    private static List<object?> BearerSecurity() => new()
    {
        new Dictionary<string, object?> { ["bearerAuth"] = new List<object?>() }
    };

    // ── Misc helpers ───────────────────────────────────────────────────

    private static string MaskParamToJsonType(string shapeType) => shapeType.ToLowerInvariant() switch
    {
        "int" or "integer" or "array_int" => "integer",
        "float" or "double" or "decimal" or "number" => "number",
        "bool" or "boolean" => "boolean",
        _ => "string"
    };

    private static HashSet<string> ClaimedMasks(List<EntityMeta> entities, Dictionary<string, MaskConfig> masks)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var meta in entities)
            foreach (var name in masks.Keys)
                if (name.StartsWith(meta.Table + ".", StringComparison.Ordinal) || name == meta.Table)
                    claimed.Add(name);
        return claimed;
    }

    private static string Summary(Dictionary<string, MaskConfig> masks, string maskName, string fallback)
    {
        if (!masks.TryGetValue(maskName, out var m))
            return fallback;

        var description = !string.IsNullOrEmpty(m.Description) ? m.Description : fallback;
        if (m.TenantScope is string tenantColumn && tenantColumn.Length > 0 && !m.Writable
            && !description.Contains("caller's tenant", StringComparison.OrdinalIgnoreCase))
        {
            description += " Rows are automatically limited to the caller's tenant.";
        }
        // One sentence per row-scope dimension (ROW-SCOPES-PLAN §2.5).
        if (m.RowScopes is Dictionary<string, RowScopeMeta> { Count: > 0 } rowScopes && !m.Writable)
        {
            foreach (var column in rowScopes.Keys)
            {
                if (!description.Contains($"caller's {column}", StringComparison.OrdinalIgnoreCase))
                    description += $" Rows are automatically limited to the caller's {column}.";
            }
        }
        return description;
    }

    private static string TitleCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string PascalCase(string value) => string.Concat(value
        .Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
