namespace Shape.Pdf;

/// <summary>
/// Just enough of a TrueType parser to embed a (subsetted) font in a PDF: glyph lookup through a format-4
/// <c>cmap</c>, advance widths from <c>hmtx</c> and the few metrics a PDF font descriptor needs. The font bytes are
/// embedded as-is, so use a small subset (the bundled DejaVu subsets are ~23 kB). Mirror of the PHP class.
/// </summary>
public sealed class TrueTypeFont
{
    public byte[] Data { get; }
    public int UnitsPerEm { get; }
    public int Ascent { get; }
    public int Descent { get; }
    public int[] BBox { get; }

    private readonly int _numHMetrics;
    private readonly int _hmtxOffset;
    private readonly int _glyphCount;
    private readonly List<int> _end = new(), _start = new(), _delta = new(), _rangeOffset = new(), _glyphIds = new();
    private int _segCount;
    private readonly Dictionary<int, int> _cache = new();

    public TrueTypeFont(byte[] data)
    {
        Data = data;
        var tables = ReadTables();
        foreach (var t in new[] { "head", "hhea", "hmtx", "maxp", "cmap", "glyf", "loca" })
            if (!tables.ContainsKey(t)) throw new InvalidOperationException($"Font is missing the '{t}' table.");

        var head = tables["head"];
        UnitsPerEm = U16(head + 18);
        BBox = new[] { I16(head + 36), I16(head + 38), I16(head + 40), I16(head + 42) };
        var hhea = tables["hhea"];
        Ascent = I16(hhea + 4);
        Descent = I16(hhea + 6);
        _numHMetrics = U16(hhea + 34);
        _hmtxOffset = tables["hmtx"];
        _glyphCount = U16(tables["maxp"] + 4);
        ParseCmap(tables["cmap"]);
    }

    /// <summary>Load one of the bundled fonts (<c>DejaVuSans-Subset.ttf</c>, <c>DejaVuSans-Bold-Subset.ttf</c>).</summary>
    public static TrueTypeFont Bundled(string fileName)
    {
        using var s = typeof(TrueTypeFont).Assembly.GetManifestResourceStream("Shape.Pdf.Fonts." + fileName)
            ?? throw new InvalidOperationException($"Bundled font '{fileName}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new TrueTypeFont(ms.ToArray());
    }

    /// <summary>Glyph id for a Unicode code point (0 = .notdef).</summary>
    public int Glyph(int cp)
    {
        if (_cache.TryGetValue(cp, out var cached)) return cached;
        var gid = 0;
        if (cp <= 0xFFFF)
        {
            for (var i = 0; i < _segCount; i++)
            {
                if (cp > _end[i]) continue;
                if (cp >= _start[i])
                {
                    if (_rangeOffset[i] == 0) gid = (cp + _delta[i]) & 0xFFFF;
                    else
                    {
                        var idx = _rangeOffset[i] / 2 + (cp - _start[i]) - (_segCount - i);
                        var g = idx >= 0 && idx < _glyphIds.Count ? _glyphIds[idx] : 0;
                        gid = g == 0 ? 0 : (g + _delta[i]) & 0xFFFF;
                    }
                }
                break;
            }
        }
        return _cache[cp] = gid < _glyphCount ? gid : 0;
    }

    /// <summary>Advance width of a glyph in 1/1000 em, rounded to an integer.</summary>
    public int Width(int gid)
    {
        var i = gid < _numHMetrics ? gid : _numHMetrics - 1;
        var adv = U16(_hmtxOffset + i * 4);
        return (adv * 1000 + UnitsPerEm / 2) / UnitsPerEm;
    }

    private Dictionary<string, int> ReadTables()
    {
        var n = U16(4);
        var map = new Dictionary<string, int>();
        for (var i = 0; i < n; i++)
        {
            var o = 12 + i * 16;
            map[System.Text.Encoding.ASCII.GetString(Data, o, 4)] = (int)U32(o + 8);
        }
        return map;
    }

    private void ParseCmap(int cmapOffset)
    {
        var n = U16(cmapOffset + 2);
        int sub = -1;
        for (var i = 0; i < n; i++)
        {
            var rec = cmapOffset + 4 + i * 8;
            var platform = U16(rec);
            var encoding = U16(rec + 2);
            var off = cmapOffset + (int)U32(rec + 4);
            if (U16(off) == 4 && ((platform == 3 && encoding == 1) || platform == 0))
            {
                sub = off;
                if (platform == 3) break;
            }
        }
        if (sub < 0) throw new InvalidOperationException("Font has no format-4 Unicode cmap.");

        _segCount = U16(sub + 6) / 2;
        var p = sub + 14;
        for (var i = 0; i < _segCount; i++) _end.Add(U16(p + i * 2));
        p += _segCount * 2 + 2;
        for (var i = 0; i < _segCount; i++) _start.Add(U16(p + i * 2));
        p += _segCount * 2;
        for (var i = 0; i < _segCount; i++) _delta.Add(I16(p + i * 2));
        p += _segCount * 2;
        for (var i = 0; i < _segCount; i++) _rangeOffset.Add(U16(p + i * 2));
        p += _segCount * 2;
        var end = sub + U16(sub + 2);
        for (var o = p; o + 1 < end; o += 2) _glyphIds.Add(U16(o));
    }

    private int U16(int o) => (Data[o] << 8) | Data[o + 1];
    private int I16(int o) { var v = U16(o); return v >= 0x8000 ? v - 0x10000 : v; }
    private uint U32(int o) => ((uint)U16(o) << 16) | (uint)U16(o + 2);
}
