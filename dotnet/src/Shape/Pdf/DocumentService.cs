using System.Text.RegularExpressions;
using Shape.Core;
using Shape.Upload;

namespace Shape.Pdf;

/// <summary>
/// PDF documents from masks. The data comes from masks run in the CALLER's context, so row scopes, tenancy and mask
/// roles decide what a document may contain — a user can only print what they could read. A document the caller may
/// not see answers 404, exactly like a missing one. Configured under <c>ShapeConfig.Documents["name"]</c> with the same
/// spec keys as the PHP <c>Shape\Pdf\DocumentService</c> (<c>mask</c>, <c>params</c>, <c>roles</c>, <c>filename</c>,
/// <c>title</c>, <c>locale</c>, <c>footer</c>, <c>blocks</c>). Route: <c>GET /pdf/&lt;name&gt;?id=5</c>.
/// <para>
/// Archiving: a document with an <c>archive</c> block (<c>upload</c> = an upload entry with attachments,
/// <c>fields</c> = attachment fields with <c>{placeholders}</c> from the document data) can also be generated AND
/// stored as a private attachment through <c>POST /pdf/&lt;name&gt;?id=5</c> (immutable invoice copy). Every call
/// stores a new copy; a UNIQUE index on the attachment table's parent column makes "archive once" a database
/// guarantee. The caller needs both the document and the upload entry.
/// </para>
/// </summary>
public sealed class DocumentService
{
    public const int MaxTableRows = 2000;

    private readonly BatchExecutor _executor;
    private readonly Dictionary<string, Dictionary<string, object?>> _documents;
    private readonly DocumentRenderer? _renderer;

    public sealed record Document(byte[] Pdf, string Filename, Dictionary<string, object?> Data);

    public DocumentService(BatchExecutor executor, Dictionary<string, Dictionary<string, object?>>? documents, DocumentRenderer? renderer = null)
    {
        _executor = executor;
        _documents = documents ?? new();
        _renderer = renderer;
    }

    public IReadOnlyList<string> Names() => _documents.Keys.ToList();

    public Document Render(string name, IReadOnlyDictionary<string, string?> query, Dictionary<string, object?> authContext,
        string? created = null, bool compress = true)
    {
        if (!_documents.TryGetValue(name, out var spec))
        {
            throw new ApiException("NOT_FOUND", $"Document '{name}' is not configured.", 404, null, new Dictionary<string, object?>
            {
                ["expected"] = _documents.Count == 0 ? "No documents are configured for this app." : "one of: " + string.Join(", ", _documents.Keys),
                ["hint"] = "Documents are declared under ShapeConfig.Documents[\"<name>\"]."
            });
        }
        AuthorizeRole(spec, authContext);

        var declared = Strings(spec.GetValueOrDefault("params"));
        var parameters = new Dictionary<string, object?>();
        foreach (var p in declared)
        {
            if (!query.TryGetValue(p, out var v) || string.IsNullOrEmpty(v))
            {
                throw new ApiException("DOCUMENT_VALIDATION_ERROR", $"Query parameter '{p}' is required.", 400, null, new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?> { ["query"] = string.Join(", ", declared) },
                    ["hint"] = $"Call GET /pdf/{name}?" + string.Join("&", declared.Select(x => $"{x}=<value>"))
                });
            }
            parameters[p] = v;
        }

        var mask = Convert.ToString(spec.GetValueOrDefault("mask")) ?? "";
        if (mask == "") throw new ApiException("DOCUMENT_CONFIG_ERROR", $"Document '{name}' needs a 'mask'.", 500);
        var headerRows = Run(mask, parameters, authContext);
        if (headerRows.Count == 0)
            throw new ApiException("NOT_FOUND", "Document not found.", 404);   // missing or not visible: same answer

        var data = new Dictionary<string, object?>(headerRows[0]);
        foreach (var (k, v) in parameters) data.TryAdd(k, v);

