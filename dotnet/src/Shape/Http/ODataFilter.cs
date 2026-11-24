using System.Globalization;
using System.Text;
using Shape.Core;

namespace Shape.Http;

/// <summary>
/// Compiles a subset of OData <c>$filter</c> syntax into a safe parameterised
/// SQL <c>WHERE</c> clause.
///
/// Supported:
/// <list type="bullet">
///   <item>Comparison: <c>col eq 'v'</c>, <c>col ne 1</c>, <c>col gt 2.5</c>,
///   <c>col ge 1</c>, <c>col lt 'z'</c>, <c>col le null</c></item>
///   <item>String functions: <c>contains(col, 'x')</c>, <c>startswith(col, 'x')</c>,
///   <c>endswith(col, 'x')</c></item>
///   <item>Boolean composition: <c>and</c>, <c>or</c>, <c>not</c>, parentheses</item>
///   <item>Literals: single-quoted strings (with <c>''</c> escape), integers,
///   decimals, <c>true</c>, <c>false</c>, <c>null</c></item>
/// </list>
/// Identifiers are whitelisted against the caller-supplied <see cref="ICollection{T}"/>;
/// any column not in the whitelist throws <c>ODATA_FILTER_INVALID</c>.
/// </summary>
public static class ODataFilter
{
    /// <summary>
    /// Compile an OData filter expression into <c>(whereSql, params)</c>. The
    /// caller appends <c>WHERE … AND ({whereSql})</c> to its mask SQL.
    /// </summary>
    public static (string Sql, Dictionary<string, object?> Parameters) Compile(
        string expression,
        ICollection<string> allowedColumns,
        Func<string, string> quoteIdentifier)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw FilterError("$filter cannot be empty.", null);

        var parser = new Parser(expression);
        var ast = parser.ParseOr();
        parser.ExpectEnd();

