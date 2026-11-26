<?php

/**
 * Private attachments: files linked to a row, read/deleted only through Shape so the
 * attachment table's row scopes decide access. SQLite + a temp storage dir.
 *
 *   vendor/bin/tester tests/Attachments.phpt
 */

declare(strict_types=1);

use Shape\Metadata\DatabaseIntrospector;
use Shape\ShapeApp;
use Shape\Upload\AttachmentService;
use Shape\Upload\UploadManager;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\MaskRegistry;
use ShapeRuntime\Core\ValidatorRegistry;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$buildFromEntities = new ReflectionMethod(ShapeApp::class, 'buildFromEntities');
$buildFromEntities->setAccessible(true);

$catch = static function (callable $fn): string {
    try {
        $fn();
    } catch (ApiException $e) {
        return $e->apiCode();
    }
    return '(none)';
};

$dir = sys_get_temp_dir() . '/shape-att-' . bin2hex(random_bytes(4));
mkdir($dir);

$db = new Database(['dsn' => 'sqlite::memory:']);
$db->exec('CREATE TABLE cases (id INTEGER PRIMARY KEY AUTOINCREMENT, owner_id INT NOT NULL, title VARCHAR(50) NOT NULL)', []);
$db->exec('CREATE TABLE case_files (id INTEGER PRIMARY KEY AUTOINCREMENT, case_id INT NOT NULL REFERENCES cases(id), owner_id INT NOT NULL,
    file_key VARCHAR(64) NOT NULL, filename VARCHAR(255) NOT NULL, mime_type VARCHAR(100) NOT NULL, size_bytes BIGINT NOT NULL, sha256 CHAR(64) NOT NULL)', []);
$db->exec("INSERT INTO cases (owner_id, title) VALUES (1, 'mine'), (2, 'theirs')", []);

$access = ['cases' => ['user', 'admin'], 'case_files' => ['user', 'admin']];
$entities = (new DatabaseIntrospector($db))->introspect($access, [], []);
$scopes = ['owner_id' => ['self' => true, 'unscoped_roles' => ['admin']]];
$config = $buildFromEntities->invoke(null, ['access' => $access, 'scopes' => $scopes], $entities, 'sqlite');
$executor = new BatchExecutor($db, new MaskRegistry($config['masks']), new ValidatorRegistry($config['validators'] ?? []));
$executor->setScoping($config['tenancy'] ?? [], $config['scopes']);

$uploads = new UploadManager(['case_files' => [
    'storage' => ['driver' => 'local', 'path' => $dir],
    'roles' => ['user', 'admin'],
    'max_size' => 1024 * 1024,
    'mime_types' => ['application/pdf'],
    'attachments' => ['table' => 'case_files', 'fields' => ['case_id'], 'parents' => ['case_id' => 'cases']],
]]);
$svc = new AttachmentService($uploads, $executor);

$u1 = ['id' => 1, 'role' => 'user', 'user_id' => 1, 'owner_id' => 1];
$u2 = ['id' => 2, 'role' => 'user', 'user_id' => 2, 'owner_id' => 2];
$admin = ['id' => 9, 'role' => 'admin', 'user_id' => null, 'owner_id' => null];

$pdf = "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n";
$file = static function (string $name, string $bytes) use ($dir): array {
    $tmp = $dir . '/in-' . bin2hex(random_bytes(3));
    file_put_contents($tmp, $bytes);
    return ['file' => ['name' => $name, 'type' => 'application/pdf', 'tmp_name' => $tmp, 'error' => UPLOAD_ERR_OK, 'size' => strlen($bytes)]];
};

// Upload to my own case: a row is created, no URL leaks, the file is on disk under a random key.
$r = $svc->upload('case_files', $file('../../etc/passwd\".pdf', $pdf), ['case_id' => '1'], $u1);
Assert::true(is_int($r['id']) || ctype_digit((string)$r['id']));
Assert::same(hash('sha256', $pdf), $r['sha256']);
Assert::same('application/pdf', $r['mime_type']);
Assert::false(isset($r['url']));
Assert::false(str_contains($r['filename'], '/'));
Assert::false(str_contains($r['filename'], '"'));
$id = (int)$r['id'];
Assert::count(1, glob($dir . '/*.pdf'));

// Owner reads it back, byte for byte; admin sees everything.
$o = $svc->open('case_files', $id, $u1);
Assert::same($pdf, stream_get_contents($o['stream']));
fclose($o['stream']);
Assert::same(strlen($pdf), $o['size']);
$o = $svc->open('case_files', $id, $admin);
fclose($o['stream']);

// Another user gets 404 (same as missing), cannot delete either.
Assert::same('NOT_FOUND', $catch(fn() => $svc->open('case_files', $id, $u2)));
Assert::same('NOT_FOUND', $catch(fn() => $svc->delete('case_files', $id, $u2)));
Assert::same('NOT_FOUND', $catch(fn() => $svc->open('case_files', 9999, $u1)));

// Uploading to someone else's case is refused BEFORE anything is stored.
$before = count(glob($dir . '/*.pdf') ?: []);
Assert::same('NOT_FOUND', $catch(fn() => $svc->upload('case_files', $file('x.pdf', $pdf), ['case_id' => '2'], $u1)));
Assert::same($before, count(glob($dir . '/*.pdf') ?: []));

// Missing parent field, wrong mime and oversize are rejected; nothing remains on disk.
Assert::same('UPLOAD_VALIDATION_ERROR', $catch(fn() => $svc->upload('case_files', $file('x.pdf', $pdf), [], $u1)));
Assert::same('UPLOAD_VALIDATION_ERROR', $catch(fn() => $svc->upload('case_files', $file('x.pdf', "just text\n"), ['case_id' => '1'], $u1)));
Assert::same('UPLOAD_VALIDATION_ERROR', $catch(fn() => $svc->upload('case_files', $file('big.pdf', $pdf . str_repeat('a', 1024 * 1024)), ['case_id' => '1'], $u1)));
Assert::same($before, count(glob($dir . '/*.pdf') ?: []));

// Anonymous caller is rejected by the endpoint roles.
Assert::same('AUTH_FORBIDDEN', $catch(fn() => $svc->open('case_files', $id, [])));

// Delete removes the row and the file.
$svc->delete('case_files', $id, $u1);
Assert::same('NOT_FOUND', $catch(fn() => $svc->open('case_files', $id, $u1)));
Assert::count(0, glob($dir . '/*.pdf'));

// An entry without `attachments` is not served by the service.
$plain = new AttachmentService(new UploadManager(['covers' => ['storage' => ['driver' => 'local', 'path' => $dir]]]), $executor);
Assert::false($plain->isAttachmentEntry('covers'));

array_map('unlink', glob($dir . '/*') ?: []);
@rmdir($dir);
echo "OK\n";
