<?php

declare(strict_types=1);

namespace Shape\Pdf;

/**
 * Lays a declarative document spec out on A4/Letter pages and writes the PDF. No HTML, no external tools:
 * blocks are `heading`, `text`, `fields`, `table`, `spacer`, `line`. Text uses the bundled DejaVu Sans (Czech
 * diacritics included). Output is deterministic for the same input and `$created`.
 *
 * Spec:
 *   'title'  => 'Invoice {number}',
 *   'page'   => ['size' => 'A4' | 'Letter', 'orientation' => 'portrait' | 'landscape', 'margin' => 40],
 *   'locale' => 'cs' | 'en',                         // number and date formatting
 *   'footer' => '{number}   page {page} / {pages}',
 *   'blocks' => [
 *       ['type' => 'heading', 'text' => 'Invoice {number}', 'size' => 18],
 *       ['type' => 'fields', 'columns' => 2, 'items' => [['label' => 'Customer', 'value' => '{customer}']]],
 *       ['type' => 'text', 'text' => 'Due {due_date|date}', 'bold' => false, 'size' => 10],
 *       ['type' => 'table', 'columns' => [['field' => 'name', 'label' => 'Item', 'width' => 4],
 *                                         ['field' => 'total', 'label' => 'Total', 'format' => 'number:2', 'sum' => true]],
 *        'footer' => 'Total', 'empty' => 'No items'],
 *       ['type' => 'spacer', 'height' => 12], ['type' => 'line'],
 *   ]
 *
 * Placeholders: `{field}` or `{field|format}` (formats: number:N, int, date, datetime) read the document's data
 * row; `{page}` / `{pages}` work in the footer. Unknown placeholders render empty.
 */
final class DocumentRenderer
{
    private const LINE_FACTOR = 1.35;

    private TrueTypeFont $regular;
    private TrueTypeFont $bold;
    private string $locale = 'cs';
    private float $pw = 595.28;
    private float $ph = 841.89;
    private float $margin = 40.0;
    private float $y = 0.0;
    private float $limit = 0.0;
    /** @var list<string> */
    private array $pages = [];
    private string $cur = '';
    private PdfWriter $pdf;
    private string $fReg = 'F1';
    private string $fBold = 'F2';
    /** @var array<string, array<string, mixed>> */
    private array $data = [];

    public function __construct(?TrueTypeFont $regular = null, ?TrueTypeFont $bold = null)
    {
        $dir = dirname(__DIR__, 3) . '/resources/fonts/';
        $this->regular = $regular ?? TrueTypeFont::fromFile($dir . 'DejaVuSans-Subset.ttf');
        $this->bold = $bold ?? TrueTypeFont::fromFile($dir . 'DejaVuSans-Bold-Subset.ttf');
    }

    /**
     * @param array<string, mixed> $spec
     * @param array<string, mixed> $data header row (placeholders)
     * @param array<int, list<array<string, mixed>>> $tableRows rows per block index (`table` blocks)
     * @param string $created PDF date, e.g. 20261126120000Z
     */
    public function render(array $spec, array $data, array $tableRows, string $created, bool $compress = true): string
    {
        $this->data = $data;
        $this->locale = (string)($spec['locale'] ?? 'cs') === 'en' ? 'en' : 'cs';
        $page = is_array($spec['page'] ?? null) ? $spec['page'] : [];
        [$w, $h] = strtolower((string)($page['size'] ?? 'A4')) === 'letter' ? [612.0, 792.0] : [595.28, 841.89];
        if (strtolower((string)($page['orientation'] ?? 'portrait')) === 'landscape') {
            [$w, $h] = [$h, $w];
        }
        $this->pw = $w;
        $this->ph = $h;
        $this->margin = max(20.0, min(100.0, (float)($page['margin'] ?? 40)));

        $this->pdf = new PdfWriter($w, $h, $compress);
        $this->fReg = $this->pdf->addFont($this->regular, false);
        $this->fBold = $this->pdf->addFont($this->bold, true);

        $footerTpl = (string)($spec['footer'] ?? '');
        $this->limit = $h - $this->margin - ($footerTpl !== '' ? 16.0 : 0.0);
        $this->pages = [];
        $this->newPage();

        foreach (is_array($spec['blocks'] ?? null) ? array_values($spec['blocks']) : [] as $i => $block) {
            if (!is_array($block)) {
                continue;
            }
            match ((string)($block['type'] ?? '')) {
                'heading' => $this->heading($block),
                'text' => $this->textBlock($block),
                'fields' => $this->fields($block),
                'table' => $this->table($block, $tableRows[$i] ?? []),
                'spacer' => $this->y += (float)($block['height'] ?? 10),
                'line' => $this->hr(),
                default => throw new \InvalidArgumentException("Unknown block type '" . (string)($block['type'] ?? '') . "' (heading, text, fields, table, spacer, line)."),
            };
        }
        $this->pages[] = $this->cur;

        $count = count($this->pages);
        foreach ($this->pages as $n => $content) {
            if ($footerTpl !== '') {
                $text = $this->interp($footerTpl, ['page' => (string)($n + 1), 'pages' => (string)$count]);
                $content .= '0.4 g ' . $this->tj($text, false, 8.0, $this->margin, $this->ph - $this->margin * 0.6) . "0 g\n";
            }
            $this->pdf->addPage($content);
        }
        return $this->pdf->build($this->interp((string)($spec['title'] ?? 'Document')), $created);
    }

