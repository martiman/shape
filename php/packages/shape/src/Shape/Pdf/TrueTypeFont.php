<?php

declare(strict_types=1);

namespace Shape\Pdf;

/**
 * Just enough of a TrueType parser to embed a (subsetted) font in a PDF: glyph lookup through a format-4
 * `cmap`, advance widths from `hmtx`, and the few metrics a PDF font descriptor needs. The font bytes are
 * embedded as-is, so use a small subset (the bundled DejaVu subsets are ~23 kB).
 */
final class TrueTypeFont
{
    public readonly int $unitsPerEm;
    public readonly int $ascent;
    public readonly int $descent;
    /** @var array{0:int,1:int,2:int,3:int} xMin, yMin, xMax, yMax in font units */
    public readonly array $bbox;

    private readonly int $numHMetrics;
    private readonly int $hmtxOffset;
    private readonly int $glyphCount;
    /** @var list<int> */
    private array $endCode = [];
    /** @var list<int> */
    private array $startCode = [];
    /** @var list<int> */
    private array $idDelta = [];
    /** @var list<int> */
    private array $idRangeOffset = [];
    /** @var list<int> */
    private array $glyphIdArray = [];
    private int $segCount = 0;
    /** @var array<int, int> */
    private array $gidCache = [];

    public function __construct(public readonly string $data)
    {
        $tables = $this->readTables();
        foreach (['head', 'hhea', 'hmtx', 'maxp', 'cmap', 'glyf', 'loca'] as $t) {
            if (!isset($tables[$t])) {
                throw new \RuntimeException("Font is missing the '{$t}' table.");
            }
        }
        $head = $tables['head'][0];
        $this->unitsPerEm = $this->u16($head + 18);
        $this->bbox = [$this->i16($head + 36), $this->i16($head + 38), $this->i16($head + 40), $this->i16($head + 42)];
        $hhea = $tables['hhea'][0];
        $this->ascent = $this->i16($hhea + 4);
        $this->descent = $this->i16($hhea + 6);
        $this->numHMetrics = $this->u16($hhea + 34);
        $this->hmtxOffset = $tables['hmtx'][0];
        $this->glyphCount = $this->u16($tables['maxp'][0] + 4);
        $this->parseCmap($tables['cmap'][0]);
    }

    public static function fromFile(string $path): self
    {
        $d = @file_get_contents($path);
        if ($d === false) {
            throw new \RuntimeException("Cannot read font file '{$path}'.");
        }
        return new self($d);
    }

    /** Glyph id for a Unicode code point (0 = .notdef). */
    public function glyph(int $cp): int
    {
        if (isset($this->gidCache[$cp])) {
            return $this->gidCache[$cp];
        }
        $gid = 0;
        if ($cp <= 0xFFFF) {
            for ($i = 0; $i < $this->segCount; $i++) {
                if ($cp > $this->endCode[$i]) {
                    continue;
                }
                if ($cp >= $this->startCode[$i]) {
                    if ($this->idRangeOffset[$i] === 0) {
                        $gid = ($cp + $this->idDelta[$i]) & 0xFFFF;
                    } else {
                        $idx = intdiv($this->idRangeOffset[$i], 2) + ($cp - $this->startCode[$i]) - ($this->segCount - $i);
                        $g = $this->glyphIdArray[$idx] ?? 0;
                        $gid = $g === 0 ? 0 : ($g + $this->idDelta[$i]) & 0xFFFF;
                    }
                }
                break;
            }
        }
        return $this->gidCache[$cp] = $gid < $this->glyphCount ? $gid : 0;
    }

    /** Advance width of a glyph in 1/1000 em (PDF text space), rounded to an integer. */
    public function width(int $gid): int
    {
        $i = $gid < $this->numHMetrics ? $gid : $this->numHMetrics - 1;
        $adv = $this->u16($this->hmtxOffset + $i * 4);
        return intdiv($adv * 1000 + intdiv($this->unitsPerEm, 2), $this->unitsPerEm);
    }

    /** @return array<string, array{0:int,1:int}> tag => [offset, length] */
    private function readTables(): array
    {
        $n = $this->u16(4);
        $out = [];
        for ($i = 0; $i < $n; $i++) {
            $o = 12 + $i * 16;
            $out[substr($this->data, $o, 4)] = [$this->u32($o + 8), $this->u32($o + 12)];
        }
        return $out;
    }

    private function parseCmap(int $cmapOffset): void
    {
        $n = $this->u16($cmapOffset + 2);
        $sub = null;
        for ($i = 0; $i < $n; $i++) {
            $rec = $cmapOffset + 4 + $i * 8;
            $platform = $this->u16($rec);
            $encoding = $this->u16($rec + 2);
            $off = $cmapOffset + $this->u32($rec + 4);
            if ($this->u16($off) === 4 && (($platform === 3 && $encoding === 1) || $platform === 0)) {
                $sub = $off;
                if ($platform === 3) {
                    break;
                }
            }
        }
        if ($sub === null) {
            throw new \RuntimeException('Font has no format-4 Unicode cmap.');
        }
        $this->segCount = intdiv($this->u16($sub + 6), 2);
        $p = $sub + 14;
        for ($i = 0; $i < $this->segCount; $i++) {
            $this->endCode[] = $this->u16($p + $i * 2);
        }
        $p += $this->segCount * 2 + 2;
        for ($i = 0; $i < $this->segCount; $i++) {
            $this->startCode[] = $this->u16($p + $i * 2);
        }
        $p += $this->segCount * 2;
        for ($i = 0; $i < $this->segCount; $i++) {
            $this->idDelta[] = $this->i16($p + $i * 2);
        }
        $p += $this->segCount * 2;
        for ($i = 0; $i < $this->segCount; $i++) {
            $this->idRangeOffset[] = $this->u16($p + $i * 2);
        }
        $p += $this->segCount * 2;
        $end = $sub + $this->u16($sub + 2);
        for ($o = $p; $o + 1 < $end; $o += 2) {
            $this->glyphIdArray[] = $this->u16($o);
        }
    }

    private function u16(int $o): int
    {
        return (ord($this->data[$o]) << 8) | ord($this->data[$o + 1]);
    }

    private function i16(int $o): int
    {
        $v = $this->u16($o);
        return $v >= 0x8000 ? $v - 0x10000 : $v;
    }

    private function u32(int $o): int
    {
        return ($this->u16($o) << 16) | $this->u16($o + 2);
    }
}
