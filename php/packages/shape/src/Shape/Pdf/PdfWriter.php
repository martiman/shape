<?php

declare(strict_types=1);

namespace Shape\Pdf;

/**
 * Low-level PDF 1.4 writer: pages with content streams, embedded TrueType fonts as Type0/CIDFontType2
 * (Identity-H, so any glyph of the font — Czech diacritics included — is addressable), cross-reference table.
 * No external dependencies. Output is deterministic for the same input and `$created`.
 */
final class PdfWriter
{
    /** @var list<string> object bodies, index 0 = object 1 */
    private array $objects = [];
    /** @var list<array{font:TrueTypeFont, name:string, bold:bool, used:array<int,int>}> */
    private array $fonts = [];
    /** @var list<string> */
    private array $pageContents = [];

    public function __construct(
        private readonly float $pageWidth,
        private readonly float $pageHeight,
        private readonly bool $compress = true,
    ) {
    }

    /** Register a font; returns its resource name (F1, F2, …). */
    public function addFont(TrueTypeFont $font, bool $bold): string
    {
        $name = 'F' . (count($this->fonts) + 1);
        $this->fonts[] = ['font' => $font, 'name' => $name, 'bold' => $bold, 'used' => []];
        return $name;
    }

    /** Remember that a glyph (for code point $cp) is used, so widths and ToUnicode cover it. */
    public function useGlyph(string $fontName, int $gid, int $cp): void
    {
        foreach ($this->fonts as $i => $f) {
            if ($f['name'] === $fontName) {
                $this->fonts[$i]['used'][$gid] ??= $cp;
                return;
            }
        }
    }

    public function addPage(string $content): void
    {
        $this->pageContents[] = $content;
    }

    /** Format a coordinate with at most 2 decimals, identically in every port (no locale, no float printf). */
    public static function num(float $x): string
    {
        $n = (int)floor(abs($x) * 100 + 0.5);
        $s = intdiv($n, 100) . ($n % 100 !== 0 ? '.' . rtrim(sprintf('%02d', $n % 100), '0') : '');
        return $x < 0 && $n !== 0 ? '-' . $s : $s;
    }

    public function build(string $title, string $created): string
    {
        $this->objects = [];
        $catalog = $this->reserve();   // 1
        $pages = $this->reserve();     // 2
        $info = $this->reserve();      // 3

        $fontRefs = [];
        foreach ($this->fonts as $f) {
            $fontRefs[$f['name']] = $this->writeFont($f);
        }
        $resources = '<< /Font << ' . implode(' ', array_map(static fn(string $n, int $r): string => "/{$n} {$r} 0 R", array_keys($fontRefs), $fontRefs)) . ' >> >>';

        $kids = [];
        foreach ($this->pageContents as $content) {
            $stream = $this->stream($content);
            $kids[] = $this->add("<< /Type /Page /Parent {$pages} 0 R /MediaBox [0 0 " . self::num($this->pageWidth) . ' ' . self::num($this->pageHeight) . "] /Resources {$resources} /Contents {$stream} 0 R >>");
        }
        $this->set($pages, '<< /Type /Pages /Count ' . count($kids) . ' /Kids [' . implode(' ', array_map(static fn(int $k): string => "{$k} 0 R", $kids)) . '] >>');
        $this->set($catalog, "<< /Type /Catalog /Pages {$pages} 0 R >>");
        $this->set($info, '<< /Title ' . self::textString($title) . ' /Producer (Shape) /CreationDate (D:' . $created . ') >>');

        $out = "%PDF-1.4\n%\xE2\xE3\xCF\xD3\n";
        $offsets = [];
        foreach ($this->objects as $i => $body) {
            $offsets[$i] = strlen($out);
            $out .= ($i + 1) . " 0 obj\n" . $body . "\nendobj\n";
        }
        $xref = strlen($out);
        $out .= "xref\n0 " . (count($this->objects) + 1) . "\n0000000000 65535 f \n";
        foreach ($offsets as $o) {
            $out .= sprintf("%010d 00000 n \n", $o);
        }
        $out .= "trailer\n<< /Size " . (count($this->objects) + 1) . " /Root {$catalog} 0 R /Info {$info} 0 R >>\nstartxref\n{$xref}\n%%EOF\n";
        return $out;
    }