    /**
     * Fill `{placeholders}` of a template (file names, titles) from a data row.
     *
     * @param array<string, mixed> $data
     */
    public function fill(string $tpl, array $data): string
    {
        $this->data = $data;
        return $this->interp($tpl);
    }

    // ── blocks ────────────────────────────────────────────────────────

    /** @param array<string, mixed> $b */
    private function heading(array $b): void
    {
        $size = (float)($b['size'] ?? 16);
        $lh = $size * 1.25;
        foreach ($this->wrap($this->interp((string)($b['text'] ?? '')), true, $size, $this->contentWidth()) as $line) {
            $this->ensure($lh);
            $this->cur .= $this->tj($line, true, $size, $this->margin, $this->ph - ($this->y + $size));
            $this->y += $lh;
        }
        $this->y += $size * 0.5;
    }

    /** @param array<string, mixed> $b */
    private function textBlock(array $b): void
    {
        $size = (float)($b['size'] ?? 10);
        $bold = ($b['bold'] ?? false) === true;
        $lh = $size * self::LINE_FACTOR;
        foreach ($this->wrap($this->interp((string)($b['text'] ?? '')), $bold, $size, $this->contentWidth()) as $line) {
            $this->ensure($lh);
            $this->cur .= $this->tj($line, $bold, $size, $this->margin, $this->ph - ($this->y + $size));
            $this->y += $lh;
        }
        $this->y += 6;
    }

    /** @param array<string, mixed> $b */
    private function fields(array $b): void
    {
        $items = is_array($b['items'] ?? null) ? array_values($b['items']) : [];
        $cols = max(1, min(4, (int)($b['columns'] ?? 2)));
        $gap = 16.0;
        $cw = ($this->contentWidth() - ($cols - 1) * $gap) / $cols;
        $valueLh = 10.0 * self::LINE_FACTOR;

        for ($i = 0; $i < count($items); $i += $cols) {
            $row = [];
            $height = 0.0;
            for ($j = $i; $j < min($i + $cols, count($items)); $j++) {
                $it = is_array($items[$j]) ? $items[$j] : [];
                $label = $this->interp((string)($it['label'] ?? ''));
                $lines = $this->wrap($this->interp((string)($it['value'] ?? '')), false, 10.0, $cw);
                $row[] = [$label, $lines];
                $height = max($height, 8.0 * self::LINE_FACTOR + count($lines) * $valueLh);
            }
            $this->ensure($height);
            foreach ($row as $k => [$label, $lines]) {
                $x = $this->margin + $k * ($cw + $gap);
                $this->cur .= '0.4 g ' . $this->tj($label, false, 8.0, $x, $this->ph - ($this->y + 8.0)) . "0 g\n";
                foreach ($lines as $n => $line) {
                    $this->cur .= $this->tj($line, false, 10.0, $x, $this->ph - ($this->y + 8.0 * self::LINE_FACTOR + $n * $valueLh + 10.0));
                }
            }
            $this->y += $height + 6;
        }
    }

