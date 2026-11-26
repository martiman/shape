<?php

declare(strict_types=1);

namespace Shape\Upload;

use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;

/**
 * Private attachments: files linked to a row of an ordinary table, readable
 * only through Shape so that the table's row scopes and roles decide who sees
 * them (a user who cannot read the case cannot read its documents).
 *
 * Config (on an `uploads` entry):
 *
 *   'case_files' => [
 *       'storage'     => ['driver' => 'local', 'path' => '/srv/app-files/case_files'],  // outside the webroot
 *       'roles'       => ['admin', 'user'],
 *       'max_size'    => 20 * 1024 * 1024,
 *       'mime_types'  => ['application/pdf', 'image/jpeg'],
 *       'attachments' => [
 *           'table'   => 'case_files',          // ordinary table, see below
 *           'fields'  => ['case_id'],           // form fields copied into the row
 *           'parents' => ['case_id' => 'cases'] // optional: caller must be able to read cases.get(case_id)
 *       ],
 *   ],
 *
 * Table convention (columns the service fills; add scope/audit columns as usual):
 *
 *   id, file_key VARCHAR(64), filename VARCHAR(255), mime_type VARCHAR(100),
 *   size_bytes BIGINT, sha256 CHAR(64), <fields…>
 *
 * Every database step goes through BatchExecutor, so scopes, tenancy, access
 * roles, validators and the audit log apply exactly as for a normal mutation.
 *
 * Routes (wired by ShapeRouter):
 *   POST   /api.php/upload/<name>        multipart `file` + the declared fields -> attachment row
 *   GET    /api.php/files/<name>/<id>    stream the file (attachment, nosniff, no-store)
 *   DELETE /api.php/files/<name>/<id>    delete the row, then the file
 */
final class AttachmentService
{
    public function __construct(
        private readonly UploadManager $uploads,
        private readonly BatchExecutor $executor,
    ) {
    }

    /** Whether the upload entry stores private attachments. */
    public function isAttachmentEntry(string $name): bool
    {
        return is_array($this->uploads->entry($name)['attachments'] ?? null);
    }

    /**
     * @param array<string, mixed> $files    $_FILES
     * @param array<string, mixed> $form     $_POST (only the declared fields are used)
     * @param array<string, mixed> $authContext
     * @return array<string, mixed>
     */
    public function upload(string $name, array $files, array $form, array $authContext): array
    {
        $cfg = $this->config($name);
        $this->uploads->authorize($name, $authContext);

        $data = [];
        foreach ($cfg['fields'] as $field) {
            if (!array_key_exists($field, $form)) {
                continue;
            }
            $v = $form[$field];
            $data[$field] = is_string($v) && ctype_digit($v) ? (int)$v : $v;
        }
        $this->assertParents($cfg, $data, $authContext);

        return $this->persist($name, $cfg, $data, fn() => $this->uploads->handle($name, $files, $authContext), $authContext);
    }

    /**
     * Store generated content (a rendered PDF) as an attachment of the given upload entry, with the same
     * authorisation as an upload: role gate, parent visibility, row scopes, validators and audit.
     *
     * @param array<string, mixed> $fields   values of the declared attachment fields (e.g. ['invoice_id' => 5])
     * @param array<string, mixed> $authContext
     * @return array<string, mixed> the attachment descriptor, as returned by {@see upload()}
     */
    public function storeGenerated(string $name, string $bytes, string $filename, array $fields, array $authContext): array
    {
        $cfg = $this->config($name);
        $this->uploads->authorize($name, $authContext);
        $data = array_intersect_key($fields, array_flip($cfg['fields']));
        $this->assertParents($cfg, $data, $authContext);
        return $this->persist($name, $cfg, $data, fn() => $this->uploads->storeBytes($name, $bytes, $filename, $authContext), $authContext);
    }

    /**
     * @param array{table:string, fields:list<string>, parents:array<string,string>} $cfg
     * @param array<string, mixed> $data
     * @param array<string, mixed> $authContext
     * @return array<string, mixed>
     */
    private function persist(string $name, array $cfg, array $data, \Closure $store, array $authContext): array
    {
        $stored = $store();
        $storage = $this->uploads->storageFor($name);

        $row = $data + [
            'file_key'   => $stored['id'],
            'filename'   => self::cleanFilename((string)$stored['filename']),
            'mime_type'  => $stored['mime_type'],
            'size_bytes' => $stored['size'],
            'sha256'     => $stored['sha256'],
        ];
        try {
            /** @var array<string, mixed> $res */
            $res = $this->executor->executeMutations(
                [['ref' => 'attachment', 'table' => $cfg['table'], 'action' => 'insert', 'data' => $row]],
                $authContext,
                true,
            )['results'][0] ?? [];
        } catch (\Throwable $e) {
            $storage->delete((string)$stored['id']);  // no orphan file when the row is refused
            throw $e;
        }

        return [
            'id'        => $res['new_id'] ?? $res['id'] ?? null,
            'filename'  => $row['filename'],
            'size'      => $stored['size'],
            'mime_type' => $stored['mime_type'],
            'sha256'    => $stored['sha256'],
        ] + array_intersect_key($data, array_flip($cfg['fields']));
    }

