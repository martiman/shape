using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Shape.Core;

namespace Shape.Upload;

/// <summary>
/// Private attachments: files linked to a row of an ordinary table, readable only
/// through Shape so the table's row scopes and roles decide who sees them (a user
/// who cannot read the case cannot read its documents). Mirror of the PHP
/// <c>Shape\Upload\AttachmentService</c>.
///
/// Every database step goes through <see cref="BatchExecutor"/>, so scopes, tenancy,
/// access roles, validators and the audit log apply exactly as for a normal mutation.
///
/// Routes (wired by <see cref="ShapeRouter"/>):
/// <c>POST /upload/&lt;name&gt;</c> (multipart: file + declared fields),
/// <c>GET /files/&lt;name&gt;/&lt;id&gt;</c> (stream), <c>DELETE /files/&lt;name&gt;/&lt;id&gt;</c>.
/// </summary>
public sealed class AttachmentService
{
    private static readonly Regex Ident = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly UploadManager _uploads;
    private readonly BatchExecutor _executor;

    public AttachmentService(UploadManager uploads, BatchExecutor executor)
    {
        _uploads = uploads;
        _executor = executor;
    }

    /// <summary>An opened attachment ready to be streamed. The caller disposes <see cref="Stream"/>.</summary>
    public sealed record AttachmentFile(Stream Stream, string Filename, string MimeType, long Size, string Sha256);

    /// <summary>Whether the upload entry stores private attachments.</summary>
    public bool IsAttachmentEntry(string name) => _uploads.Entry(name)?.Attachments != null;

    public async Task<Dictionary<string, object?>> UploadAsync(
        string name,
        IFormFileCollection files,
        IReadOnlyDictionary<string, string> form,
        Dictionary<string, object?> authContext)
    {
        var cfg = Config(name);
        _uploads.Authorize(name, authContext);

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in cfg.Fields)
        {
            if (!form.TryGetValue(field, out var v)) continue;
            data[field] = long.TryParse(v, out var n) && v.All(char.IsDigit) ? n : v;
        }