    /**
     * @param array<string, mixed> $b
     * @param list<array<string, mixed>> $rows
     */
    private function table(array $b, array $rows): void
    {
        $columns = is_array($b['columns'] ?? null) ? array_values($b['columns']) : [];
        if ($columns === []) {
            throw new \InvalidArgumentException("A 'table' block needs 'columns'.");
        }
        $pad = 4.0;
        $size = 9.0;
        $lh = $size * self::LINE_FACTOR;
        $total = 0.0;
        foreach ($columns as $c) {
            $total += max(0.1, (float)($c['width'] ?? 1));
        }
        $widths = [];
        $xs = [];
        $x = $this->margin;
        foreach ($columns as $c) {
            $wcol = $this->contentWidth() * max(0.1, (float)($c['width'] ?? 1)) / $total;
            $xs[] = $x;
            $widths[] = $wcol;
            $x += $wcol;
        }
        $align = [];
        foreach ($columns as $c) {
            $fmt = (string)($c['format'] ?? '');
            $align[] = (string)($c['align'] ?? (str_starts_with($fmt, 'number') || $fmt === 'int' ? 'right' : 'left'));
        }

        $header = static fn(): array => array_map(static fn(array $c): string => (string)($c['label'] ?? $c['field'] ?? ''), $columns);
        $drawHeader = function () use ($widths, $xs, $align, $pad, $size, $lh, $header): void {
            $labels = $header();
            $h = $lh + 2 * $pad;
            $this->ensure($h + $lh);
            $this->cur .= '0.92 g ' . PdfWriter::num($this->margin) . ' ' . PdfWriter::num($this->ph - ($this->y + $h)) . ' ' . PdfWriter::num($this->contentWidth()) . ' ' . PdfWriter::num($h) . " re f 0 g\n";
            foreach ($labels as $i => $label) {
                $this->cur .= $this->cell($this->interp($label), true, $size, $xs[$i], $widths[$i], $pad, $align[$i], $this->y + $pad);
            }
            $this->y += $h;
        };

        $drawHeader();
        $sums = [];
        foreach ($rows as $row) {
            $cells = [];
            $height = 0.0;
            foreach ($columns as $i => $c) {
                $raw = $row[(string)($c['field'] ?? '')] ?? null;
                if (($c['sum'] ?? false) === true && is_numeric($raw)) {
                    $sums[$i] = ($sums[$i] ?? 0.0) + (float)$raw;
                }
                $text = $this->formatValue($raw, isset($c['format']) ? (string)$c['format'] : null);
                $lines = $this->wrap($text, false, $size, $widths[$i] - 2 * $pad);
                $cells[$i] = $lines;
                $height = max($height, count($lines) * $lh);
            }
            $rowH = $height + 2 * $pad;
            if ($this->y + $rowH > $this->limit) {
                $this->nextPage();
                $drawHeader();
            }
            foreach ($cells as $i => $lines) {
                foreach ($lines as $n => $line) {
                    $this->cur .= $this->cellLine($line, false, $size, $xs[$i], $widths[$i], $pad, $align[$i], $this->y + $pad + $n * $lh);
                }
            }
            $this->y += $rowH;
            $this->cur .= $this->rule($this->margin, $this->margin + $this->contentWidth(), $this->y, 0.5, '0.8');
        }

        if ($rows === [] && isset($b['empty'])) {
            $this->ensure($lh + 2 * $pad);
            $this->cur .= '0.4 g ' . $this->tj($this->interp((string)$b['empty']), false, $size, $this->margin + $pad, $this->ph - ($this->y + $pad + $size)) . "0 g\n";
            $this->y += $lh + 2 * $pad;
        }

        if ($sums !== []) {
            $h = $lh + 2 * $pad;
            if ($this->y + $h > $this->limit) {
                $this->nextPage();
            }
            $this->cur .= $this->rule($this->margin, $this->margin + $this->contentWidth(), $this->y, 1.0, '0.3');
            $footer = (string)($b['footer'] ?? ($this->locale === 'en' ? 'Total' : 'Celkem'));
            foreach ($columns as $i => $c) {
                if (isset($sums[$i])) {
                    $text = $this->formatValue($sums[$i], isset($c['format']) ? (string)$c['format'] : null);
                } elseif ($i === 0) {
                    $text = $this->interp($footer);
                } else {
                    continue;
                }
                $this->cur .= $this->cell($text, true, $size, $xs[$i], $widths[$i], $pad, $align[$i], $this->y + $pad);
            }
            $this->y += $h;
        }
        $this->y += 8;
    }

