<?php

declare(strict_types=1);

namespace Shape\Upload;

use ShapeRuntime\Core\ApiException;

/**
 * Local-disk storage backend. Saves uploaded files under a configured
 * directory and serves them via a configured public-URL prefix.
 *
 * Configuration (under the upload entry's `storage` key):
 *
 *   'storage' => [
 *       'driver'     => 'local',
 *       'path'       => '/var/www/myapp/uploads/covers',  // absolute or relative to cwd
 *       'public_url' => '/uploads/covers',                 // URL prefix served by the web server
 *   ],
 *
 * The web server (Apache/nginx/php -S) serves files at `public_url` from
 * the directory at `path`. Shape doesn't proxy file reads — the upload
 * route is for write only; reads are static-served at the public URL.
 *
 * Subdirectories are created on demand (mkdir -p semantics). The upload
 * manager picks unique keys; this backend just stores bytes verbatim.
 */
final class LocalStorage implements StorageInterface
{
    private readonly string $path;
    private readonly string $publicUrl;

    /** @param array<string, mixed> $config The `storage` block */
    public function __construct(array $config)
    {
        $rawPath = (string)($config['path'] ?? '');
        if ($rawPath === '') {
            throw new ApiException('UPLOAD_CONFIG_ERROR', "Local storage requires a 'path' setting.", 500);
        }
        $this->path = rtrim($rawPath, '/');
        $this->publicUrl = rtrim((string)($config['public_url'] ?? '/uploads'), '/');

        if (!is_dir($this->path)) {
            @mkdir($this->path, 0755, true);
        }
        if (!is_dir($this->path) || !is_writable($this->path)) {
            throw new ApiException(
                'UPLOAD_CONFIG_ERROR',
                "Local storage directory '{$this->path}' is missing or not writable.",
                500,
            );
        }
    }

    public function put(string $key, string $tmpPath, string $mimeType): string
    {
        $safeKey = $this->sanitizeKey($key);
        $target = $this->path . '/' . $safeKey;

        $targetDir = dirname($target);
        if (!is_dir($targetDir)) {
            @mkdir($targetDir, 0755, true);
        }

        $moved = is_uploaded_file($tmpPath)
            ? move_uploaded_file($tmpPath, $target)
            : @rename($tmpPath, $target);

        if (!$moved && !@copy($tmpPath, $target)) {
            throw new ApiException(
                'UPLOAD_STORAGE_ERROR',
                "Failed to write upload to '{$target}'.",
                500,
            );
        }

        // Best-effort permissions; ignore failures (filesystem may not support).
        @chmod($target, 0644);

        return $this->publicUrl . '/' . $safeKey;
    }

    public function exists(string $key): bool
    {
        return is_file($this->path . '/' . $this->sanitizeKey($key));
    }

    public function open(string $key)
    {
        $target = $this->path . '/' . $this->sanitizeKey($key);
        if (!is_file($target)) {
            return null;
        }
        $h = @fopen($target, 'rb');
        return $h === false ? null : $h;
    }

    public function delete(string $key): void
    {
        $target = $this->path . '/' . $this->sanitizeKey($key);
        if (is_file($target)) {
            @unlink($target);
        }
    }

    public function driver(): string
    {
        return 'local';
    }

    /**
     * Reject path-traversal attempts and normalize separators. The upload
     * manager generates keys so this is defense-in-depth, but a future
     * caller passing untrusted input deserves the safety net.
     */
    private function sanitizeKey(string $key): string
    {
        $key = ltrim($key, '/');
        if ($key === '' || str_contains($key, '..')) {
            throw new ApiException('UPLOAD_STORAGE_ERROR', "Invalid storage key '{$key}'.", 400);
        }
        return $key;
    }
}
