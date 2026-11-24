using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Shape.Http;

/// <summary>
/// HTTP cache-header helpers for the GET surface.
/// <list type="bullet">
///   <item><b>ETag</b> is computed from the response body on every GET — costs
///   almost nothing and lets clients revalidate cheaply.</item>
///   <item><b>Cache-Control</b> is opt-in per mask. When a mask carries a
///   <c>CacheControl</c> string, it's emitted verbatim — the dev declares the
///   privacy posture, the framework doesn't second-guess.</item>
/// </list>
/// <c>If-None-Match</c> short-circuits to <c>304 Not Modified</c> when the
/// client's cached ETag matches.
/// </summary>
public static class CacheHeaders
{
    /// <summary>
    /// Compute and emit ETag (and optional Cache-Control). Returns <c>true</c>
    /// when <c>If-None-Match</c> matches and the caller should skip the body
    /// (sets status 304).
    /// </summary>
    public static bool ShouldRespond304(HttpContext context, byte[] body, string? cacheControl = null)
    {
        var etag = "\"" + Sha256Hex(body) + "\"";

        if (!string.IsNullOrEmpty(cacheControl))
            context.Response.Headers["Cache-Control"] = cacheControl;
        context.Response.Headers["ETag"] = etag;

        var ifNoneMatch = context.Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && EtagMatches(ifNoneMatch, etag))
        {
            context.Response.StatusCode = 304;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Match <c>If-None-Match</c> against our computed ETag. Honors <c>*</c>,
    /// comma-separated lists, and weak validator prefix <c>W/</c>.
    /// </summary>
    public static bool EtagMatches(string ifNoneMatch, string ourEtag)
    {
        if (ifNoneMatch.Trim() == "*")
            return true;

        foreach (var rawCandidate in ifNoneMatch.Split(','))
        {
            var candidate = rawCandidate.Trim();
            if (candidate.StartsWith("W/", StringComparison.Ordinal))
                candidate = candidate[2..];
            if (candidate == ourEtag)
                return true;
        }
        return false;
    }

    private static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