    /** PDF text string: ASCII literal, otherwise UTF-16BE hex with BOM. */
    public static function textString(string $s): string
    {
        if (preg_match('/^[\x20-\x7E]*$/', $s) === 1) {
            return '(' . strtr($s, ['\\' => '\\\\', '(' => '\\(', ')' => '\\)']) . ')';
        }
        return '<FEFF' . strtoupper(bin2hex((string)mb_convert_encoding($s, 'UTF-16BE', 'UTF-8'))) . '>';
    }

    // ── internals ─────────────────────────────────────────────────────

    private function reserve(): int
    {
        $this->objects[] = '';
        return count($this->objects);
    }

    private function add(string $body): int
    {
        $this->objects[] = $body;
        return count($this->objects);
    }

    private function set(int $id, string $body): void
    {
        $this->objects[$id - 1] = $body;
    }

    private function stream(string $data, string $extra = ''): int
    {
        if ($this->compress) {
            $data = (string)gzcompress($data, 9);
            $extra .= ' /Filter /FlateDecode';
        }
        return $this->add('<< /Length ' . strlen($data) . $extra . " >>\nstream\n" . $data . "\nendstream");
    }

    /** @param array{font:TrueTypeFont, name:string, bold:bool, used:array<int,int>} $f */
    private function writeFont(array $f): int
    {
        $font = $f['font'];
        $ps = $f['bold'] ? 'DejaVuSans-Bold' : 'DejaVuSans';
        $scale = static fn(int $v): int => intdiv($v * 1000 + ($v >= 0 ? intdiv($font->unitsPerEm, 2) : -intdiv($font->unitsPerEm, 2)), $font->unitsPerEm);

        $file = $this->streamRaw($font->data, ' /Length1 ' . strlen($font->data));
        $desc = $this->add('<< /Type /FontDescriptor /FontName /' . $ps . ' /Flags 32 /FontBBox ['
            . implode(' ', array_map($scale, $font->bbox)) . '] /ItalicAngle 0 /Ascent ' . $scale($font->ascent)
            . ' /Descent ' . $scale($font->descent) . ' /CapHeight ' . $scale($font->ascent) . ' /StemV ' . ($f['bold'] ? 140 : 80)
            . " /FontFile2 {$file} 0 R >>");

        ksort($f['used']);
        $w = '';
        foreach (array_keys($f['used']) as $gid) {
            $w .= $gid . ' [' . $font->width($gid) . '] ';
        }
        $cid = $this->add('<< /Type /Font /Subtype /CIDFontType2 /BaseFont /' . $ps . ' /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor ' . $desc . ' 0 R /CIDToGIDMap /Identity /DW 600 /W [' . trim($w) . '] >>');

        $toUni = $this->stream($this->toUnicode($f['used']));
        return $this->add('<< /Type /Font /Subtype /Type0 /BaseFont /' . $ps . ' /Encoding /Identity-H /DescendantFonts [' . $cid . ' 0 R] /ToUnicode ' . $toUni . ' 0 R >>');
    }

    /** Font program is always stored compressed (when enabled) — never re-encoded. */
    private function streamRaw(string $data, string $extra): int
    {
        return $this->stream($data, $extra);
    }

    /** @param array<int,int> $used gid => code point */
    private function toUnicode(array $used): string
    {
        $cmap = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n";
        foreach (array_chunk($used, 100, true) as $chunk) {
            $cmap .= count($chunk) . " beginbfchar\n";
            foreach ($chunk as $gid => $cp) {
                $cmap .= sprintf("<%04X> <%04X>\n", $gid, $cp);
            }
            $cmap .= "endbfchar\n";
        }
        return $cmap . "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";
    }
}