    private function hr(): void
    {
        $this->ensure(6.0);
        $this->y += 3;
        $this->cur .= $this->rule($this->margin, $this->margin + $this->contentWidth(), $this->y, 0.5, '0.6');
        $this->y += 3;
    }

    // ── drawing helpers ──────────────────────────────────────────────

    private function cell(string $text, bool $bold, float $size, float $x, float $w, float $pad, string $align, float $top): string
    {
        $out = '';
        foreach ($this->wrap($text, $bold, $size, $w - 2 * $pad) as $n => $line) {
            $out .= $this->cellLine($line, $bold, $size, $x, $w, $pad, $align, $top + $n * $size * self::LINE_FACTOR);
        }
        return $out;
    }

    private function cellLine(string $line, bool $bold, float $size, float $x, float $w, float $pad, string $align, float $top): string
    {
        $tw = $this->width($line, $bold, $size);
        $tx = match ($align) {
            'right' => $x + $w - $pad - $tw,
            'center' => $x + ($w - $tw) / 2,
            default => $x + $pad,
        };
        return $this->tj($line, $bold, $size, $tx, $this->ph - ($top + $size));
    }

    private function rule(float $x1, float $x2, float $y, float $width, string $gray): string
    {
        $py = PdfWriter::num($this->ph - $y);
        return "{$gray} G " . PdfWriter::num($width) . ' w ' . PdfWriter::num($x1) . " {$py} m " . PdfWriter::num($x2) . " {$py} l S 0 G\n";
    }

    private function contentWidth(): float
    {
        return $this->pw - 2 * $this->margin;
    }

    private function ensure(float $h): void
    {
        if ($this->y + $h > $this->limit) {
            $this->nextPage();
        }
    }

    private function newPage(): void
    {
        $this->cur = "0 g\n";
        $this->y = $this->margin;
    }

    private function nextPage(): void
    {
        $this->pages[] = $this->cur;
        $this->newPage();
    }

    /** One text-showing operation at a baseline position (PDF coordinates). */
    private function tj(string $text, bool $bold, float $size, float $x, float $baselineY): string
    {
        $font = $bold ? $this->bold : $this->regular;
        $name = $bold ? $this->fBold : $this->fReg;
        $hex = '';
        foreach ($this->chars($text) as $cp) {
            $gid = $this->glyphFor($font, $cp);
            $this->pdf->useGlyph($name, $gid, $cp);
            $hex .= sprintf('%04X', $gid);
        }
        return 'BT /' . $name . ' ' . PdfWriter::num($size) . ' Tf ' . PdfWriter::num($x) . ' ' . PdfWriter::num($baselineY) . ' Td <' . $hex . "> Tj ET\n";
    }

    private function glyphFor(TrueTypeFont $font, int $cp): int
    {
        $gid = $font->glyph($cp);
        if ($gid === 0) {
            $gid = $font->glyph(0x3F);   // '?' for anything the subset does not cover
        }
        return $gid;
    }

    /** @return list<int> */
    private function chars(string $s): array
    {
        $out = [];
        foreach (preg_split('//u', $s, -1, PREG_SPLIT_NO_EMPTY) ?: [] as $ch) {
            $cp = mb_ord($ch, 'UTF-8');
            if ($cp >= 0x20 || $cp === 0x09) {
                $out[] = $cp === 0x09 ? 0x20 : $cp;
            }
        }
        return $out;
    }

