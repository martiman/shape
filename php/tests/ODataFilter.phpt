<?php

/**
 * OData $filter compiled to SQL (ShapeRuntime\Core\ODataFilter) and applied by BatchExecutor
 * before pagination and COUNT. Regression: the filter used to run in PHP after LIMIT, so only
 * the first rows (default limit 100) were searched and $count ignored the filter.
 *
 *   vendor/bin/tester tests/ODataFilter.phpt
 */

declare(strict_types=1);

use Shape\Metadata\DatabaseIntrospector;
use Shape\ShapeApp;
use Shape\ShapeRouter;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\MaskRegistry;
use ShapeRuntime\Core\ODataFilter;
use ShapeRuntime\Core\ValidatorRegistry;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$q = static fn(string $c): string => '"' . $c . '"';
$catch = static function (callable $fn): array {
    try {
        $fn();
    } catch (ApiException $e) {
        return ['code' => $e->apiCode(), 'status' => $e->status(), 'message' => $e->getMessage(), 'details' => $e->details()];
    }
    return ['code' => '(none)', 'status' => 0, 'message' => '', 'details' => []];
};

// ── Compiler: SQL shape and bound values ──────────────────────────────────
$c = ODataFilter::compile('seconds ge 3600', $q);
Assert::same('"seconds" >= :__f0', $c['sql']);
Assert::same(['__f0' => 3600], $c['params']);

// precedence: not > and > or, parentheses override
$c = ODataFilter::compile('a eq 1 or b eq 2 and c eq 3', $q);
Assert::same('("a" = :__f0 OR ("b" = :__f1 AND "c" = :__f2))', $c['sql']);
$c = ODataFilter::compile('(a eq 1 or b eq 2) and not c eq 3', $q);
Assert::same('(("a" = :__f0 OR "b" = :__f1) AND (NOT "c" = :__f2))', $c['sql']);

// strings, escaped quotes, floats, booleans, null, dates
$c = ODataFilter::compile("name eq 'it''s' and price lt 9.5 and done eq true", $q);
Assert::same(['__f0' => "it's", '__f1' => 9.5, '__f2' => 1], $c['params']);
Assert::same('"x" IS NULL', ODataFilter::compile('x eq null', $q)['sql']);
Assert::same('"x" IS NOT NULL', ODataFilter::compile('x ne null', $q)['sql']);
Assert::same(['__f0' => '2026-11-20 10:00:00'], ODataFilter::compile('d ge 2026-11-20T10:00:00Z', $q)['params']);
Assert::same(['__f0' => '2026-11-20'], ODataFilter::compile('d ge 2026-11-20', $q)['params']);
Assert::same(['__f0' => '2026-11-20'], ODataFilter::compile("d ge '2026-11-20'", $q)['params']);

// functions escape LIKE wildcards
$c = ODataFilter::compile("contains(note,'50%_x')", $q);
Assert::same('"note" LIKE :__f0 ESCAPE \'!\'', $c['sql']);
Assert::same(['__f0' => '%50!%!_x%'], $c['params']);
Assert::same(['__f0' => 'ab%'], ODataFilter::compile("startswith(n,'ab')", $q)['params']);
Assert::same(['__f0' => '%ab'], ODataFilter::compile("endswith(n,'ab')", $q)['params']);
Assert::same(['n', 'note'], ODataFilter::compile("n eq 1 and contains(note,'x') and n gt 0", $q)['columns']);

// invalid / hostile input → QUERY_VALIDATION_ERROR, never SQL
foreach ([
    "n eq 1; DROP TABLE ft", 'n eq 1 or 1=1', 'na"me eq 1', "n eq 'open", 'n eq', 'n', 'n gt null', '', '(n eq 1', 'n eq 1)',
    'n xx 1', "contains(n)", 'contains(n, 5)', 'n eq abc', str_repeat('(', 30) . 'n eq 1' . str_repeat(')', 30),
    str_repeat('n eq 1 and ', 60) . 'n eq 1', str_repeat('x', 2100),
] as $bad) {
    $err = $catch(fn() => ODataFilter::compile($bad, $q));
    Assert::same('QUERY_VALIDATION_ERROR', $err['code'], 'filter: ' . substr($bad, 0, 40));
    Assert::same(400, $err['status']);
    Assert::true(isset($err['details']['hint']));
}

// router keeps $filter as a raw string for the executor
$parse = new ReflectionMethod(ShapeRouter::class, 'parseODataQuery');
$parse->setAccessible(true);
Assert::same('n gt 5', $parse->invoke(null, '$filter=n%20gt%205&$top=3')['filter']);
Assert::null($parse->invoke(null, '$top=3')['filter']);

