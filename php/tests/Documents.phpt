<?php

/**
 * PDF documents: declarative layout (Czech text, tables, pagination), data from masks in the caller's context
 * (row scopes), and a golden file shared with the .NET port (byte-identical output).
 *
 *   vendor/bin/tester tests/Documents.phpt
 */

declare(strict_types=1);

use Shape\Pdf\DocumentRenderer;
use Shape\Pdf\DocumentService;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\MaskRegistry;
use ShapeRuntime\Core\ValidatorRegistry;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$catch = static function (callable $fn): string {
    try {
        $fn();
    } catch (ApiException $e) {
        return $e->apiCode();
    }
    return '(none)';
};

/** Structural PDF check: every xref entry points at "<n> 0 obj", startxref points at the table. */
$assertValidPdf = static function (string $pdf): void {
    Assert::same('%PDF-1.4', substr($pdf, 0, 8));
    Assert::same("%%EOF\n", substr($pdf, -6));
    Assert::true(preg_match('/startxref\n(\d+)\n%%EOF/', $pdf, $m) === 1);
    Assert::same('xref', substr($pdf, (int)$m[1], 4));
    Assert::true(preg_match('/xref\n0 (\d+)\n/', $pdf, $c, 0, (int)$m[1]) === 1);
    $n = (int)$c[1];
    $table = substr($pdf, (int)$m[1] + strlen("xref\n0 {$n}\n"));
    for ($i = 1; $i < $n; $i++) {
        $entry = substr($table, $i * 20, 20);
        $offset = (int)substr($entry, 0, 10);
        Assert::same("{$i} 0 obj", substr($pdf, $offset, strlen("{$i} 0 obj")));
    }
};

// ── golden file shared with the .NET port: same input => byte-identical PDF ─────────────────────────
$j = json_decode((string)file_get_contents(__DIR__ . '/golden/document-sample.json'), true);
$tables = [];
foreach ($j['tableRows'] as $k => $v) {
    $tables[(int)$k] = $v;
}
$golden = (new DocumentRenderer())->render($j['spec'], $j['data'], $tables, $j['created'], false);
Assert::same((string)file_get_contents(__DIR__ . '/golden/document-sample.pdf'), $golden);
$assertValidPdf($golden);
Assert::true(preg_match('#/Count 2#', $golden) === 1);   // 30 wrapped rows paginate onto two pages

// compressed output is valid too, deterministic, and much smaller
$packed = (new DocumentRenderer())->render($j['spec'], $j['data'], $tables, $j['created'], true);
$assertValidPdf($packed);
Assert::same($packed, (new DocumentRenderer())->render($j['spec'], $j['data'], $tables, $j['created'], true));
Assert::true(strlen($packed) < strlen($golden) / 2);

// ── formats and placeholders ─────────────────────────────────────────────────────────────────────────
$r = new DocumentRenderer();
$d = ['total' => 1234567.891, 'neg' => -0.004, 'day' => '2026-11-26 14:05:00', 'ok' => true, 'none' => null];
Assert::same("1\u{00A0}234\u{00A0}567,89", $r->fill('{total|number:2}', $d));
Assert::same("1\u{00A0}234\u{00A0}568", $r->fill('{total|int}', $d));
Assert::same('0,00', $r->fill('{neg|number:2}', $d));                 // no "-0,00"
Assert::same('26. 11. 2026', $r->fill('{day|date}', $d));
Assert::same('26. 11. 2026 14:05', $r->fill('{day|datetime}', $d));
Assert::same('ano||', $r->fill('{ok}|{none}|{missing}', $d));
$en = new DocumentRenderer();
$en->render(['locale' => 'en', 'blocks' => []], [], [], '20260101000000Z');
Assert::same('1,234,567.89', $en->fill('{total|number:2}', $d));
Assert::same('2026-11-26', $en->fill('{day|date}', $d));

// ── layout errors are explicit ───────────────────────────────────────────────────────────────────────
Assert::exception(static fn() => (new DocumentRenderer())->render(['blocks' => [['type' => 'nope']]], [], [], '20260101000000Z'), InvalidArgumentException::class);
Assert::exception(static fn() => (new DocumentRenderer())->render(['blocks' => [['type' => 'table']]], [], [], '20260101000000Z'), InvalidArgumentException::class);

