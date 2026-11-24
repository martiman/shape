<?php

/**
 * Standalone unit test for the SQL Server (`sqlsrv`) dialect fragments.
 * Runs without any database: SqlDialect is pure/static.
 *
 *   vendor/bin/tester tests/SqlDialect.phpt
 */

declare(strict_types=1);

use ShapeRuntime\Core\SqlDialect;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

// ── quoteIdentifier: SQL Server uses brackets, escaping a literal `]` ──
Assert::same('[id]', SqlDialect::quoteIdentifier('sqlsrv', 'id'));
Assert::same('[weird]]col]', SqlDialect::quoteIdentifier('sqlsrv', 'weird]col'));
// Other drivers unchanged.
Assert::same('"id"', SqlDialect::quoteIdentifier('pgsql', 'id'));
Assert::same('`id`', SqlDialect::quoteIdentifier('mysql', 'id'));

// ── paginate: SQL Server OFFSET/FETCH with the mandatory ORDER BY shim ──
Assert::same(
    'SELECT * FROM (SELECT 1) AS __m ORDER BY (SELECT NULL) OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY',
    SqlDialect::paginate('sqlsrv', 'SELECT 1', 5, 10)
);
// skip-only: FETCH NEXT omitted (SQL Server allows OFFSET n ROWS alone).
Assert::same(
    'SELECT * FROM (SELECT 1) AS __m ORDER BY (SELECT NULL) OFFSET 10 ROWS',
    SqlDialect::paginate('sqlsrv', 'SELECT 1', 0, 10)
);
// top-only: OFFSET 0 ROWS FETCH NEXT.
Assert::same(
    'SELECT * FROM (SELECT 1) AS __m ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY',
    SqlDialect::paginate('sqlsrv', 'SELECT 1', 5, 0)
);

// ── paginate: other drivers keep LIMIT/OFFSET (+ bigint skip-only sentinel) ──
Assert::same(
    'SELECT * FROM (SELECT 1) AS __m LIMIT 5 OFFSET 10',
    SqlDialect::paginate('mysql', 'SELECT 1', 5, 10)
);
Assert::same(
    'SELECT * FROM (SELECT 1) AS __m LIMIT 9223372036854775807 OFFSET 10',
    SqlDialect::paginate('pgsql', 'SELECT 1', 0, 10)
);

// ── insertAllocatingPk: single-statement MAX+1 allocation (MANUAL-PK-PLAN §2.1) ──
// sqlsrv: brackets, ISNULL, OUTPUT INSERTED between column list and SELECT,
// UPDLOCK/HOLDLOCK range lock on the source scan.
Assert::same(
    'INSERT INTO [invoices] ([invoice_id], [title], [amount]) OUTPUT INSERTED.[invoice_id] '
    . 'SELECT ISNULL(MAX([invoice_id]), 0) + 1, :title, :amount FROM [invoices] WITH (UPDLOCK, HOLDLOCK)',
    SqlDialect::insertAllocatingPk('sqlsrv', 'invoices', 'invoice_id', ['title', 'amount'])
);
// pgsql: double quotes, COALESCE, trailing RETURNING.
Assert::same(
    'INSERT INTO "invoices" ("id", "title") SELECT COALESCE(MAX("id"), 0) + 1, :title FROM "invoices" RETURNING "id"',
    SqlDialect::insertAllocatingPk('pgsql', 'invoices', 'id', ['title'])
);
// mysql/sqlite: backticks, COALESCE, no returning clause (the runtime
// allocates via selectNextPk + explicit insert on these drivers).
Assert::same(
    'INSERT INTO `invoices` (`id`, `title`) SELECT COALESCE(MAX(`id`), 0) + 1, :title FROM `invoices`',
    SqlDialect::insertAllocatingPk('mysql', 'invoices', 'id', ['title'])
);
Assert::same(
    'INSERT INTO `invoices` (`id`, `title`) SELECT COALESCE(MAX(`id`), 0) + 1, :title FROM `invoices`',
    SqlDialect::insertAllocatingPk('sqlite', 'invoices', 'id', ['title'])
);
// Empty-columns edge: PK-only insert stays syntactically valid.
Assert::same(
    'INSERT INTO [t] ([id]) OUTPUT INSERTED.[id] SELECT ISNULL(MAX([id]), 0) + 1 FROM [t] WITH (UPDLOCK, HOLDLOCK)',
    SqlDialect::insertAllocatingPk('sqlsrv', 't', 'id', [])
);
Assert::same(
    'INSERT INTO `t` (`id`) SELECT COALESCE(MAX(`id`), 0) + 1 FROM `t`',
    SqlDialect::insertAllocatingPk('mysql', 't', 'id', [])
);

// ── selectNextPk: locked next-id read for the drivers without in-statement return ──
Assert::same(
    'SELECT COALESCE(MAX(`id`), 0) + 1 FROM `t` FOR UPDATE',
    SqlDialect::selectNextPk('mysql', 't', 'id')
);
// sqlite has no FOR UPDATE (single-writer by design).
Assert::same(
    'SELECT COALESCE(MAX(`id`), 0) + 1 FROM `t`',
    SqlDialect::selectNextPk('sqlite', 't', 'id')
);
