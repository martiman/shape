<?php

declare(strict_types=1);

namespace Shape\Upload;

use ShapeRuntime\Core\ApiException;

/**
 * Resolve the configured upload entry, validate the incoming file
 * against per-entry rules (size, mime), generate a stable storage key,
 * dispatch to the configured backend, and shape the JSON response.
 *
 * Config under `config['uploads']`:
 *
 *   'uploads' => [
 *       'covers' => [
 *           'storage' => [
 *               'driver'     => 'local',
 *               'path'       => './uploads/covers',
 *               'public_url' => '/uploads/covers',
 *           ],
 *           'roles'       => ['admin'],
 *           'max_size'    => 10485760,                 // 10MB
 *           'mime_types'  => ['image/jpeg', 'image/png'],
 *           'description' => 'Upload a book cover image.',
 *       ],
 *   ],
 *
 * The wire route is `POST /api.php/upload/<name>` with a multipart body
 * containing a single `file` field. Auth flows through ShapeRouter's
 * usual JwtGuard pipeline; the role check happens here.
 *
 * Response shape:
 *
 *   {
 *     "id":        "abc123ef.jpg",         // stable storage key
 *     "filename":  "book-cover.jpg",       // original client-supplied name
 *     "size":      102400,
 *     "mime_type": "image/jpeg",
 *     "url":       "/uploads/covers/abc123ef.jpg"
 *   }
 *
 * Devs typically follow with a normal mutation linking the URL or `id`
 * into a domain row (e.g. `books.cover_url`).
 */
final class UploadManager
{
    /** Default upload cap (bytes) when an endpoint sets no explicit max_size. */
    private const DEFAULT_MAX_UPLOAD_BYTES = 10 * 1024 * 1024;

    /** @param array<string, array<string, mixed>> $uploads */
    public function __construct(private readonly array $uploads)
    {
    }

    /**
     * @param array<string, mixed> $authContext
     * @return array<string, mixed>
     */
    public function handle(string $name, array $files, array $authContext): array
    {
        $entry = $this->uploads[$name] ?? null;
        if (!is_array($entry)) {
            throw new ApiException(
                'NOT_FOUND',
                "Upload endpoint '{$name}' is not configured.",
                404,
                null,
                [
                    'expected' => $this->uploads === []
                        ? 'No upload endpoints are configured for this app.'
                        : 'one of: ' . implode(', ', array_keys($this->uploads)),
                    'hint' => "Upload endpoints are declared under config['uploads']['<name>'].",
                ]
            );
        }

        $this->authorizeRole($entry, $authContext);

        $file = $this->extractFile($files);
        $detectedMime = $this->validateFile($file, $entry);

        // SECURITY-REVIEW M6: derive the stored extension from the detected
        // MIME, not the client filename — a PNG-content file named "x.html"
        // must not be stored (and later served) as .html.
        $key = $this->generateKey($detectedMime);
        $storage = $this->buildStorage($entry);
        $sha256 = (string)hash_file('sha256', $file['tmp_name']);
        $url = $storage->put($key, $file['tmp_name'], $detectedMime);
        // Private attachments are never served statically: no URL is exposed,
        // the file is fetched through GET /api.php/files/<name>/<id>.
        $isPrivate = is_array($entry['attachments'] ?? null);

        return [
            'id'        => $key,
            'filename'  => (string)$file['name'],
            'size'      => (int)$file['size'],
            // Report the *detected* mime, not the client-supplied header —
            // a client claiming Content-Type: image/jpeg for what's actually
            // a text file would otherwise mislead downstream consumers.
            'mime_type' => $detectedMime,
            'sha256'    => $sha256,
            'url'       => $isPrivate ? null : $url,
        ];
    }

    /**
     * Store generated content (e.g. a rendered PDF) exactly as if it had been uploaded: the same role gate, size
     * and mime rules, key generation and sha256. Used by the document archive.
     *
     * @param array<string, mixed> $authContext
     * @return array<string, mixed> same shape as {@see handle()}
     */
    public function storeBytes(string $name, string $bytes, string $filename, array $authContext): array
    {
        $tmp = tempnam(sys_get_temp_dir(), 'shape-gen-');
        if ($tmp === false || file_put_contents($tmp, $bytes) === false) {
            throw new ApiException('UPLOAD_STORAGE_ERROR', 'Cannot write a temporary file for the generated content.', 500);
        }
        try {
            return $this->handle($name, ['file' => [
                'name' => $filename, 'type' => '', 'tmp_name' => $tmp, 'error' => UPLOAD_ERR_OK, 'size' => strlen($bytes),
            ]], $authContext);
        } finally {
            if (is_file($tmp)) {
                @unlink($tmp);   // LocalStorage moves the file; clean up when it did not (or validation failed)
            }
        }
    }