        var tableRows = new Dictionary<int, List<Dictionary<string, object?>>>();
        var blocks = AsList(spec.GetValueOrDefault("blocks"));
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is not Dictionary<string, object?> block || Convert.ToString(block.GetValueOrDefault("type")) != "table")
                continue;
            if (!block.ContainsKey("mask"))
            {
                tableRows[i] = AsList(block.GetValueOrDefault("rows")).OfType<Dictionary<string, object?>>().ToList();
                continue;
            }
            var bp = new Dictionary<string, object?>();
            if (block.GetValueOrDefault("params") is Dictionary<string, object?> pm)
                foreach (var (k, v) in pm)
                {
                    var m = v is string sv ? Regex.Match(sv, @"^\{([A-Za-z_][A-Za-z0-9_]*)\}$") : null;
                    bp[k] = m is { Success: true } ? data.GetValueOrDefault(m.Groups[1].Value) : v;
                }
            var rows = Run(Convert.ToString(block["mask"])!, bp, authContext);
            if (rows.Count > MaxTableRows)
                throw new ApiException("DOCUMENT_TOO_LARGE", $"The table has more than {MaxTableRows} rows.", 413, null,
                    new Dictionary<string, object?> { ["hint"] = "Narrow the mask (period, status) so the document stays printable." });
            tableRows[i] = rows;
        }

        var renderer = _renderer ?? new DocumentRenderer();
        byte[] pdf;
        try
        {
            pdf = renderer.Render(spec, data, tableRows, created ?? DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "Z", compress);
        }
        catch (ArgumentException e)
        {
            throw new ApiException("DOCUMENT_CONFIG_ERROR", e.Message, 500);
        }

        var file = renderer.Fill(Convert.ToString(spec.GetValueOrDefault("filename")) ?? name + ".pdf", data);
        file = Regex.Replace(file, "[^A-Za-z0-9._-]+", "_").Trim('_');
        if (file.Length == 0 || !file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            file = (file.Length == 0 ? name : file) + ".pdf";
        return new Document(pdf, file, data);
    }

    /// <summary>Render the document and store it as a private attachment (see the class comment).</summary>
    public async Task<Dictionary<string, object?>> ArchiveAsync(
        string name, IReadOnlyDictionary<string, string?> query, Dictionary<string, object?> authContext,
        AttachmentService attachments, string? created = null)
    {
        var archive = _documents.TryGetValue(name, out var spec) ? spec.GetValueOrDefault("archive") as Dictionary<string, object?> : null;
        var upload = archive == null ? "" : Convert.ToString(archive.GetValueOrDefault("upload")) ?? "";
        if (archive == null || upload.Length == 0)
        {
            throw new ApiException("NOT_FOUND", $"Document '{name}' cannot be archived.", 404, null, new Dictionary<string, object?>
            {
                ["hint"] = "Add \"archive\": { \"upload\": \"<upload with attachments>\", \"fields\": { \"<col>\": \"{id}\" } } to the document."
            });
        }
        var doc = Render(name, query, authContext, created);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (archive.GetValueOrDefault("fields") is Dictionary<string, object?> fm)
            foreach (var (k, v) in fm)
            {
                var m = v is string sv ? Regex.Match(sv, @"^\{([A-Za-z_][A-Za-z0-9_]*)\}$") : null;
                fields[k] = m is { Success: true } ? doc.Data.GetValueOrDefault(m.Groups[1].Value) : v;
            }
        return await attachments.StoreGeneratedAsync(upload, doc.Pdf, doc.Filename, fields, authContext);
    }

    private List<Dictionary<string, object?>> Run(string mask, Dictionary<string, object?> parameters, Dictionary<string, object?> authContext)
    {
        var outcome = _executor.Execute(
            new List<Dictionary<string, object?>>
            {
                new() { ["ref"] = "doc", ["sql"] = mask, ["params"] = new Dictionary<string, object?>(parameters) }
            },
            authContext);
        var first = ((List<Dictionary<string, object?>>)outcome["results"]!)[0];
        var cols = (List<string>)((Dictionary<string, object?>)first["meta"]!)["columns"]!;
        var rows = new List<Dictionary<string, object?>>();
        foreach (var r in (List<object?[]>)first["rows"]!)
        {
            var o = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < cols.Count; i++) o[cols[i]] = r[i];
            rows.Add(o);
        }
        return rows;
    }

    private static void AuthorizeRole(Dictionary<string, object?> spec, Dictionary<string, object?> authContext)
    {
        var allowed = Strings(spec.GetValueOrDefault("roles"));
        if (allowed.Count == 0) return;
        var role = authContext.GetValueOrDefault("role")?.ToString() ?? "";
        if (role.Length == 0 || !allowed.Contains(role))
        {
            throw new ApiException("AUTH_FORBIDDEN", "Current role is not allowed for this document.", 403, null, new Dictionary<string, object?>
            {
                ["expected"] = new Dictionary<string, object?> { ["role"] = "one of: " + string.Join(", ", allowed) },
                ["hint"] = role.Length == 0 ? "Authenticate first via POST {op:'auth.token'}." : $"Your role '{role}' cannot print this document."
            });
        }
    }

    private static List<object?> AsList(object? o) =>
        o is System.Collections.IEnumerable e and not string ? e.Cast<object?>().ToList() : new List<object?>();

    private static List<string> Strings(object? o) => AsList(o).Select(x => Convert.ToString(x) ?? "").ToList();
}
