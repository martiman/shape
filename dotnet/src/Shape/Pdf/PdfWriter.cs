using System.IO.Compression;
using System.Text;

namespace Shape.Pdf;

/// <summary>
/// Low-level PDF 1.4 writer: pages with content streams, embedded TrueType fonts as Type0/CIDFontType2 (Identity-H, so
/// any glyph of the font — Czech diacritics included — is addressable), cross-reference table. No dependencies.
/// Output is deterministic for the same input and <c>created</c>; with compression off it is byte-identical to the
/// PHP implementation. Mirror of the PHP class.
/// </summary>
public sealed class PdfWriter
{
    private sealed class FontEntry
    {
        public TrueTypeFont Font = null!;
        public string Name = "";
        public bool Bold;
        public SortedDictionary<int, int> Used = new();
    }

    private readonly List<byte[]> _objects = new();
    private readonly List<FontEntry> _fonts = new();
    private readonly List<string> _pages = new();
    private readonly double _pw, _ph;
    private readonly bool _compress;
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public PdfWriter(double pageWidth, double pageHeight, bool compress = true)
    {
        _pw = pageWidth;
        _ph = pageHeight;
        _compress = compress;
    }

    /// <summary>Register a font; returns its resource name (F1, F2, …).</summary>
    public string AddFont(TrueTypeFont font, bool bold)
    {
        var name = "F" + (_fonts.Count + 1);
        _fonts.Add(new FontEntry { Font = font, Name = name, Bold = bold });
        return name;
    }

    /// <summary>Remember that a glyph (for code point <paramref name="cp"/>) is used, so widths and ToUnicode cover it.</summary>
    public void UseGlyph(string fontName, int gid, int cp)
    {
        foreach (var f in _fonts)
            if (f.Name == fontName) { f.Used.TryAdd(gid, cp); return; }
    }

    public void AddPage(string content) => _pages.Add(content);

    /// <summary>Format a coordinate with at most 2 decimals, identically in every port (no locale, no float printf).</summary>
    public static string Num(double x)
    {
        var n = (long)Math.Floor(Math.Abs(x) * 100 + 0.5);
        var s = (n / 100).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (n % 100 != 0 ? "." + (n % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0') : "");
        return x < 0 && n != 0 ? "-" + s : s;
    }

    public byte[] Build(string title, string created)
    {
        _objects.Clear();
        var catalog = Reserve();
        var pages = Reserve();
        var info = Reserve();

        var refs = new List<(string Name, int Ref)>();
        foreach (var f in _fonts) refs.Add((f.Name, WriteFont(f)));
        var resources = "<< /Font << " + string.Join(" ", refs.Select(r => $"/{r.Name} {r.Ref} 0 R")) + " >> >>";

        var kids = new List<int>();
        foreach (var content in _pages)
        {
            var stream = Stream(Latin1.GetBytes(content), "");
            kids.Add(Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {Num(_pw)} {Num(_ph)}] /Resources {resources} /Contents {stream} 0 R >>"));
        }
        Set(pages, $"<< /Type /Pages /Count {kids.Count} /Kids [" + string.Join(" ", kids.Select(k => $"{k} 0 R")) + "] >>");
        Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        Set(info, "<< /Title " + TextString(title) + " /Producer (Shape) /CreationDate (D:" + created + ") >>");

        using var ms = new MemoryStream();
        void W(string s) { var b = Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < _objects.Count; i++)
        {
            offsets.Add(ms.Length);
            W($"{i + 1} 0 obj\n");
            ms.Write(_objects[i], 0, _objects[i].Length);
            W("\nendobj\n");
        }
        var xref = ms.Length;
        W($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W(o.ToString("0000000000", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n");
        W($"trailer\n<< /Size {_objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>PDF text string: ASCII literal, otherwise UTF-16BE hex with BOM.</summary>
    public static string TextString(string s)
    {
        if (s.All(c => c >= 0x20 && c <= 0x7E))
            return "(" + s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";
    }

    // ── internals ─────────────────────────────────────────────────────

    private int Reserve() { _objects.Add(Array.Empty<byte>()); return _objects.Count; }
    private int Add(string body) { _objects.Add(Latin1.GetBytes(body)); return _objects.Count; }
    private int AddBytes(byte[] body) { _objects.Add(body); return _objects.Count; }
    private void Set(int id, string body) => _objects[id - 1] = Latin1.GetBytes(body);

    private int Stream(byte[] data, string extra)
    {
        if (_compress)
        {
            using var outMs = new MemoryStream();
            using (var z = new ZLibStream(outMs, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data, 0, data.Length);
            data = outMs.ToArray();
            extra += " /Filter /FlateDecode";
        }
        var head = Latin1.GetBytes($"<< /Length {data.Length}{extra} >>\nstream\n");
        var tail = Latin1.GetBytes("\nendstream");
        var all = new byte[head.Length + data.Length + tail.Length];
        head.CopyTo(all, 0);
        data.CopyTo(all, head.Length);
        tail.CopyTo(all, head.Length + data.Length);
        return AddBytes(all);
    }

    private int WriteFont(FontEntry f)
    {
        var font = f.Font;
        var ps = f.Bold ? "DejaVuSans-Bold" : "DejaVuSans";
        int Scale(int v) => (v * 1000 + (v >= 0 ? font.UnitsPerEm / 2 : -(font.UnitsPerEm / 2))) / font.UnitsPerEm;

        var file = Stream(font.Data, " /Length1 " + font.Data.Length);
        var desc = Add("<< /Type /FontDescriptor /FontName /" + ps + " /Flags 32 /FontBBox ["
            + string.Join(" ", font.BBox.Select(Scale)) + "] /ItalicAngle 0 /Ascent " + Scale(font.Ascent)
            + " /Descent " + Scale(font.Descent) + " /CapHeight " + Scale(font.Ascent) + " /StemV " + (f.Bold ? 140 : 80)
            + $" /FontFile2 {file} 0 R >>");

        var w = new StringBuilder();
        foreach (var gid in f.Used.Keys) w.Append(gid).Append(" [").Append(font.Width(gid)).Append("] ");
        var cid = Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /" + ps + " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor " + desc + " 0 R /CIDToGIDMap /Identity /DW 600 /W [" + w.ToString().Trim() + "] >>");

        var toUni = Stream(Latin1.GetBytes(ToUnicode(f.Used)), "");
        return Add("<< /Type /Font /Subtype /Type0 /BaseFont /" + ps + " /Encoding /Identity-H /DescendantFonts [" + cid + " 0 R] /ToUnicode " + toUni + " 0 R >>");
    }

    private static string ToUnicode(SortedDictionary<int, int> used)
    {
        var sb = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var list = used.ToList();
        for (var i = 0; i < list.Count; i += 100)
        {
            var chunk = list.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (gid, cp) in chunk) sb.Append('<').Append(gid.ToString("X4")).Append("> <").Append(cp.ToString("X4")).Append(">\n");
            sb.Append("endbfchar\n");
        }
        return sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend").ToString();
    }
}
