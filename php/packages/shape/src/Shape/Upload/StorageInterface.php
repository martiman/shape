<?php

declare(strict_types=1);

namespace Shape\Upload;

/**
 * Abstract over file-storage backends so LocalStorage today can become
 * S3Storage / GcsStorage / R2Storage later without changing the upload
 * manager. Each implementation is responsible only for the put/get/delete
 * primitives — validation, naming, and metadata live in UploadManager.
 */
interface StorageInterface
{
    /**
     * Store the bytes at the given relative key. The implementation may
     * choose to nest, hash, or shard the key as long as it returns a
     * stable URL the caller can use to retrieve the file later.
     *
     * @param string $key      Relative key (e.g. 'abc123.jpg')
     * @param string $tmpPath  Path to the uploaded temp file (move from there)
     * @param string $mimeType Content-type to record/serve
     * @return string Public URL the client can use to fetch the file
     */
    public function put(string $key, string $tmpPath, string $mimeType): string;

    /** Whether a previously-stored object exists. */
    public function exists(string $key): bool;

    /**
     * Open the object for reading (private attachments are streamed through
     * Shape after an authorisation check instead of being served statically).
     *
     * @return resource|null null when the key does not exist
     */
    public function open(string $key);

    /** Remove the object. No-op if the key doesn't exist. */
    public function delete(string $key): void;

    /** Driver name (`local`, `s3`, …) for diagnostics and manifest output. */
    public function driver(): string;
}