// ── service: data from masks in the caller's context ────────────────────────────────────────────────
$tmp = sys_get_temp_dir() . '/shape-doc-' . bin2hex(random_bytes(4)) . '.sqlite';
$db = new Database(['dsn' => 'sqlite:' . $tmp]);
$db->exec('CREATE TABLE invoices (id INTEGER PRIMARY KEY AUTOINCREMENT, owner_id INT NOT NULL, number TEXT NOT NULL, customer TEXT NOT NULL)', []);
$db->exec('CREATE TABLE invoice_items (id INTEGER PRIMARY KEY AUTOINCREMENT, invoice_id INT NOT NULL, owner_id INT NOT NULL, name TEXT NOT NULL, total REAL NOT NULL)', []);
$db->exec("INSERT INTO invoices (owner_id, number, customer) VALUES (1, '2026/001', 'Žluťoučký s.r.o.'), (2, '2026/002', 'Other')", []);
$db->exec("INSERT INTO invoice_items (invoice_id, owner_id, name, total) VALUES (1, 1, 'Práce', 1000.5), (1, 1, 'Materiál', 249.5), (2, 2, 'Secret', 99), (1, 2, 'Leak', 5)", []);
$scopes = ['owner_id' => ['self' => true, 'unscoped_roles' => ['admin']]];
$mk = static fn(string $sql, array $params): array => [
    'sql' => $sql . ' AND owner_id = :__scope_owner_id', 'sql_unscoped' => $sql, 'params' => $params,
    'roles' => ['admin', 'user'], 'row_scopes' => ['owner_id'], 'description' => 'x',
];
$masks = [
    'invoices.get' => $mk('SELECT id, number, customer FROM invoices WHERE id = :id', ['id' => ['type' => 'int', 'required' => true]]),
    'invoice_items.byInvoice' => $mk('SELECT name, total FROM invoice_items WHERE invoice_id = :invoice_id', ['invoice_id' => ['type' => 'int', 'required' => true]]),
];
$executor = new BatchExecutor($db, new MaskRegistry($masks), new ValidatorRegistry([]));
$executor->setScoping([], $scopes);
$docs = [
    'invoice' => [
        'mask' => 'invoices.get', 'params' => ['id'], 'roles' => ['admin', 'user'],
        'filename' => 'faktura {number}.pdf', 'title' => 'Faktura {number} – {customer}', 'footer' => '{number} {page}/{pages}',
        'blocks' => [
            ['type' => 'heading', 'text' => 'Faktura {number}'],
            ['type' => 'fields', 'items' => [['label' => 'Odběratel', 'value' => '{customer}']]],
            ['type' => 'table', 'mask' => 'invoice_items.byInvoice', 'params' => ['invoice_id' => '{id}'],
                'columns' => [['field' => 'name', 'label' => 'Položka', 'width' => 3], ['field' => 'total', 'label' => 'Cena', 'format' => 'number:2', 'sum' => true]]],
        ],
    ],
];
$svc = new DocumentService($executor, $docs);
$u1 = ['id' => 1, 'role' => 'user', 'owner_id' => 1];
$u2 = ['id' => 2, 'role' => 'user', 'owner_id' => 2];
$admin = ['id' => 9, 'role' => 'admin', 'owner_id' => null];

$out = $svc->render('invoice', ['id' => '1', 'ignored' => 'x'], $u1, '20261126120000Z');
$assertValidPdf($out['pdf']);
Assert::same('faktura_2026_001.pdf', $out['filename']);            // placeholder filled, unsafe characters replaced
Assert::true(str_contains($out['pdf'], '/Title <FEFF'));             // non-ASCII title is UTF-16 encoded

// the document only shows what the caller may see: items of another owner never appear
$font = Shape\Pdf\TrueTypeFont::fromFile(dirname(__DIR__) . '/packages/shape/resources/fonts/DejaVuSans-Subset.ttf');
$hex = static function (string $s) use ($font): string {
    $h = '';
    foreach (preg_split('//u', $s, -1, PREG_SPLIT_NO_EMPTY) as $ch) {
        $h .= sprintf('%04X', $font->glyph(mb_ord($ch, 'UTF-8')));
    }
    return $h;
};
$plain = $svc->render('invoice', ['id' => '1'], $u1, '20261126120000Z', false)['pdf'];
Assert::true(str_contains($plain, $hex('Práce')));                  // own item is printed ...
Assert::false(str_contains($plain, $hex('Leak')));                  // ... a row of another owner on the SAME invoice is not
Assert::true(str_contains($svc->render('invoice', ['id' => '1'], $admin, '20261126120000Z', false)['pdf'], $hex('Leak')));   // unscoped role sees it
Assert::true(str_contains($svc->render('invoice', ['id' => '2'], $admin, '20261126120000Z', false)['pdf'], $hex('Secret')));
$adminPdf = $svc->render('invoice', ['id' => '2'], $admin, '20261126120000Z');
$assertValidPdf($adminPdf['pdf']);
Assert::same('NOT_FOUND', $catch(fn() => $svc->render('invoice', ['id' => '2'], $u1)));      // someone else's invoice == missing
Assert::same('NOT_FOUND', $catch(fn() => $svc->render('invoice', ['id' => '999'], $u1)));
Assert::same('NOT_FOUND', $catch(fn() => $svc->render('nope', ['id' => '1'], $u1)));
Assert::same('DOCUMENT_VALIDATION_ERROR', $catch(fn() => $svc->render('invoice', [], $u1)));  // required param missing
Assert::same('DOCUMENT_VALIDATION_ERROR', $catch(fn() => $svc->render('invoice', ['id' => ['1']], $u1)));
Assert::same('AUTH_FORBIDDEN', $catch(fn() => $svc->render('invoice', ['id' => '1'], [])));    // anonymous

// a table that would produce a huge PDF is refused instead of silently truncated
$db->exec('BEGIN', []);
for ($i = 0; $i < DocumentService::MAX_TABLE_ROWS + 1; $i++) {
    $db->exec('INSERT INTO invoice_items (invoice_id, owner_id, name, total) VALUES (1, 1, :n, 1)', ['n' => 'x' . $i]);
}
$db->exec('COMMIT', []);
Assert::same('DOCUMENT_TOO_LARGE', $catch(fn() => $svc->render('invoice', ['id' => '1'], $u1)));

// misconfigured block type becomes a config error, not a crash
$bad = new DocumentService($executor, ['x' => ['mask' => 'invoices.get', 'params' => ['id'], 'blocks' => [['type' => 'nope']]]]);
Assert::same('DOCUMENT_CONFIG_ERROR', $catch(fn() => $bad->render('x', ['id' => '1'], $u1)));

@unlink($tmp);
echo "OK\n";