    /** @return array<string, mixed>|null The configured upload entry, or null. */
    public function entry(string $name): ?array
    {
        $e = $this->uploads[$name] ?? null;
        return is_array($e) ? $e : null;
    }

    /** Storage backend of an upload entry (used by the attachment service). */
    public function storageFor(string $name): StorageInterface
    {
        $entry = $this->entry($name);
        if ($entry === null) {
            throw new ApiException('NOT_FOUND', "Upload endpoint '{$name}' is not configured.", 404);
        }
        return $this->buildStorage($entry);
    }

    /** @param array<string, mixed> $authContext */
    public function authorize(string $name, array $authContext): void
    {
        $entry = $this->entry($name);
        if ($entry === null) {
            throw new ApiException('NOT_FOUND', "Upload endpoint '{$name}' is not configured.", 404);
        }
        $this->authorizeRole($entry, $authContext);
    }

    /** @return list<string> Names of configured upload endpoints. */
    public function names(): array
    {
        return array_keys($this->uploads);
    }

    // ── Internals ─────────────────────────────────────────────────────

    /** @param array<string, mixed> $entry */
    private function authorizeRole(array $entry, array $authContext): void
    {
        $allowed = $entry['roles'] ?? [];
        if (!is_array($allowed) || $allowed === []) {
            return;  // public upload endpoint
        }
        $role = isset($authContext['role']) ? (string)$authContext['role'] : '';
        if ($role === '' || !in_array($role, $allowed, true)) {
            throw new ApiException(
                'AUTH_FORBIDDEN',
                "Current role is not allowed for this upload endpoint.",
                403,
                null,
                [
                    'expected' => ['role' => 'one of: ' . implode(', ', $allowed)],
                    'hint' => $role === ''
                        ? "Authenticate first via POST {op:'auth.token'} and pass the access token as Authorization: Bearer <token>."
                        : "Your role '{$role}' cannot use this upload endpoint.",
                ]
            );
        }
    }

    /**
     * Pull the single uploaded file out of $_FILES. Accepts either a
     * `file` field (the documented convention) or, if a single file is
     * present under any other name, that one — common when clients use
     * `<input name="cover">`-style forms. Multi-file uploads are not
     * supported in v1; document and reject.
     *
     * @param array<string, mixed> $files
     * @return array{name:string,type:string,tmp_name:string,error:int,size:int}
     */
    private function extractFile(array $files): array
    {
        if ($files === []) {
            throw new ApiException(
                'UPLOAD_VALIDATION_ERROR',
                'No file uploaded. Send a multipart/form-data request with a `file` field.',
                400,
                null,
                [
                    'expected' => "multipart/form-data body with a single 'file' field",
                    'hint' => "Use Content-Type: multipart/form-data and attach the file under the `file` form name.",
                ]
            );
        }

        $candidate = $files['file'] ?? null;
        if ($candidate === null && count($files) === 1) {
            $candidate = reset($files);
        }
        if (!is_array($candidate)) {
            throw new ApiException(
                'UPLOAD_VALIDATION_ERROR',
                'Multi-file uploads are not supported in v1; send a single file under the `file` form name.',
                400,
            );
        }
        // PHP normalizes single-file form fields into a flat map; multi-file
        // arrays use parallel-list shape — reject the latter explicitly.
        if (is_array($candidate['name'] ?? null)) {
            throw new ApiException(
                'UPLOAD_VALIDATION_ERROR',
                'Multi-file uploads are not supported in v1.',
                400,
            );
        }

        $err = (int)($candidate['error'] ?? UPLOAD_ERR_NO_FILE);
        if ($err !== UPLOAD_ERR_OK) {
            throw new ApiException(
                'UPLOAD_VALIDATION_ERROR',
                'Upload failed: ' . self::uploadErrorMessage($err),
                400,
            );
        }

        return [
            'name'     => (string)($candidate['name'] ?? 'unnamed'),
            'type'     => (string)($candidate['type'] ?? 'application/octet-stream'),
            'tmp_name' => (string)($candidate['tmp_name'] ?? ''),
            'error'    => $err,
            'size'     => (int)($candidate['size'] ?? 0),
        ];
    }