    /**
     * Resolve an attachment for download. A row the caller may not see is
     * reported as 404, the same as a missing one (no existence oracle).
     *
     * @param array<string, mixed> $authContext
     * @return array{stream:resource, filename:string, mime_type:string, size:int, sha256:string}
     */
    public function open(string $name, int|string $id, array $authContext): array
    {
        $cfg = $this->config($name);
        $this->uploads->authorize($name, $authContext);
        $row = $this->findRow($cfg['table'], $id, $authContext);
        $stream = $this->uploads->storageFor($name)->open((string)$row['file_key']);
        if ($stream === null) {
            throw new ApiException('NOT_FOUND', 'The attachment file is missing from storage.', 404);
        }
        return [
            'stream'    => $stream,
            'filename'  => (string)$row['filename'],
            'mime_type' => (string)$row['mime_type'],
            'size'      => (int)$row['size_bytes'],
            'sha256'    => (string)$row['sha256'],
        ];
    }

    /**
     * Delete the row (scopes, roles and audit apply), then the file.
     *
     * @param array<string, mixed> $authContext
     */
    public function delete(string $name, int|string $id, array $authContext): void
    {
        $cfg = $this->config($name);
        $this->uploads->authorize($name, $authContext);
        $row = $this->findRow($cfg['table'], $id, $authContext);
        $res = $this->executor->executeMutations(
            [['ref' => 'attachment', 'table' => $cfg['table'], 'action' => 'delete', 'filter' => ['id' => $row['id']]]],
            $authContext,
            true,
        )['results'][0] ?? [];
        if ((int)($res['affected'] ?? 0) > 0) {
            $this->uploads->storageFor($name)->delete((string)$row['file_key']);
        }
    }

    // ── Internals ─────────────────────────────────────────────────────

    /** @return array{table:string, fields:list<string>, parents:array<string,string>} */
    private function config(string $name): array
    {
        $entry = $this->uploads->entry($name);
        $att = is_array($entry['attachments'] ?? null) ? $entry['attachments'] : null;
        if ($att === null) {
            throw new ApiException(
                'NOT_FOUND',
                "Upload endpoint '{$name}' does not store attachments.",
                404,
                null,
                ['hint' => "Declare config['uploads']['{$name}']['attachments'] = ['table' => ..., 'fields' => [...]]."]
            );
        }
        $table = (string)($att['table'] ?? '');
        if ($table === '' || preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $table) !== 1) {
            throw new ApiException('UPLOAD_CONFIG_ERROR', "Attachments of '{$name}' need a valid 'table'.", 500);
        }
        $fields = array_values(array_map('strval', (array)($att['fields'] ?? [])));
        $parents = [];
        foreach ((array)($att['parents'] ?? []) as $field => $parentTable) {
            $parents[(string)$field] = (string)$parentTable;
            if (!in_array((string)$field, $fields, true)) {
                $fields[] = (string)$field;
            }
        }
        return ['table' => $table, 'fields' => $fields, 'parents' => $parents];
    }

    /**
     * Read one attachment row through the table's scoped `.get` mask.
     *
     * @param array<string, mixed> $authContext
     * @return array<string, mixed>
     */
    private function findRow(string $table, int|string $id, array $authContext): array
    {
        $row = $this->getOne($table, $id, $authContext);
        if ($row === null) {
            throw new ApiException('NOT_FOUND', 'Attachment not found.', 404);
        }
        return $row;
    }

    /**
     * Authorise the parent rows BEFORE storing anything.
     *
     * @param array{table:string, fields:list<string>, parents:array<string,string>} $cfg
     * @param array<string, mixed> $data
     * @param array<string, mixed> $authContext
     */
    private function assertParents(array $cfg, array $data, array $authContext): void
    {
        foreach ($cfg['parents'] as $field => $table) {
            if (!isset($data[$field])) {
                throw new ApiException(
                    'UPLOAD_VALIDATION_ERROR',
                    "Form field '{$field}' is required.",
                    400,
                    null,
                    ['expected' => ['form' => $field], 'hint' => "Send '{$field}' next to the file in the multipart body."]
                );
            }
            $this->assertParentVisible($table, $data[$field], $authContext);
        }
    }

    /** @param array<string, mixed> $authContext */
    private function assertParentVisible(string $table, mixed $id, array $authContext): void
    {
        if ($this->getOne($table, $id, $authContext) === null) {
            throw new ApiException(
                'NOT_FOUND',
                "The referenced {$table} row was not found.",
                404,
                null,
                ['hint' => 'Either it does not exist or you are not allowed to see it.']
            );
        }
    }

    /**
     * @param array<string, mixed> $authContext
     * @return array<string, mixed>|null
     */
    private function getOne(string $table, mixed $id, array $authContext): ?array
    {
        $r = $this->executor->execute(
            [['ref' => 'one', 'sql' => $table . '.get', 'params' => ['id' => $id]]],
            $authContext
        )['results'][0] ?? [];
        $cols = $r['meta']['columns'] ?? [];
        $first = $r['rows'][0] ?? null;
        return $first === null ? null : array_combine($cols, $first);
    }

    /** Keep a readable name but strip path parts and control characters. */
    private static function cleanFilename(string $name): string
    {
        $name = basename(str_replace('\\', '/', $name));
        $name = (string)preg_replace('/[\x00-\x1F\x7F"]+/u', '', $name);
        $name = trim($name);
        if ($name === '' || $name === '.' || $name === '..') {
            return 'file';
        }
        return mb_substr($name, 0, 200);
    }
}
