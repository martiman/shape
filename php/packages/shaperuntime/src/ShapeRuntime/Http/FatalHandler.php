<?php

declare(strict_types=1);

namespace ShapeRuntime\Http;

/**
 * Convert uncatchable PHP fatals (E_ERROR / E_PARSE / E_CORE_ERROR /
 * E_COMPILE_ERROR — typically OOM, type errors before script entry,
 * missing includes, segfaults from C extensions) into the framework's
 * standard JSON error envelope.
 *
 * Without this, those errors leak PHP's default HTML error page and break
 * the self-documenting-errors contract precisely when an operator or LLM
 * client most needs structured information.
 *
 * Lives in shaperuntime so both routers (ShapeRuntime\Http\AppRouter and
 * Shape\ShapeRouter) can wire it from their entry points without
 * duplicating the logic.
 */
final class FatalHandler
{
    /**
     * Register the shutdown hook. Idempotent — registers at most once per
     * process. No-op under PHP_SAPI === 'cli' so direct script invocations
     * don't get a confusing JSON envelope on top of normal error reporting;
     * HTTP SAPIs (cli-server / fpm-fcgi / mod_php / litespeed) all opt in.
     */
    public static function register(): void
    {
        static $registered = false;
        if ($registered) {
            return;
        }
        $registered = true;

        if (PHP_SAPI === 'cli') {
            return;
        }

        register_shutdown_function(static function (): void {
            $err = error_get_last();
            if ($err === null) {
                return;
            }
            $fatalTypes = E_ERROR | E_PARSE | E_CORE_ERROR | E_COMPILE_ERROR | E_USER_ERROR | E_RECOVERABLE_ERROR;
            if (((int)$err['type'] & $fatalTypes) === 0) {
                return;
            }

            // Some output (e.g. partial JSON, or a raw HTML error page from
            // PHP's default handler) may already be on the wire. Emit our
            // envelope on a best-effort basis: if headers are still open,
            // set status + content-type; otherwise just append the JSON so
            // it's at least parseable from the response tail.
            if (!headers_sent()) {
                http_response_code(500);
                header('Content-Type: application/json');
            }

            $rawMessage = (string)$err['message'];

            // Classify from the (server-side) raw message for a useful hint, but
            // never return the raw text to the client.
            $hint = match (true) {
                str_contains($rawMessage, 'Allowed memory size')
                    => 'PHP exhausted its memory limit. Lower ?$top, paginate the read, or increase memory_limit.',
                str_contains($rawMessage, 'Maximum execution time')
                    => 'Request exceeded max_execution_time. Narrow the query or raise the timeout.',
                default
                    => 'Contact the operator with the correlation id; full stack details are in the server error log.',
            };

            // SECURITY-REVIEW M7: return a generic message + correlation id; the
            // raw fatal (paths, SQL, internals) goes only to the server log.
            $correlationId = bin2hex(random_bytes(8));
            error_log("[shape][INTERNAL_ERROR][{$correlationId}] " . $rawMessage);

            echo json_encode([
                'error' => [
                    'code'           => 'INTERNAL_ERROR',
                    'message'        => 'An internal error occurred. Contact the operator with the correlation id.',
                    'correlation_id' => $correlationId,
                    'hint'           => $hint,
                ],
            ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        });
    }
}