    /**
     * Validate the uploaded file against the entry's rules and return
     * the detected mime type (so the caller stores and reports the
     * detected value rather than the client-supplied header).
     *
     * @param array{name:string,type:string,tmp_name:string,error:int,size:int} $file
     * @param array<string, mixed> $entry
     */
    private function validateFile(array $file, array $entry): string
    {
        if ($file['size'] <= 0) {
            throw new ApiException('UPLOAD_VALIDATION_ERROR', 'Uploaded file is empty.', 400);
        }

        // SECURITY-REVIEW M5: apply a default cap when the endpoint sets none,
        // so an unbounded upload can't be read into memory at store time.
        $maxSize = isset($entry['max_size']) && (int)$entry['max_size'] > 0
            ? (int)$entry['max_size']
            : self::DEFAULT_MAX_UPLOAD_BYTES;
        if ($file['size'] > $maxSize) {
            $sizeKb = (int)ceil($file['size'] / 1024);
            $maxKb  = (int)ceil($maxSize / 1024);
            throw new ApiException(
                'UPLOAD_VALIDATION_ERROR',
                "File too large: {$sizeKb} KB exceeds limit of {$maxKb} KB.",
                400,
                null,
                [
                    'expected' => ['max_size' => $maxSize . ' bytes'],
                    'hint' => 'Compress or resize the file, or ask the admin to raise the limit on this endpoint.',
                ]
            );
        }

        // Detect from file content, not the client-supplied Content-Type header
        // (which is trivially spoofable). The detected value is also what
        // gets returned to the caller — what's on disk is the truth.
        $detected = self::detectMimeType($file['tmp_name'], (string)$file['type']);

        $allowedMime = $entry['mime_types'] ?? [];
        if (is_array($allowedMime) && $allowedMime !== []) {
            if (!in_array($detected, $allowedMime, true)) {
                throw new ApiException(
                    'UPLOAD_VALIDATION_ERROR',
                    "Disallowed mime type: '{$detected}'.",
                    400,
                    null,
                    [
                        'expected' => ['mime_type' => 'one of: ' . implode(', ', $allowedMime)],
                        'hint' => 'The mime type is detected from file contents, not the client header — relabeling the client request will not help.',
                    ]
                );
            }
        }

        return $detected;
    }

    /**
     * Generate a stable storage key: <16-hex-random> + an extension derived
     * from the DETECTED MIME type (SECURITY-REVIEW M6). The client filename
     * never influences the stored extension, so a mislabeled file cannot be
     * stored — and later served — as an executable/renderable type. Unknown
     * MIME → no extension.
     */
    private function generateKey(string $detectedMime): string
    {
        $ext = match ($detectedMime) {
            'image/png' => '.png',
            'image/jpeg' => '.jpg',
            'image/gif' => '.gif',
            'image/webp' => '.webp',
            'application/pdf' => '.pdf',
            'application/zip' => '.zip',
            default => '',
        };
        return bin2hex(random_bytes(8)) . $ext;
    }

    /** @param array<string, mixed> $entry */
    private function buildStorage(array $entry): StorageInterface
    {
        $cfg = is_array($entry['storage'] ?? null) ? $entry['storage'] : [];
        $driver = (string)($cfg['driver'] ?? 'local');
        return match ($driver) {
            'local' => new LocalStorage($cfg),
            default => throw new ApiException(
                'UPLOAD_CONFIG_ERROR',
                "Unsupported storage driver '{$driver}'. Only 'local' is supported in v1.",
                500,
            ),
        };
    }

    private static function detectMimeType(string $tmpPath, string $clientHint): string
    {
        if (function_exists('finfo_open')) {
            $finfo = finfo_open(FILEINFO_MIME_TYPE);
            if ($finfo !== false) {
                $mime = finfo_file($finfo, $tmpPath);
                finfo_close($finfo);
                if (is_string($mime) && $mime !== '') {
                    return $mime;
                }
            }
        }
        return $clientHint !== '' ? $clientHint : 'application/octet-stream';
    }

    private static function uploadErrorMessage(int $code): string
    {
        return match ($code) {
            UPLOAD_ERR_INI_SIZE   => 'file exceeds upload_max_filesize',
            UPLOAD_ERR_FORM_SIZE  => 'file exceeds form MAX_FILE_SIZE',
            UPLOAD_ERR_PARTIAL    => 'partial upload',
            UPLOAD_ERR_NO_FILE    => 'no file uploaded',
            UPLOAD_ERR_NO_TMP_DIR => 'server has no temp directory',
            UPLOAD_ERR_CANT_WRITE => 'server failed to write to disk',
            UPLOAD_ERR_EXTENSION  => 'a PHP extension stopped the upload',
            default               => 'unknown error code ' . $code,
        };
    }
}