        AssertParents(cfg, data, authContext);
        return await Persist(name, cfg, data, () => _uploads.HandleAsync(name, files, authContext), authContext);
    }

    /// <summary>
    /// Store generated content (a rendered PDF) as an attachment of the given upload entry, with the same
    /// authorisation as an upload: role gate, parent visibility, row scopes, validators and audit.
    /// </summary>
    public async Task<Dictionary<string, object?>> StoreGeneratedAsync(
        string name,
        byte[] bytes,
        string filename,
        IReadOnlyDictionary<string, object?> fields,
        Dictionary<string, object?> authContext)
    {
        var cfg = Config(name);
        _uploads.Authorize(name, authContext);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in cfg.Fields)
            if (fields.TryGetValue(field, out var v)) data[field] = v;
        AssertParents(cfg, data, authContext);
        return await Persist(name, cfg, data,
            () => Task.FromResult(_uploads.StoreBytes(name, bytes, filename, authContext)), authContext);
    }

    private void AssertParents(Resolved cfg, Dictionary<string, object?> data, Dictionary<string, object?> authContext)
    {
        // Authorise the parent BEFORE storing anything.
        foreach (var (field, table) in cfg.Parents)
        {
            if (!data.TryGetValue(field, out var parentId) || parentId is null)
            {
                throw new ApiException(
                    "UPLOAD_VALIDATION_ERROR",
                    $"Form field '{field}' is required.",
                    400,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = new Dictionary<string, object?> { ["form"] = field },
                        ["hint"] = $"Send '{field}' next to the file in the multipart body."
                    });
            }
            AssertParentVisible(table, parentId, authContext);
        }
    }

    private async Task<Dictionary<string, object?>> Persist(
        string name,
        Resolved cfg,
        Dictionary<string, object?> data,
        Func<Task<Dictionary<string, object?>>> store,
        Dictionary<string, object?> authContext)
    {
        var stored = await store();
        var storage = _uploads.StorageFor(name);
        var key = (string)stored["id"]!;

        var row = new Dictionary<string, object?>(data, StringComparer.Ordinal)
        {
            ["file_key"] = key,
            ["filename"] = CleanFilename((string)stored["filename"]!),
            ["mime_type"] = stored["mime_type"],
            ["size_bytes"] = stored["size"],
            ["sha256"] = stored["sha256"]
        };

        Dictionary<string, object?> res;
        try
        {
            var outcome = _executor.ExecuteMutations(
                new List<Dictionary<string, object?>>
                {
                    new() { ["ref"] = "attachment", ["table"] = cfg.Table, ["action"] = "insert", ["data"] = row }
                },
                authContext,
                useTransaction: true);
            res = ((List<Dictionary<string, object?>>)outcome["results"]!)[0];
        }
        catch
        {
            storage.Delete(key); // no orphan file when the row is refused
            throw;
        }

        var response = new Dictionary<string, object?>
        {
            ["id"] = res.TryGetValue("id", out var id) ? id : null,
            ["filename"] = row["filename"],
            ["size"] = stored["size"],
            ["mime_type"] = stored["mime_type"],
            ["sha256"] = stored["sha256"]
        };
        foreach (var field in cfg.Fields)
            if (data.TryGetValue(field, out var fv)) response[field] = fv;
        return response;
    }

    /// <summary>
    /// Resolve an attachment for download. A row the caller may not see is reported as 404,
    /// the same as a missing one (no existence oracle).
    /// </summary>
    public AttachmentFile Open(string name, object id, Dictionary<string, object?> authContext)
    {
        var cfg = Config(name);
        _uploads.Authorize(name, authContext);
        var row = FindRow(cfg.Table, id, authContext);
        var stream = _uploads.StorageFor(name).Open(Convert.ToString(row["file_key"])!)
            ?? throw new ApiException("NOT_FOUND", "The attachment file is missing from storage.", 404);
        return new AttachmentFile(
            stream,
            Convert.ToString(row["filename"])!,
            Convert.ToString(row["mime_type"])!,
            Convert.ToInt64(row["size_bytes"]),
            Convert.ToString(row["sha256"])!);
    }

    /// <summary>Delete the row (scopes, roles and audit apply), then the file.</summary>
    public void Delete(string name, object id, Dictionary<string, object?> authContext)
    {
        var cfg = Config(name);
        _uploads.Authorize(name, authContext);
        var row = FindRow(cfg.Table, id, authContext);
        var outcome = _executor.ExecuteMutations(
            new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["ref"] = "attachment", ["table"] = cfg.Table, ["action"] = "delete",
                    ["filter"] = new Dictionary<string, object?> { ["id"] = row["id"] }
                }
            },
            authContext,
            useTransaction: true);
        var res = ((List<Dictionary<string, object?>>)outcome["results"]!)[0];
        if (Convert.ToInt32(res["affected"]) > 0)
            _uploads.StorageFor(name).Delete(Convert.ToString(row["file_key"])!);
    }

    // ── Internals ─────────────────────────────────────────────────────

    private sealed record Resolved(string Table, List<string> Fields, Dictionary<string, string> Parents);

    private Resolved Config(string name)
    {
        var att = _uploads.Entry(name)?.Attachments
            ?? throw new ApiException(
                "NOT_FOUND",
                $"Upload endpoint '{name}' does not store attachments.",
                404,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = $"Declare Uploads[\"{name}\"].Attachments = new AttachmentsConfig {{ Table = ..., Fields = ... }}."
                });
        if (string.IsNullOrEmpty(att.Table) || !Ident.IsMatch(att.Table))
            throw new ApiException("UPLOAD_CONFIG_ERROR", $"Attachments of '{name}' need a valid 'Table'.", 500);

        var fields = new List<string>(att.Fields);
        foreach (var field in att.Parents.Keys)
            if (!fields.Contains(field)) fields.Add(field);
        return new Resolved(att.Table, fields, new Dictionary<string, string>(att.Parents));
    }

    /// <summary>Read one attachment row through the table's scoped <c>.get</c> mask.</summary>
    private Dictionary<string, object?> FindRow(string table, object id, Dictionary<string, object?> authContext) =>
        GetOne(table, id, authContext) ?? throw new ApiException("NOT_FOUND", "Attachment not found.", 404);

    private void AssertParentVisible(string table, object id, Dictionary<string, object?> authContext)
    {
        if (GetOne(table, id, authContext) == null)
        {
            throw new ApiException(
                "NOT_FOUND",
                $"The referenced {table} row was not found.",
                404,
                null,
                new Dictionary<string, object?> { ["hint"] = "Either it does not exist or you are not allowed to see it." });
        }
    }

    private Dictionary<string, object?>? GetOne(string table, object id, Dictionary<string, object?> authContext)
    {
        var outcome = _executor.Execute(
            new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["ref"] = "one",
                    ["sql"] = table + ".get",
                    ["params"] = new Dictionary<string, object?> { ["id"] = id }
                }
            },
            authContext);
        var first = ((List<Dictionary<string, object?>>)outcome["results"]!)[0];
        var meta = (Dictionary<string, object?>)first["meta"]!;
        var cols = (List<string>)meta["columns"]!;
        var rows = (List<object?[]>)first["rows"]!;
        if (rows.Count == 0) return null;
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < cols.Count; i++) row[cols[i]] = rows[0][i];
        return row;
    }

    /// <summary>Keep a readable name but strip path parts, quotes and control characters.</summary>
    internal static string CleanFilename(string name)
    {
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = Regex.Replace(name, "[\\x00-\\x1F\\x7F\"]+", "").Trim();
        if (name.Length == 0 || name is "." or "..") return "file";
        return name.Length > 200 ? name[..200] : name;
    }
}