        var paramsOut = new Dictionary<string, object?>();
        var sb = new StringBuilder();
        var ctx = new EmitContext(allowedColumns, quoteIdentifier, paramsOut);
        Emit(ast, sb, ctx);
        return (sb.ToString(), paramsOut);
    }

    // ── AST nodes ─────────────────────────────────────────────────────

    private abstract record Node;
    private sealed record Compare(string Column, string Op, object? Value) : Node;
    private sealed record Func(string Name, string Column, string Value) : Node;
    private sealed record And(Node Left, Node Right) : Node;
    private sealed record Or(Node Left, Node Right) : Node;
    private sealed record Not(Node Inner) : Node;
    private sealed record Group(Node Inner) : Node;

    // ── Parser ────────────────────────────────────────────────────────

    private sealed class Parser
    {
        private readonly string _src;
        private int _pos;

        public Parser(string src) { _src = src; }

        public Node ParseOr()
        {
            var left = ParseAnd();
            while (TryConsumeKeyword("or"))
                left = new Or(left, ParseAnd());
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseUnary();
            while (TryConsumeKeyword("and"))
                left = new And(left, ParseUnary());
            return left;
        }

        private Node ParseUnary()
        {
            if (TryConsumeKeyword("not"))
                return new Not(ParseUnary());
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipWs();
            if (Peek() == '(')
            {
                _pos++;
                var inner = ParseOr();
                SkipWs();
                if (Peek() != ')')
                    throw FilterError("Expected ')'.", _pos);
                _pos++;
                return new Group(inner);
            }

            // Function or comparison.
            var ident = ReadIdent();
            SkipWs();

            if (Peek() == '(')
            {
                _pos++;
                SkipWs();
                var col = ReadIdent();
                SkipWs();
                if (Peek() != ',')
                    throw FilterError("Expected ',' in function call.", _pos);
                _pos++;
                SkipWs();
                var value = ReadString();
                SkipWs();
                if (Peek() != ')')
                    throw FilterError("Expected ')' closing function.", _pos);
                _pos++;
                var fn = ident.ToLowerInvariant();
                if (fn is not ("contains" or "startswith" or "endswith"))
                    throw FilterError($"Unsupported function '{ident}'.", _pos);
                return new Func(fn, col, value);
            }

            // Comparison: identifier op value.
            var op = ReadIdent().ToLowerInvariant();
            if (op is not ("eq" or "ne" or "gt" or "ge" or "lt" or "le"))
                throw FilterError($"Unsupported operator '{op}'.", _pos);

            SkipWs();
            var rhs = ReadValue();
            return new Compare(ident, op, rhs);
        }

        public void ExpectEnd()
        {
            SkipWs();
            if (_pos != _src.Length)
                throw FilterError($"Unexpected trailing input at position {_pos}.", _pos);
        }

        private bool TryConsumeKeyword(string kw)
        {
            SkipWs();
            if (_pos + kw.Length > _src.Length) return false;
            for (var i = 0; i < kw.Length; i++)
                if (char.ToLowerInvariant(_src[_pos + i]) != kw[i]) return false;
            // word boundary check
            var after = _pos + kw.Length;
            if (after < _src.Length && (char.IsLetterOrDigit(_src[after]) || _src[after] == '_')) return false;
            _pos += kw.Length;
            return true;
        }

        private string ReadIdent()
        {
            SkipWs();
            var start = _pos;
            while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_'))
                _pos++;
            if (start == _pos)
                throw FilterError("Expected identifier.", _pos);
            return _src[start.._pos];
        }

        private object? ReadValue()
        {
            SkipWs();
            if (_pos >= _src.Length)
                throw FilterError("Expected value.", _pos);

            var c = _src[_pos];
            if (c == '\'') return ReadString();
            if (c == '-' || char.IsDigit(c)) return ReadNumber();

            // Keywords: true, false, null
            var kw = ReadIdent().ToLowerInvariant();
            return kw switch
            {
                "true" => true,
                "false" => false,
                "null" => null,
                _ => throw FilterError($"Unexpected literal '{kw}'.", _pos)
            };
        }

        private string ReadString()
        {
            SkipWs();
            if (_pos >= _src.Length || _src[_pos] != '\'')
                throw FilterError("Expected single-quoted string.", _pos);
            _pos++;
            var sb = new StringBuilder();
            while (_pos < _src.Length)
            {
                var c = _src[_pos];
                if (c == '\'')
                {
                    // '' inside string is an escaped single quote.
                    if (_pos + 1 < _src.Length && _src[_pos + 1] == '\'')
                    {
                        sb.Append('\'');
                        _pos += 2;
                        continue;
                    }
                    _pos++;
                    return sb.ToString();
                }
                sb.Append(c);
                _pos++;
            }
            throw FilterError("Unterminated string literal.", _pos);
        }

        private object ReadNumber()
        {
            var start = _pos;
            if (_src[_pos] == '-') _pos++;
            var hasDot = false;
            while (_pos < _src.Length)
            {
                var c = _src[_pos];
                if (char.IsDigit(c)) { _pos++; continue; }
                if (c == '.' && !hasDot) { hasDot = true; _pos++; continue; }
                break;
            }
            var text = _src[start.._pos];
            if (hasDot && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return l;
            throw FilterError($"Invalid numeric literal '{text}'.", _pos);
        }

        private char Peek() => _pos < _src.Length ? _src[_pos] : '\0';

        private void SkipWs()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
        }
    }

    // ── Emitter ───────────────────────────────────────────────────────

    private sealed class EmitContext
    {
        public ICollection<string> AllowedColumns { get; }
        public Func<string, string> Quote { get; }
        public Dictionary<string, object?> Params { get; }
        public int NextParam;
        public EmitContext(ICollection<string> allowed, Func<string, string> quote, Dictionary<string, object?> p)
        {
            AllowedColumns = allowed;
            Quote = quote;
            Params = p;
        }

        public string AddParam(object? value)
        {
            var name = "__f" + NextParam++;
            Params[name] = value;
            return "@" + name;
        }

        public string CheckedColumn(string column)
        {
            if (!AllowedColumns.Contains(column))
                throw FilterError(
                    $"Column '{column}' is not allowed in $filter.",
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = "one of: " + string.Join(", ", AllowedColumns),
                        ["hint"] = "Only the entity's declared columns can appear in $filter; aliased / joined columns are not addressable from the filter expression."
                    });
            return Quote(column);
        }
    }

    private static void Emit(Node node, StringBuilder sb, EmitContext ctx)
    {
        switch (node)
        {
            case Compare c:
                EmitCompare(c, sb, ctx);
                break;
            case Func f:
                EmitFunc(f, sb, ctx);
                break;
            case And a:
                Emit(a.Left, sb, ctx);
                sb.Append(" AND ");
                Emit(a.Right, sb, ctx);
                break;
            case Or o:
                Emit(o.Left, sb, ctx);
                sb.Append(" OR ");
                Emit(o.Right, sb, ctx);
                break;
            case Not n:
                sb.Append("NOT (");
                Emit(n.Inner, sb, ctx);
                sb.Append(')');
                break;
            case Group g:
                sb.Append('(');
                Emit(g.Inner, sb, ctx);
                sb.Append(')');
                break;
            default:
                throw FilterError("Unknown filter node type.", null);
        }
    }

    private static void EmitCompare(Compare c, StringBuilder sb, EmitContext ctx)
    {
        var col = ctx.CheckedColumn(c.Column);
        if (c.Value is null)
        {
            sb.Append(col);
            sb.Append(c.Op == "ne" ? " IS NOT NULL" : " IS NULL");
            return;
        }
        var sqlOp = c.Op switch
        {
            "eq" => "=",
            "ne" => "<>",
            "gt" => ">",
            "ge" => ">=",
            "lt" => "<",
            "le" => "<=",
            _ => throw FilterError($"Unknown operator '{c.Op}'.", null)
        };
        sb.Append(col).Append(' ').Append(sqlOp).Append(' ').Append(ctx.AddParam(c.Value));
    }

    private static void EmitFunc(Func f, StringBuilder sb, EmitContext ctx)
    {
        var col = ctx.CheckedColumn(f.Column);
        var pattern = f.Name switch
        {
            "contains" => "%" + EscapeLike(f.Value) + "%",
            "startswith" => EscapeLike(f.Value) + "%",
            "endswith" => "%" + EscapeLike(f.Value),
            _ => throw FilterError($"Unknown function '{f.Name}'.", null)
        };
        sb.Append(col).Append(" LIKE ").Append(ctx.AddParam(pattern));
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    // ── Errors ────────────────────────────────────────────────────────

    private static ApiException FilterError(string message, int? position, Dictionary<string, object?>? details = null)
    {
        details ??= new Dictionary<string, object?>();
        if (position.HasValue) details["position"] = position.Value;
        if (!details.ContainsKey("hint"))
            details["hint"] = "Examples: \"status eq 'paid'\", \"price gt 10\", \"contains(name, 'acme')\", \"a eq 1 and b eq 2\".";
        return new ApiException("ODATA_FILTER_INVALID", message, 400, null, details);
    }
}
