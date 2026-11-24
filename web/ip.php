<?php

declare(strict_types=1);

/**
 * Tiny endpoint that returns the IP address of the host serving this page.
 *
 * Sidecar to the static index.html / start.html so those files stay HTML
 * (no rename, no extension change). The pages do a one-line `fetch()` to
 * this endpoint and substitute the result into the footer placeholder.
 *
 * Resolution order:
 *   1. $_SERVER['SERVER_ADDR'] — the IP the SAPI is listening on. Most
 *      shared / VPS / dedicated hosts populate this with the real public
 *      IP. Containerized setups (Docker / k8s) populate with the
 *      container's internal IP, which is usually fine — that's what's
 *      actually serving the bytes.
 *   2. gethostbyname(gethostname()) — fallback when SERVER_ADDR is empty
 *      or loopback. Works on most Linux SAPIs.
 *   3. literal "unknown" — nothing else worked; the page shows it as-is.
 */

$ip = isset($_SERVER['SERVER_ADDR']) ? (string)$_SERVER['SERVER_ADDR'] : '';

if ($ip === '' || $ip === '127.0.0.1' || $ip === '::1') {
    $hostname = gethostname();
    if (is_string($hostname) && $hostname !== '') {
        $resolved = gethostbyname($hostname);
        // gethostbyname returns the input string on failure — only accept a
        // value that actually changed.
        if ($resolved !== $hostname && $resolved !== '') {
            $ip = $resolved;
        }
    }
}

header('Content-Type: text/plain; charset=utf-8');
header('Cache-Control: no-store, max-age=0');
echo $ip !== '' ? $ip : 'unknown';
