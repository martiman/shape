using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Shape.Pdf;

/// <summary>
/// Lays a declarative document spec out on A4/Letter pages and writes the PDF. No HTML, no external tools: blocks are
/// <c>heading</c>, <c>text</c>, <c>fields</c>, <c>table</c>, <c>spacer</c>, <c>line</c>. Text uses the bundled DejaVu Sans
/// (Czech diacritics included). Spec, placeholders and formats are those of the PHP <c>Shape\Pdf\DocumentRenderer</c>
/// (see its class comment); with compression off the output is byte-identical to PHP's.
/// </summary>
public sealed class DocumentRenderer
{
    private const double LineFactor = 1.35;

    private readonly TrueTypeFont _regular;
    private readonly TrueTypeFont _bold;
    private string _locale = "cs";
    private double _pw = 595.28, _ph = 841.89, _margin = 40.0, _y, _limit;
    private readonly List<string> _pages = new();
    private string _cur = "";
    private PdfWriter _pdf = null!;
    private string _fReg = "F1", _fBold = "F2";
    private Dictionary<string, object?> _data = new();
    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)(?:\|([a-z0-9:]+))?\}", RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2}))?", RegexOptions.Compiled);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public DocumentRenderer(TrueTypeFont? regular = null, TrueTypeFont? bold = null)
    {
        _regular = regular ?? TrueTypeFont.Bundled("DejaVuSans-Subset.ttf");
        _bold = bold ?? TrueTypeFont.Bundled("DejaVuSans-Bold-Subset.ttf");
    }

    /// <param name="spec">document spec (see the PHP class comment)</param>
    /// <param name="data">header row (placeholders)</param>
    /// <param name="tableRows">rows per block index (<c>table</c> blocks)</param>
    /// <param name="created">PDF date, e.g. <c>20261126120000Z</c></param>
    public byte[] Render(Dictionary<string, object?> spec, Dictionary<string, object?> data,
        Dictionary<int, List<Dictionary<string, object?>>> tableRows, string created, bool compress = true)
    {
        _data = data;
        _locale = Str(spec, "locale", "cs") == "en" ? "en" : "cs";
        var page = AsDict(spec.GetValueOrDefault("page")) ?? new();
        double w = 595.28, h = 841.89;
        if (Str(page, "size", "A4").ToLowerInvariant() == "letter") { w = 612.0; h = 792.0; }
        if (Str(page, "orientation", "portrait").ToLowerInvariant() == "landscape") (w, h) = (h, w);
        _pw = w;
        _ph = h;
        _margin = Math.Max(20.0, Math.Min(100.0, Dbl(page, "margin", 40)));

        _pdf = new PdfWriter(w, h, compress);
        _fReg = _pdf.AddFont(_regular, false);
        _fBold = _pdf.AddFont(_bold, true);

        var footerTpl = Str(spec, "footer", "");
        _limit = h - _margin - (footerTpl != "" ? 16.0 : 0.0);
        _pages.Clear();
        NewPage();

        var blocks = AsList(spec.GetValueOrDefault("blocks"));
        for (var i = 0; i < blocks.Count; i++)
        {
            if (AsDict(blocks[i]) is not { } block) continue;
            switch (Str(block, "type", ""))
            {
                case "heading": Heading(block); break;
                case "text": TextBlock(block); break;
                case "fields": Fields(block); break;
                case "table": Table(block, tableRows.TryGetValue(i, out var rows) ? rows : new()); break;
                case "spacer": _y += Dbl(block, "height", 10); break;
                case "line": Hr(); break;
                default: throw new ArgumentException($"Unknown block type '{Str(block, "type", "")}' (heading, text, fields, table, spacer, line).");
            }
        }
        _pages.Add(_cur);

        var count = _pages.Count;
        for (var n = 0; n < count; n++)
        {
            var content = _pages[n];
            if (footerTpl != "")
            {
                var text = Interp(footerTpl, new() { ["page"] = (n + 1).ToString(Inv), ["pages"] = count.ToString(Inv) });
                content += "0.4 g " + Tj(text, false, 8.0, _margin, _ph - _margin * 0.6) + "0 g\n";
            }
            _pdf.AddPage(content);
        }
        return _pdf.Build(Interp(Str(spec, "title", "Document")), created);
    }

    /// <summary>Fill <c>{placeholders}</c> of a template (file names, titles) from a data row.</summary>
    public string Fill(string tpl, Dictionary<string, object?> data)
    {
        _data = data;
        return Interp(tpl);
    }

    // ── blocks ────────────────────────────────────────────────────────

    private void Heading(Dictionary<string, object?> b)
    {
        var size = Dbl(b, "size", 16);
        var lh = size * 1.25;
        foreach (var line in Wrap(Interp(Str(b, "text", "")), true, size, ContentWidth()))
        {
            Ensure(lh);
            _cur += Tj(line, true, size, _margin, _ph - (_y + size));
            _y += lh;
        }
        _y += size * 0.5;
    }

    private void TextBlock(Dictionary<string, object?> b)
    {
        var size = Dbl(b, "size", 10);
        var bold = b.GetValueOrDefault("bold") is true;
        var lh = size * LineFactor;
        foreach (var line in Wrap(Interp(Str(b, "text", "")), bold, size, ContentWidth()))
        {
            Ensure(lh);
            _cur += Tj(line, bold, size, _margin, _ph - (_y + size));
            _y += lh;
        }
        _y += 6;
    }

    private void Fields(Dictionary<string, object?> b)
    {
        var items = AsList(b.GetValueOrDefault("items"));
        var cols = Math.Max(1, Math.Min(4, Int(b, "columns", 2)));
        var gap = 16.0;
        var cw = (ContentWidth() - (cols - 1) * gap) / cols;
        var valueLh = 10.0 * LineFactor;

        for (var i = 0; i < items.Count; i += cols)
        {
            var row = new List<(string Label, List<string> Lines)>();
            var height = 0.0;
            for (var j = i; j < Math.Min(i + cols, items.Count); j++)
            {
                var it = AsDict(items[j]) ?? new();
                var label = Interp(Str(it, "label", ""));
                var lines = Wrap(Interp(Str(it, "value", "")), false, 10.0, cw);
                row.Add((label, lines));
                height = Math.Max(height, 8.0 * LineFactor + lines.Count * valueLh);
            }
            Ensure(height);
            for (var k = 0; k < row.Count; k++)
            {
                var x = _margin + k * (cw + gap);
                _cur += "0.4 g " + Tj(row[k].Label, false, 8.0, x, _ph - (_y + 8.0)) + "0 g\n";
                for (var n = 0; n < row[k].Lines.Count; n++)
                    _cur += Tj(row[k].Lines[n], false, 10.0, x, _ph - (_y + 8.0 * LineFactor + n * valueLh + 10.0));
            }
            _y += height + 6;
        }
    }

    private void Table(Dictionary<string, object?> b, List<Dictionary<string, object?>> rows)
    {
        var columns = AsList(b.GetValueOrDefault("columns")).Select(c => AsDict(c) ?? new()).ToList();
        if (columns.Count == 0) throw new ArgumentException("A 'table' block needs 'columns'.");
        var pad = 4.0;
        var size = 9.0;
        var lh = size * LineFactor;
        var total = 0.0;
        foreach (var c in columns) total += Math.Max(0.1, Dbl(c, "width", 1));
        var widths = new List<double>();
        var xs = new List<double>();
        var x = _margin;
        foreach (var c in columns)
        {
            var wcol = ContentWidth() * Math.Max(0.1, Dbl(c, "width", 1)) / total;
            xs.Add(x);
            widths.Add(wcol);
            x += wcol;
        }
        var align = new List<string>();
        foreach (var c in columns)
        {
            var fmt = Str(c, "format", "");
            align.Add(Str(c, "align", fmt.StartsWith("number", StringComparison.Ordinal) || fmt == "int" ? "right" : "left"));
        }

        void DrawHeader()
        {
            var h = lh + 2 * pad;
            Ensure(h + lh);
            _cur += "0.92 g " + PdfWriter.Num(_margin) + " " + PdfWriter.Num(_ph - (_y + h)) + " " + PdfWriter.Num(ContentWidth()) + " " + PdfWriter.Num(h) + " re f 0 g\n";
            for (var i = 0; i < columns.Count; i++)
            {
                var label = Str(columns[i], "label", Str(columns[i], "field", ""));
                _cur += Cell(Interp(label), true, size, xs[i], widths[i], pad, align[i], _y + pad);
            }
            _y += h;
        }

        DrawHeader();
        var sums = new Dictionary<int, double>();
        foreach (var row in rows)
        {
            var cells = new Dictionary<int, List<string>>();
            var height = 0.0;
            for (var i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                var raw = row.GetValueOrDefault(Str(c, "field", ""));
                if (c.GetValueOrDefault("sum") is true && IsNumeric(raw, out var num))
                    sums[i] = sums.GetValueOrDefault(i) + num;
                var text = FormatValue(raw, c.ContainsKey("format") && c["format"] != null ? Str(c, "format", "") : null);
                var lines = Wrap(text, false, size, widths[i] - 2 * pad);
                cells[i] = lines;
                height = Math.Max(height, lines.Count * lh);
            }
            var rowH = height + 2 * pad;
            if (_y + rowH > _limit)
            {
                NextPage();
                DrawHeader();
            }
            foreach (var (i, lines) in cells)
                for (var n = 0; n < lines.Count; n++)
                    _cur += CellLine(lines[n], false, size, xs[i], widths[i], pad, align[i], _y + pad + n * lh);
            _y += rowH;
            _cur += Rule(_margin, _margin + ContentWidth(), _y, 0.5, "0.8");
        }

        if (rows.Count == 0 && b.ContainsKey("empty") && b["empty"] != null)
        {
            Ensure(lh + 2 * pad);
            _cur += "0.4 g " + Tj(Interp(Str(b, "empty", "")), false, size, _margin + pad, _ph - (_y + pad + size)) + "0 g\n";
            _y += lh + 2 * pad;
        }

        if (sums.Count > 0)
        {
            var h = lh + 2 * pad;
            if (_y + h > _limit) NextPage();
            _cur += Rule(_margin, _margin + ContentWidth(), _y, 1.0, "0.3");
            var footer = Str(b, "footer", _locale == "en" ? "Total" : "Celkem");
            for (var i = 0; i < columns.Count; i++)
            {
                string text;
                if (sums.TryGetValue(i, out var sum))
                    text = FormatValue(sum, columns[i].ContainsKey("format") && columns[i]["format"] != null ? Str(columns[i], "format", "") : null);
                else if (i == 0)
                    text = Interp(footer);
                else
                    continue;
                _cur += Cell(text, true, size, xs[i], widths[i], pad, align[i], _y + pad);
            }
            _y += h;
        }
        _y += 8;
    }

    private void Hr()
    {
        Ensure(6.0);
        _y += 3;
        _cur += Rule(_margin, _margin + ContentWidth(), _y, 0.5, "0.6");
        _y += 3;
    }

    // ── drawing helpers ──────────────────────────────────────────────

    private string Cell(string text, bool bold, double size, double x, double w, double pad, string align, double top)
    {
        var sb = new StringBuilder();
        var lines = Wrap(text, bold, size, w - 2 * pad);
        for (var n = 0; n < lines.Count; n++)
            sb.Append(CellLine(lines[n], bold, size, x, w, pad, align, top + n * size * LineFactor));
        return sb.ToString();
    }

    private string CellLine(string line, bool bold, double size, double x, double w, double pad, string align, double top)
    {
        var tw = Width(line, bold, size);
        var tx = align switch
        {
            "right" => x + w - pad - tw,
            "center" => x + (w - tw) / 2,
            _ => x + pad,
        };
        return Tj(line, bold, size, tx, _ph - (top + size));
    }

    private string Rule(double x1, double x2, double y, double width, string gray)
    {
        var py = PdfWriter.Num(_ph - y);
        return gray + " G " + PdfWriter.Num(width) + " w " + PdfWriter.Num(x1) + " " + py + " m " + PdfWriter.Num(x2) + " " + py + " l S 0 G\n";
    }

    private double ContentWidth() => _pw - 2 * _margin;

    private void Ensure(double h)
    {
        if (_y + h > _limit) NextPage();
    }

    private void NewPage()
    {
        _cur = "0 g\n";
        _y = _margin;
    }

    private void NextPage()
    {
        _pages.Add(_cur);
        NewPage();
    }

    /// <summary>One text-showing operation at a baseline position (PDF coordinates).</summary>
    private string Tj(string text, bool bold, double size, double x, double baselineY)
    {
        var font = bold ? _bold : _regular;
        var name = bold ? _fBold : _fReg;
        var hex = new StringBuilder();
        foreach (var cp in Chars(text))
        {
            var gid = GlyphFor(font, cp);
            _pdf.UseGlyph(name, gid, cp);
            hex.Append(gid.ToString("X4"));
        }
        return "BT /" + name + " " + PdfWriter.Num(size) + " Tf " + PdfWriter.Num(x) + " " + PdfWriter.Num(baselineY) + " Td <" + hex + "> Tj ET\n";
    }

    private static int GlyphFor(TrueTypeFont font, int cp)
    {
        var gid = font.Glyph(cp);
        return gid == 0 ? font.Glyph(0x3F) : gid;   // '?' for anything the subset does not cover
    }

    private static List<int> Chars(string s)
    {
        var o = new List<int>();
        foreach (var r in s.EnumerateRunes())
        {
            var cp = r.Value;
            if (cp >= 0x20 || cp == 0x09) o.Add(cp == 0x09 ? 0x20 : cp);
        }
        return o;
    }

    private double Width(string s, bool bold, double size)
    {
        var font = bold ? _bold : _regular;
        var sum = 0;
        foreach (var cp in Chars(s)) sum += font.Width(GlyphFor(font, cp));
        return sum * size / 1000;
    }

    private static List<string> Runes(string s) => s.EnumerateRunes().Select(r => r.ToString()).ToList();

    private List<string> Wrap(string text, bool bold, double size, double maxW)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var cur = "";
            foreach (var word in para.Split(' '))
            {
                var attempt = cur == "" ? word : cur + " " + word;
                if (cur == "" || Width(attempt, bold, size) <= maxW) cur = attempt;
                else { lines.Add(cur); cur = word; }
                // a single word wider than the line: break it by characters
                while (Width(cur, bold, size) > maxW && Runes(cur).Count > 1)
                {
                    var r = Runes(cur);
                    var n = r.Count;
                    var k = 1;
                    while (k < n && Width(string.Concat(r.Take(k + 1)), bold, size) <= maxW) k++;
                    lines.Add(string.Concat(r.Take(k)));
                    cur = string.Concat(r.Skip(k));
                }
            }
            lines.Add(cur);
        }
        return lines;
    }

    // ── placeholders and formats ─────────────────────────────────────

    private string Interp(string tpl, Dictionary<string, string>? extra = null) =>
        Placeholder.Replace(tpl, m =>
            extra != null && extra.TryGetValue(m.Groups[1].Value, out var e)
                ? e
                : FormatValue(_data.GetValueOrDefault(m.Groups[1].Value), m.Groups[2].Success ? m.Groups[2].Value : null));

    private string FormatValue(object? v, string? fmt)
    {
        if (v == null) return "";
        if (!string.IsNullOrEmpty(fmt))
        {
            if (fmt.StartsWith("number", StringComparison.Ordinal) && IsNumeric(v, out var d))
            {
                var tail = fmt.Length > 7 ? fmt[7..] : "";   // number:N (N decimals); plain "number" = 2
                return Number(d, tail == "" ? 2 : LeadingInt(tail));
            }
            if (fmt == "int" && IsNumeric(v, out var di)) return Number(di, 0);
            if ((fmt == "date" || fmt == "datetime") && v is string sv) return Date(sv, fmt == "datetime");
        }
        if (v is bool bv) return bv ? (_locale == "en" ? "yes" : "ano") : (_locale == "en" ? "no" : "ne");
        return v switch
        {
            string s => s,
            IDictionary or IEnumerable => System.Text.Json.JsonSerializer.Serialize(v, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            IFormattable f => f.ToString(null, Inv),
            _ => v.ToString() ?? "",
        };
    }

    /// <summary>Leading digits as an int (PHP <c>(int)"2x"</c> semantics), 0 when there are none.</summary>
    private static int LeadingInt(string t)
    {
        var m = Regex.Match(t, @"^\s*[+-]?\d+");
        return m.Success && int.TryParse(m.Value, NumberStyles.Integer, Inv, out var n) ? n : 0;
    }

    private string Number(double v, int dec)
    {
        dec = Math.Max(0, Math.Min(6, dec));
        var pow = 1L;
        for (var i = 0; i < dec; i++) pow *= 10;
        var scaled = (long)Math.Floor(Math.Abs(v) * pow + 0.5);
        var intPart = scaled / pow;
        var frac = dec > 0 ? (scaled % pow).ToString(Inv).PadLeft(dec, '0') : "";
        var (group, point) = _locale == "en" ? (",", ".") : (" ", ",");
        var digits = intPart.ToString(Inv);
        var sb = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(group);
            sb.Append(digits[i]);
        }
        var neg = v < 0 && scaled != 0 ? "-" : "";
        return neg + sb + (dec > 0 ? point + frac : "");
    }

    private string Date(string v, bool time)
    {
        var m = DateRx.Match(v);
        if (!m.Success) return v;
        var d = _locale == "en"
            ? $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}"
            : int.Parse(m.Groups[3].Value, Inv) + ". " + int.Parse(m.Groups[2].Value, Inv) + ". " + m.Groups[1].Value;
        return time && m.Groups[4].Success ? d + " " + m.Groups[4].Value + ":" + m.Groups[5].Value : d;
    }

    // ── spec access (PHP-array-like semantics) ───────────────────────

    private static bool IsNumeric(object? v, out double d)
    {
        switch (v)
        {
            case int i: d = i; return true;
            case long l: d = l; return true;
            case double x: d = x; return true;
            case float f: d = f; return true;
            case decimal m: d = (double)m; return true;
            case string s: return double.TryParse(s.Trim(), NumberStyles.Float, Inv, out d);
            default: d = 0; return false;
        }
    }

    private static string Str(Dictionary<string, object?> d, string key, string def)
    {
        var v = d.GetValueOrDefault(key);
        return v switch
        {
            null => def,
            string s => s,
            bool b => b ? "1" : "",
            IFormattable f => f.ToString(null, Inv),
            _ => v.ToString() ?? def,
        };
    }

    private static double Dbl(Dictionary<string, object?> d, string key, double def) =>
        d.TryGetValue(key, out var v) && v != null && IsNumeric(v, out var x) ? x : def;

    private static int Int(Dictionary<string, object?> d, string key, int def) =>
        d.TryGetValue(key, out var v) && v != null && IsNumeric(v, out var x) ? (int)x : def;

    private static Dictionary<string, object?>? AsDict(object? o) => o switch
    {
        Dictionary<string, object?> d => d,
        IDictionary<string, object?> d => new Dictionary<string, object?>(d),
        _ => null,
    };

    private static List<object?> AsList(object? o) =>
        o is IEnumerable e and not string and not IDictionary ? e.Cast<object?>().ToList() : new List<object?>();
}
