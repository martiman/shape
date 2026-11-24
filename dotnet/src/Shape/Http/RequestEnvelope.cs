using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shape.Core;

namespace Shape.Http;

public static class RequestEnvelope
{
    public static async Task<(string Op, Dictionary<string, object?> Payload)> ReadAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var raw = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(raw))
            throw new ApiException("INVALID_JSON", "Request body is empty.", 400);

        Dictionary<string, object?> envelope;
        try
        {
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw)
                ?? throw new ApiException("INVALID_JSON", "Body must be valid JSON object.", 400);
            envelope = doc.ToDictionary(p => p.Key, p => ParamResolver.UnwrapJsonElement(p.Value));
        }
        catch (JsonException)
        {
            throw new ApiException("INVALID_JSON", "Body must be valid JSON object.", 400);
        }

        var op = envelope.GetValueOrDefault("op")?.ToString()?.Trim() ?? "";
        if (string.IsNullOrEmpty(op))
            throw new ApiException("REQUEST_VALIDATION_ERROR", "Field op is required.", 400);

        // For meta ops (manifest/openapi) the payload is allowed to be absent.
        if (op is "manifest" or "openapi")
        {
            var metaPayload = envelope.TryGetValue("payload", out var metaRaw) && metaRaw is Dictionary<string, object?> metaDict
                ? metaDict
                : new Dictionary<string, object?>();
            return (op, metaPayload);
        }

        if (!envelope.TryGetValue("payload", out var payloadRaw) || payloadRaw is not Dictionary<string, object?> payload)
            throw new ApiException("REQUEST_VALIDATION_ERROR", "Field payload must be an object.", 400);

        return (op, payload);
    }
}