    private function width(string $s, bool $bold, float $size): float
    {
        $font = $bold ? $this->bold : $this->regular;
        $sum = 0;
        foreach ($this->chars($s) as $cp) {
            $sum += $font->width($this->glyphFor($font, $cp));
        }
        return $sum * $size / 1000;
    }

    /** @return list<string> */
    private function wrap(string $text, bool $bold, float $size, float $maxW): array
    {
        $lines = [];
        foreach (explode("\n", str_replace("\r", '', $text)) as $para) {
            $cur = '';
            foreach (explode(' ', $para) as $word) {
                $try = $cur === '' ? $word : $cur . ' ' . $word;
                if ($cur === '' || $this->width($try, $bold, $size) <= $maxW) {
                    $cur = $try;
                } else {
                    $lines[] = $cur;
                    $cur = $word;
                }
                // a single word wider than the line: break it by characters
                while ($this->width($cur, $bold, $size) > $maxW && mb_strlen($cur) > 1) {
                    $n = mb_strlen($cur);
                    $k = 1;
                    while ($k < $n && $this->width(mb_substr($cur, 0, $k + 1), $bold, $size) <= $maxW) {
                        $k++;
                    }
                    $lines[] = mb_substr($cur, 0, $k);
                    $cur = mb_substr($cur, $k);
                }
            }
            $lines[] = $cur;
        }
        return $lines;
    }

    // ── placeholders and formats ─────────────────────────────────────

    /** @param array<string, string> $extra */
    private function interp(string $tpl, array $extra = []): string
    {
        return (string)preg_replace_callback(
            '/\{([A-Za-z_][A-Za-z0-9_]*)(?:\|([a-z0-9:]+))?\}/',
            fn(array $m): string => array_key_exists($m[1], $extra)
                ? $extra[$m[1]]
                : $this->formatValue($this->data[$m[1]] ?? null, $m[2] ?? null),
            $tpl
        );
    }

    private function formatValue(mixed $v, ?string $fmt): string
    {
        if ($v === null) {
            return '';
        }
        if ($fmt !== null && $fmt !== '') {
            if (str_starts_with($fmt, 'number') && is_numeric($v)) {
                $tail = substr($fmt, 7);   // number:N (N decimals); plain "number" = 2
                return $this->number((float)$v, $tail === '' ? 2 : (int)$tail);
            }
            if ($fmt === 'int' && is_numeric($v)) {
                return $this->number((float)$v, 0);
            }
            if (($fmt === 'date' || $fmt === 'datetime') && is_string($v)) {
                return $this->date($v, $fmt === 'datetime');
            }
        }
        if (is_bool($v)) {
            return $v ? ($this->locale === 'en' ? 'yes' : 'ano') : ($this->locale === 'en' ? 'no' : 'ne');
        }
        return is_scalar($v) ? (string)$v : (string)json_encode($v, JSON_UNESCAPED_UNICODE);
    }

    private function number(float $v, int $dec): string
    {
        $dec = max(0, min(6, $dec));
        $scaled = (int)floor(abs($v) * (10 ** $dec) + 0.5);
        $int = intdiv($scaled, 10 ** $dec);
        $frac = $dec > 0 ? str_pad((string)($scaled % (10 ** $dec)), $dec, '0', STR_PAD_LEFT) : '';
        [$group, $point] = $this->locale === 'en' ? [',', '.'] : ["\u{00A0}", ','];
        $digits = (string)$int;
        $out = '';
        for ($i = 0, $n = strlen($digits); $i < $n; $i++) {
            if ($i > 0 && ($n - $i) % 3 === 0) {
                $out .= $group;
            }
            $out .= $digits[$i];
        }
        $neg = $v < 0 && $scaled !== 0 ? '-' : '';
        return $neg . $out . ($dec > 0 ? $point . $frac : '');
    }

    private function date(string $v, bool $time): string
    {
        if (preg_match('/^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2}))?/', $v, $m) !== 1) {
            return $v;
        }
        $d = $this->locale === 'en' ? "{$m[1]}-{$m[2]}-{$m[3]}" : ((int)$m[3]) . '. ' . ((int)$m[2]) . '. ' . $m[1];
        return $time && isset($m[4]) ? $d . ' ' . $m[4] . ':' . $m[5] : $d;
    }
}