// ── Executor: filter applies before LIMIT and COUNT ───────────────────────
$db = new Database(['dsn' => 'sqlite::memory:']);
$db->exec('CREATE TABLE ft (id INTEGER PRIMARY KEY AUTOINCREMENT, owner INT NOT NULL, n INT NOT NULL, grp VARCHAR(5) NOT NULL, name VARCHAR(30), note VARCHAR(30))', []);
for ($i = 1; $i <= 350; $i++) {
    $db->exec('INSERT INTO ft (owner, n, grp, name, note) VALUES (:o, :n, :g, :nm, :nt)', [
        'o' => $i % 2 === 0 ? 1 : 2, 'n' => $i, 'g' => $i % 2 === 0 ? 'a' : 'b', 'nm' => "row $i", 'nt' => $i % 10 === 0 ? null : 'it\'s ' . $i,
    ]);
}
$buildFromEntities = new ReflectionMethod(ShapeApp::class, 'buildFromEntities');
$buildFromEntities->setAccessible(true);
$access = ['ft' => ['user', 'admin']];
$entities = (new DatabaseIntrospector($db))->introspect($access, [], []);

$build = static function (array $extra = []) use ($buildFromEntities, $entities, $db, $access): BatchExecutor {
    $config = $buildFromEntities->invoke(null, ['access' => $access] + $extra, $entities, 'sqlite');
    $ex = new BatchExecutor($db, new MaskRegistry($config['masks']), new ValidatorRegistry($config['validators'] ?? []));
    if (isset($config['scopes'])) {
        $ex->setScoping($config['tenancy'] ?? [], $config['scopes']);
    }
    return $ex;
};
$executor = $build();
$run = static function (BatchExecutor $ex, string $filter, array $options = [], array $ctx = ['role' => 'admin']): array {
    $query = ['ref' => 'q', 'sql' => 'ft.list', 'params' => []] + ($options !== [] ? ['options' => $options] : []);
    return $ex->execute([$query], $ctx, $filter === '' ? [] : ['q' => $filter])['results'][0];
};

// default limit is 100 rows: matches beyond the first 100 must still be found
$r = $run($executor, 'n gt 300');
Assert::same(50, $r['count']);                       // 301..350 (before the fix: 0, the filter only saw rows 1..100)
$r = $run($executor, 'n gt 300', ['top' => 10, 'count' => true]);
Assert::same(10, $r['count']);
Assert::same(50, $r['total_count']);                 // $count honours the filter

$r = $run($executor, "grp eq 'a'", ['top' => 200, 'count' => true]);
Assert::same(175, $r['total_count']);
Assert::same(175, $r['count']);
$r = $run($executor, "grp eq 'a'", ['top' => 100, 'skip' => 100, 'count' => true]);
Assert::same(75, $r['count']);                       // second page of the filtered set

// and / or / not / parentheses / null / functions
Assert::same(10, $run($executor, 'n ge 341 and n le 350')['count']);
Assert::same(10, $run($executor, "(n le 10 or n gt 340) and grp eq 'a'")['count']);   // even n in 1..10 and 341..350
Assert::same(35, $run($executor, 'note eq null')['count']);                  // every 10th row
Assert::same(100, $run($executor, 'not note eq null', ['top' => 100])['count']);
Assert::same(1, $run($executor, "name eq 'row 7'")['count']);
Assert::same(11, $run($executor, "startswith(name,'row 30')")['count']);               // row 30, row 300..309
Assert::same(1, $run($executor, "contains(note,'it''s 123')")['count']);
Assert::same(0, $run($executor, 'n eq 5 and n eq 6')['count']);

// the filter cannot widen a row scope: owner is the caller's own id
$scoped = $build(['scopes' => ['owner' => ['self' => true, 'unscoped_roles' => ['admin']]]]);
$user1 = ['id' => 1, 'role' => 'user', 'owner' => 1];
Assert::same(175, $run($scoped, '', ['top' => 200], $user1)['count']);
Assert::same(0, $run($scoped, 'owner eq 2', ['top' => 200], $user1)['count']);       // other user's rows stay invisible
Assert::same(175, $run($scoped, 'owner eq 2', ['top' => 200, 'count' => true])['total_count']); // admin sees them
Assert::same(10, $run($scoped, 'n gt 330', ['top' => 200], $user1)['count']);        // scope AND filter

// unknown column → client error that lists the available columns
$err = $catch(fn() => $run($executor, 'zzz eq 1'));
Assert::same('QUERY_VALIDATION_ERROR', $err['code']);
Assert::contains('zzz', $err['message']);
Assert::true(in_array('grp', $err['details']['available'] ?? [], true));

// clients cannot smuggle a filter through query options
$err = $catch(fn() => $executor->execute([['ref' => 'q', 'sql' => 'ft.list', 'params' => [], 'options' => ['filter' => 'n gt 1']]], ['role' => 'admin']));
Assert::same('QUERY_VALIDATION_ERROR', $err['code']);

echo "OK\n";
