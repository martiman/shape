<?php

declare(strict_types=1);

namespace Shape\Http;

/**
 * HTTP cache-header helpers for the GET surface.
 *
 * Two pieces:
 *  - **ETag** is computed from the response body on every GET. The header is
 *    emitted always; it costs almost nothing and lets clients revalidate.
 *  - **Cache-Control** is opt-in per mask. When a mask's config carries a
 *    `'cache_control'` string, it's emitted verbatim. The dev declares the
 *    privacy posture (`public` / `private` / `max-age=…`) — the framework
 *    doesn't second-guess.
 *
 * `If-None-Match` is honored when the request includes it: a matching ETag
 * collapses the response to `304 Not Modified` with no body — saves the
 * bandwidth on hot endpoints, and combined with a CDN-friendly Cache-Control
 * makes a Shape backend dramatically cheaper to run at scale.
 *
 * Usage:
 *
 *   $body = json_encode($response, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
 *   if (CacheHeaders::shouldRespond304($body, $cacheControl)) {
 *       return;  // headers sent, body intentionally skipped
 *   }
 *   header('Content-Type: application/json');
 *   echo $body;
 */
final class CacheHeaders
{
    /**
     * Emit ETag (and optional Cache-Control), then decide whether to short-
     * circuit to 304. Returns true if the caller should skip writing the body.
     */
    public static function shouldRespond304(string $body, ?string $cacheControl = null): bool
    {
        $etag = '"' . hash('sha256', $body) . '"';

        if (is_string($cacheControl) && $cacheControl !== '') {
            header('Cache-Control: ' . $cacheControl);
        }
        header('ETag: ' . $etag);

        $ifNoneMatch = self::headerValue('If-None-Match');
        if ($ifNoneMatch !== null && self::etagMatches($ifNoneMatch, $etag)) {
            http_response_code(304);
            return true;
        }
        return false;
    }

    private static function headerValue(string $name): ?string
    {
        $serverKey = 'HTTP_' . str_replace('-', '_', strtoupper($name));
        $val = $_SERVER[$serverKey] ?? null;
        return is_string($val) && $val !== '' ? $val : null;
    }

    /**
     * Match If-None-Match against our computed ETag.
     *
     * Honors:
     *  - `*` (matches any ETag — used for "I have something cached, anything")
     *  - Comma-separated lists
     *  - Weak validator prefix `W/` (treated as a match for our strong ETag)
     */
    private static function etagMatches(string $ifNoneMatch, string $ourEtag): bool
    {
        if (trim($ifNoneMatch) === '*') {
            return true;
        }
        foreach (explode(',', $ifNoneMatch) as $candidate) {
            $candidate = trim($candidate);
            if (str_starts_with($candidate, 'W/')) {
                $candidate = substr($candidate, 2);
            }
            if ($candidate === $ourEtag) {
                return true;
            }
        }
        return false;
    }
}
