<?php

declare(strict_types=1);

namespace Shape\Pdf;

use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;

/**
 * PDF documents from masks. The data comes from masks run in the CALLER's context, so row scopes, tenancy and
 * mask roles decide what a document may contain — a user can only print what they could read. A document the
 * caller may not see answers 404, exactly like a missing one.
 *
 * Config (`documents` namespace; spec keys are those of {@see DocumentRenderer}):
 *
 *   'documents' => [
 *       'invoice' => [
 *           'mask'     => 'invoices.get',            // first row = header data for {placeholders}
 *           'params'   => ['id'],                    // query-string parameters accepted and passed to the mask(s)
 *           'roles'    => ['admin', 'user'],         // optional extra role gate
 *           'filename' => 'faktura-{number}.pdf',
 *           'title'    => 'Faktura {number}',
 *           'locale'   => 'cs',
 *           'footer'   => '{number} · strana {page} / {pages}',
 *           'blocks'   => [
 *               ['type' => 'heading', 'text' => 'Faktura {number}'],
 *               ['type' => 'table', 'mask' => 'invoice_items.byInvoiceId', 'params' => ['invoice_id' => '{id}'],
 *                'columns' => [['field' => 'name', 'label' => 'Položka', 'width' => 5],
 *                              ['field' => 'total', 'label' => 'Cena', 'format' => 'number:2', 'sum' => true]]],
 *           ],
 *       ],
 *   ],
 *
 * Route (wired by ShapeRouter): GET /api.php/pdf/<name>?id=5
 */
final class DocumentService
{
    public const MAX_TABLE_ROWS = 2000;

    /** @param array<string, array<string, mixed>> $documents */
    public function __construct(
        private readonly BatchExecutor $executor,
        private readonly array $documents,
        private readonly ?DocumentRenderer $renderer = null,
    ) {
    }

    /** @return list<string> */
    public function names(): array
    {
        return array_keys($this->documents);
    }

    /**
     * @param array<string, mixed> $query request parameters (only the declared `params` are used)
     * @param array<string, mixed> $authContext
     * @return array{pdf:string, filename:string}
     */
    public function render(string $name, array $query, array $authContext, ?string $created = null, bool $compress = true): array
    {
        $spec = $this->documents[$name] ?? null;
        if (!is_array($spec)) {
            throw new ApiException(
                'NOT_FOUND',
                "Document '{$name}' is not configured.",
                404,
                null,
                ['expected' => $this->documents === [] ? 'No documents are configured for this app.' : 'one of: ' . implode(', ', array_keys($this->documents)), 'hint' => "Documents are declared under config['documents']['<name>']."]
            );
        }
        $this->authorizeRole($spec, $authContext);

        $declared = is_array($spec['params'] ?? null) ? array_values(array_map('strval', $spec['params'])) : [];
        $params = [];
        foreach ($declared as $p) {
            if (!array_key_exists($p, $query) || $query[$p] === '' || is_array($query[$p])) {
                throw new ApiException(
                    'DOCUMENT_VALIDATION_ERROR',
                    "Query parameter '{$p}' is required.",
                    400,
                    null,
                    ['expected' => ['query' => implode(', ', $declared)], 'hint' => "Call GET /api.php/pdf/{$name}?" . implode('&', array_map(static fn(string $x): string => "{$x}=<value>", $declared))]
                );
            }
            $params[$p] = $query[$p];
        }

        $mask = (string)($spec['mask'] ?? '');
        if ($mask === '') {
            throw new ApiException('DOCUMENT_CONFIG_ERROR', "Document '{$name}' needs a 'mask'.", 500);
        }
        $headerRows = $this->run($mask, $params, $authContext);
        if ($headerRows === []) {
            throw new ApiException('NOT_FOUND', 'Document not found.', 404);   // missing or not visible: same answer
        }
        $data = $headerRows[0] + $params;

        $tableRows = [];
        $blocks = is_array($spec['blocks'] ?? null) ? array_values($spec['blocks']) : [];
        foreach ($blocks as $i => $block) {
            if (!is_array($block) || ($block['type'] ?? '') !== 'table') {
                continue;
            }
            if (!isset($block['mask'])) {
                $tableRows[$i] = is_array($block['rows'] ?? null) ? array_values($block['rows']) : [];
                continue;
            }
            $bp = [];
            foreach (is_array($block['params'] ?? null) ? $block['params'] : [] as $k => $v) {
                $bp[(string)$k] = is_string($v) && preg_match('/^\{([A-Za-z_][A-Za-z0-9_]*)\}$/', $v, $m) === 1 ? ($data[$m[1]] ?? null) : $v;
            }
            $rows = $this->run((string)$block['mask'], $bp, $authContext);
            if (count($rows) > self::MAX_TABLE_ROWS) {
                throw new ApiException('DOCUMENT_TOO_LARGE', 'The table has more than ' . self::MAX_TABLE_ROWS . ' rows.', 413, null, ['hint' => 'Narrow the mask (period, status) so the document stays printable.']);
            }
            $tableRows[$i] = $rows;
        }

        $renderer = $this->renderer ?? new DocumentRenderer();
        try {
            $pdf = $renderer->render($spec, $data, $tableRows, $created ?? gmdate('YmdHis') . 'Z', $compress);
        } catch (\InvalidArgumentException $e) {
            throw new ApiException('DOCUMENT_CONFIG_ERROR', $e->getMessage(), 500);
        }
        $file = $renderer->fill((string)($spec['filename'] ?? $name . '.pdf'), $data);
        $file = trim((string)preg_replace('/[^A-Za-z0-9._-]+/', '_', $file), '_');
        if ($file === '' || !str_ends_with(strtolower($file), '.pdf')) {
            $file = ($file === '' ? $name : $file) . '.pdf';
        }
        return ['pdf' => $pdf, 'filename' => $file];
    }

    /**
     * @param array<string, mixed> $params
     * @param array<string, mixed> $authContext
     * @return list<array<string, mixed>>
     */
    private function run(string $mask, array $params, array $authContext): array
    {
        $r = $this->executor->execute([['ref' => 'doc', 'sql' => $mask, 'params' => $params]], $authContext)['results'][0] ?? [];
        $cols = is_array($r['meta']['columns'] ?? null) ? $r['meta']['columns'] : [];
        $rows = [];
        foreach (is_array($r['rows'] ?? null) ? $r['rows'] : [] as $row) {
            $rows[] = array_combine($cols, $row);
        }
        return $rows;
    }

    /**
     * @param array<string, mixed> $spec
     * @param array<string, mixed> $authContext
     */
    private function authorizeRole(array $spec, array $authContext): void
    {
        $allowed = is_array($spec['roles'] ?? null) ? $spec['roles'] : [];
        if ($allowed === []) {
            return;
        }
        $role = isset($authContext['role']) ? (string)$authContext['role'] : '';
        if ($role === '' || !in_array($role, $allowed, true)) {
            throw new ApiException(
                'AUTH_FORBIDDEN',
                'Current role is not allowed for this document.',
                403,
                null,
                ['expected' => ['role' => 'one of: ' . implode(', ', $allowed)], 'hint' => $role === '' ? 'Authenticate first via POST {op:\'auth.token\'}.' : "Your role '{$role}' cannot print this document."]
            );
        }
    }
}
