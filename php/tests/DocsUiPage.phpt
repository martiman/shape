<?php

/**
 * Docs UI page (GET /api.php?docs) — mirror of dotnet DocsUiPageTests: the
 * page is self-contained, points the Scalar viewer at ?openapi, carries the
 * token-prompt wiring, honors a self-hosted asset override, and escapes
 * operator-supplied values.
 *
 *   vendor/bin/tester tests/DocsUiPage.phpt
 */

declare(strict_types=1);

use Shape\DocsUiPage;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$html = DocsUiPage::render('My.Service', '/api.php?openapi');

Assert::true(str_starts_with($html, '<!doctype html>'));
Assert::contains('My.Service — API reference', $html);
Assert::contains('"/api.php?openapi"', $html);              // JSON-encoded spec URL in the JS
Assert::contains(DocsUiPage::DEFAULT_ASSET_URL, $html);     // Scalar CDN default
Assert::contains('shape_docs_token', $html);                // token prompt/retry wiring
Assert::contains('createApiReference', $html);

// Self-hosted bundle override + escaping of operator-supplied values.
$custom = DocsUiPage::render('A<B&C', '/api.php?openapi', 'https://intranet.local/scalar.js');
Assert::contains('https://intranet.local/scalar.js', $custom);
Assert::notContains(DocsUiPage::DEFAULT_ASSET_URL, $custom);
Assert::contains('A&lt;B&amp;C', $custom);                  // HTML-encoded title
