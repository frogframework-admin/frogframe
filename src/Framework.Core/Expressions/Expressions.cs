using System.Globalization;

namespace Framework.Expressions;

// Bahasa ekspresi terbatas (RFC §9). Tidak ada eval, Roslyn, SQL, atau refleksi:
// sumber teks -> token -> AST -> dievaluasi oleh interpreter kecil dengan fungsi allowlist.

public sealed class ExpressionException(string message, int position = -1) : Exception(message)
{
    public int Position { get; } = position;
}

public sealed record ExpressionLimits(int MaxLength = 500, int MaxDepth = 24, int MaxNodes = 200, int MaxSteps = 5_000)
{
    public static readonly ExpressionLimits Default = new();
}

public enum TokKind { Number, String, Ident, Op, LParen, RParen, LBracket, RBracket, Comma, End }

public readonly record struct Token(TokKind Kind, string Text, int Pos);

public static class Lexer
{
    private static readonly string[] Ops = ["==", "!=", ">=", "<=", "&&", "||", ">", "<", "!", "+", "-", "*", "/", "%"];

    public static List<Token> Tokenize(string src)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.' || src[i] == '_')) i++;
                tokens.Add(new Token(TokKind.Number, src[start..i].Replace("_", ""), start));
                continue;
            }

            if (c is '\'' or '"')
            {
                var start = i++;
                var sb = new System.Text.StringBuilder();
                while (i < src.Length && src[i] != c)
                {
                    if (src[i] == '\\' && i + 1 < src.Length) { sb.Append(src[i + 1]); i += 2; continue; }
                    sb.Append(src[i++]);
                }
                if (i >= src.Length) throw new ExpressionException("String tidak ditutup", start);
                i++;
                tokens.Add(new Token(TokKind.String, sb.ToString(), start));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var start = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] is '_' or '.' or '$')) i++;
                var text = src[start..i];
                tokens.Add(text is "in" or "and" or "or" or "not"
                    ? new Token(TokKind.Op, text switch { "and" => "&&", "or" => "||", "not" => "!", _ => "in" }, start)
                    : new Token(TokKind.Ident, text, start));
                continue;
            }

            switch (c)
            {
                case '(': tokens.Add(new Token(TokKind.LParen, "(", i++)); continue;
                case ')': tokens.Add(new Token(TokKind.RParen, ")", i++)); continue;
                case '[': tokens.Add(new Token(TokKind.LBracket, "[", i++)); continue;
                case ']': tokens.Add(new Token(TokKind.RBracket, "]", i++)); continue;
                case ',': tokens.Add(new Token(TokKind.Comma, ",", i++)); continue;
            }

            var op = Ops.FirstOrDefault(o => string.CompareOrdinal(src, i, o, 0, o.Length) == 0);
            if (op is null) throw new ExpressionException($"Karakter tidak dikenal '{c}'", i);
            tokens.Add(new Token(TokKind.Op, op, i));
            i += op.Length;
        }
        tokens.Add(new Token(TokKind.End, "", src.Length));
        return tokens;
    }
}

// ---------- AST ----------
public abstract record Node;
public sealed record Literal(object? Value) : Node;
public sealed record PathRef(string[] Segments) : Node { public string Path => string.Join('.', Segments); }
public sealed record Unary(string Op, Node Operand) : Node;
public sealed record Binary(string Op, Node Left, Node Right) : Node;
public sealed record Call(string Function, IReadOnlyList<Node> Args) : Node;
public sealed record ListLit(IReadOnlyList<Node> Items) : Node;

public sealed class ParsedExpression
{
    public const int LanguageVersion = 1;
    public required string Source { get; init; }
    public required Node Root { get; init; }
    /// <summary>Path yang dirujuk ekspresi, mis. "total" atau "items.amount".</summary>
    public required IReadOnlySet<string> Paths { get; init; }
    public IEnumerable<string> RootNames => Paths.Select(p => p.Split('.')[0]).Distinct();
}

public sealed class ExpressionParser
{
    private readonly FunctionRegistry _functions;
    private readonly ExpressionLimits _limits;
    private List<Token> _t = [];
    private int _p, _nodes;
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

    public ExpressionParser(FunctionRegistry functions, ExpressionLimits? limits = null)
    {
        _functions = functions;
        _limits = limits ?? ExpressionLimits.Default;
    }

    public ParsedExpression Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ExpressionException("Ekspresi kosong");
        if (source.Length > _limits.MaxLength) throw new ExpressionException($"Ekspresi melebihi {_limits.MaxLength} karakter");
        _t = Lexer.Tokenize(source); _p = 0; _nodes = 0; _paths.Clear();
        var root = Or(0);
        if (Peek.Kind != TokKind.End) throw new ExpressionException($"Token tak terduga '{Peek.Text}'", Peek.Pos);
        return new ParsedExpression { Source = source, Root = root, Paths = new HashSet<string>(_paths) };
    }

    private Token Peek => _t[_p];
    private Token Next() => _t[_p++];
    private bool IsOp(string op) => Peek.Kind == TokKind.Op && Peek.Text == op;

    private T Track<T>(T node, int depth) where T : Node
    {
        if (++_nodes > _limits.MaxNodes) throw new ExpressionException($"Ekspresi melebihi {_limits.MaxNodes} node");
        if (depth > _limits.MaxDepth) throw new ExpressionException($"Kedalaman ekspresi melebihi {_limits.MaxDepth}");
        return node;
    }

    private Node Or(int d) { var l = And(d + 1); while (IsOp("||")) { Next(); l = Track(new Binary("||", l, And(d + 1)), d); } return l; }
    private Node And(int d) { var l = Eq(d + 1); while (IsOp("&&")) { Next(); l = Track(new Binary("&&", l, Eq(d + 1)), d); } return l; }
    private Node Eq(int d) { var l = Cmp(d + 1); while (IsOp("==") || IsOp("!=")) { var o = Next().Text; l = Track(new Binary(o, l, Cmp(d + 1)), d); } return l; }
    private Node Cmp(int d)
    {
        var l = Add(d + 1);
        while (IsOp("<") || IsOp("<=") || IsOp(">") || IsOp(">=") || IsOp("in"))
        { var o = Next().Text; l = Track(new Binary(o, l, Add(d + 1)), d); }
        return l;
    }
    private Node Add(int d) { var l = Mul(d + 1); while (IsOp("+") || IsOp("-")) { var o = Next().Text; l = Track(new Binary(o, l, Mul(d + 1)), d); } return l; }
    private Node Mul(int d) { var l = Un(d + 1); while (IsOp("*") || IsOp("/") || IsOp("%")) { var o = Next().Text; l = Track(new Binary(o, l, Un(d + 1)), d); } return l; }
    private Node Un(int d)
    {
        if (IsOp("!") || IsOp("-")) { var o = Next().Text; return Track(new Unary(o, Un(d + 1)), d); }
        return Primary(d + 1);
    }

    private Node Primary(int d)
    {
        var tok = Next();
        switch (tok.Kind)
        {
            case TokKind.Number:
                if (!decimal.TryParse(tok.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
                    throw new ExpressionException($"Angka tidak valid '{tok.Text}'", tok.Pos);
                return Track(new Literal(num), d);
            case TokKind.String:
                return Track(new Literal(tok.Text), d);
            case TokKind.LParen:
                var inner = Or(d + 1);
                Expect(TokKind.RParen, ")");
                return inner;
            case TokKind.LBracket:
                var items = new List<Node>();
                if (Peek.Kind != TokKind.RBracket)
                    do { items.Add(Or(d + 1)); } while (TryConsume(TokKind.Comma));
                Expect(TokKind.RBracket, "]");
                return Track(new ListLit(items), d);
            case TokKind.Ident:
                switch (tok.Text)
                {
                    case "true": return Track(new Literal(true), d);
                    case "false": return Track(new Literal(false), d);
                    case "null": return Track(new Literal(null), d);
                }
                if (Peek.Kind == TokKind.LParen)
                {
                    Next();
                    if (!_functions.TryGet(tok.Text, out var fn))
                        throw new ExpressionException($"Fungsi '{tok.Text}' tidak diizinkan", tok.Pos);
                    var args = new List<Node>();
                    if (Peek.Kind != TokKind.RParen)
                        do { args.Add(Or(d + 1)); } while (TryConsume(TokKind.Comma));
                    Expect(TokKind.RParen, ")");
                    if (args.Count < fn.MinArgs || args.Count > fn.MaxArgs)
                        throw new ExpressionException($"Fungsi '{tok.Text}' menerima {fn.MinArgs}-{fn.MaxArgs} argumen", tok.Pos);
                    return Track(new Call(tok.Text, args), d);
                }
                if (tok.Text.StartsWith('$') || tok.Text.Contains("..") || tok.Text.EndsWith('.'))
                    throw new ExpressionException($"Nama tidak valid '{tok.Text}'", tok.Pos);
                _paths.Add(tok.Text);
                return Track(new PathRef(tok.Text.Split('.')), d);
            default:
                throw new ExpressionException(tok.Kind == TokKind.End ? "Ekspresi tidak lengkap" : $"Token tak terduga '{tok.Text}'", tok.Pos);
        }
    }

    private bool TryConsume(TokKind k) { if (Peek.Kind != k) return false; _p++; return true; }
    private void Expect(TokKind k, string text)
    {
        if (Peek.Kind != k) throw new ExpressionException($"Diharapkan '{text}'", Peek.Pos);
        _p++;
    }
}

// ---------- Evaluasi ----------
public interface IEvalScope
{
    bool TryResolve(string name, out object? value);
}

public sealed class DictionaryScope(IReadOnlyDictionary<string, object?> values, IEvalScope? parent = null) : IEvalScope
{
    public bool TryResolve(string name, out object? value)
    {
        if (values.TryGetValue(name, out value)) return true;
        if (parent is not null) return parent.TryResolve(name, out value);
        value = null;
        return false;
    }
}

public sealed class EvalContext
{
    public required IEvalScope Scope { get; init; }
    public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>();
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.Now);
    public ExpressionLimits Limits { get; init; } = ExpressionLimits.Default;
    internal int Steps;
}

public sealed record FunctionDef(string Name, int MinArgs, int MaxArgs, Func<IReadOnlyList<object?>, EvalContext, object?> Invoke);

public sealed class FunctionRegistry
{
    private readonly Dictionary<string, FunctionDef> _fns = new(StringComparer.Ordinal);

    public void Register(FunctionDef fn) => _fns[fn.Name] = fn;
    public bool TryGet(string name, out FunctionDef fn) => _fns.TryGetValue(name, out fn!);
    public IEnumerable<string> Names => _fns.Keys;

    public static FunctionRegistry CreateDefault()
    {
        var r = new FunctionRegistry();
        r.Register(new("empty", 1, 1, (a, _) => Values.IsEmpty(a[0])));
        r.Register(new("len", 1, 1, (a, _) => (decimal)(a[0] switch
        {
            null => 0,
            string s => s.Length,
            IEnumerable<object?> l => l.Count(),
            _ => 1
        })));
        r.Register(new("sum", 1, 1, (a, _) => a[0] is IEnumerable<object?> l
            ? l.Sum(x => Values.ToDecimal(x) ?? 0m)
            : Values.ToDecimal(a[0]) ?? 0m));
        r.Register(new("round", 1, 2, (a, _) =>
        {
            var v = Values.ToDecimal(a[0]) ?? 0m;
            var digits = a.Count > 1 ? (int)(Values.ToDecimal(a[1]) ?? 0) : 0;
            return Math.Round(v, Math.Clamp(digits, 0, 10), MidpointRounding.AwayFromZero);
        }));
        r.Register(new("abs", 1, 1, (a, _) => Math.Abs(Values.ToDecimal(a[0]) ?? 0m)));
        r.Register(new("coalesce", 1, 8, (a, _) => a.FirstOrDefault(x => !Values.IsEmpty(x))));
        r.Register(new("today", 0, 0, (_, c) => c.Today.ToString("yyyy-MM-dd")));
        r.Register(new("has_role", 1, 1, (a, c) => a[0] is string s && c.Roles.Contains(s)));
        return r;
    }
}

public static class Values
{
    public static bool IsEmpty(object? v) => v switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        IEnumerable<object?> l => !l.Any(),
        _ => false
    };

    public static decimal? ToDecimal(object? v) => v switch
    {
        null => null,
        decimal d => d,
        int i => i,
        long l => l,
        double db => (decimal)db,
        float f => (decimal)f,
        bool b => b ? 1 : 0,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) => r,
        _ => null
    };

    public static bool IsNumber(object? v) => v is decimal or int or long or double or float;

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        IEnumerable<object?> l => l.Any(),
        _ when IsNumber(v) => ToDecimal(v) != 0,
        _ => true
    };

    public static bool AreEqual(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (IsNumber(a) || IsNumber(b))
        {
            var x = ToDecimal(a); var y = ToDecimal(b);
            return x is not null && y is not null && x == y;
        }
        if (a is bool ba && b is bool bb) return ba == bb;
        return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    public static int Compare(object? a, object? b)
    {
        if (a is null || b is null) throw new ExpressionException("Tidak dapat membandingkan nilai kosong");
        if (IsNumber(a) || IsNumber(b))
        {
            var x = ToDecimal(a) ?? throw new ExpressionException("Perbandingan angka dengan non-angka");
            var y = ToDecimal(b) ?? throw new ExpressionException("Perbandingan angka dengan non-angka");
            return x.CompareTo(y);
        }
        // Tanggal disimpan sebagai yyyy-MM-dd sehingga perbandingan ordinal tetap benar.
        return string.CompareOrdinal(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
    }
}

public sealed class ExpressionEvaluator(FunctionRegistry functions)
{
    public object? Evaluate(ParsedExpression expr, EvalContext ctx) => Eval(expr.Root, ctx);

    public bool EvaluateBool(ParsedExpression expr, EvalContext ctx) => Values.Truthy(Evaluate(expr, ctx));

    private object? Eval(Node n, EvalContext c)
    {
        if (++c.Steps > c.Limits.MaxSteps) throw new ExpressionException("Batas langkah evaluasi terlampaui");
        switch (n)
        {
            case Literal l: return l.Value;
            case PathRef p: return Resolve(p, c);
            case ListLit list: return list.Items.Select(i => Eval(i, c)).ToList();
            case Unary u:
                var v = Eval(u.Operand, c);
                return u.Op == "!" ? !Values.Truthy(v) : -(Values.ToDecimal(v) ?? throw new ExpressionException("Operator '-' butuh angka"));
            case Binary b: return EvalBinary(b, c);
            case Call call:
                functions.TryGet(call.Function, out var fn);
                return fn.Invoke(call.Args.Select(a => Eval(a, c)).ToList(), c);
            default: throw new ExpressionException("Node tidak dikenal");
        }
    }

    private object? EvalBinary(Binary b, EvalContext c)
    {
        // Short-circuit
        if (b.Op == "&&") return Values.Truthy(Eval(b.Left, c)) && Values.Truthy(Eval(b.Right, c));
        if (b.Op == "||") return Values.Truthy(Eval(b.Left, c)) || Values.Truthy(Eval(b.Right, c));

        var l = Eval(b.Left, c);
        var r = Eval(b.Right, c);
        switch (b.Op)
        {
            case "==": return Values.AreEqual(l, r);
            case "!=": return !Values.AreEqual(l, r);
            case "<": return Values.Compare(l, r) < 0;
            case "<=": return Values.Compare(l, r) <= 0;
            case ">": return Values.Compare(l, r) > 0;
            case ">=": return Values.Compare(l, r) >= 0;
            case "in":
                return r is IEnumerable<object?> list ? list.Any(x => Values.AreEqual(l, x))
                     : r is string s && l is string sub && s.Contains(sub, StringComparison.Ordinal);
            case "+" when l is string || r is string:
                return Convert.ToString(l, CultureInfo.InvariantCulture) + Convert.ToString(r, CultureInfo.InvariantCulture);
        }

        var x = Values.ToDecimal(l) ?? 0m;
        var y = Values.ToDecimal(r) ?? 0m;
        return b.Op switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" => y == 0 ? throw new ExpressionException("Pembagian dengan nol") : x / y,
            "%" => y == 0 ? throw new ExpressionException("Pembagian dengan nol") : x % y,
            _ => throw new ExpressionException($"Operator '{b.Op}' tidak dikenal")
        };
    }

    private static object? Resolve(PathRef p, EvalContext c)
    {
        if (!c.Scope.TryResolve(p.Segments[0], out var current)) return null;
        foreach (var seg in p.Segments.Skip(1))
            current = Step(current, seg);
        return current;
    }

    private static object? Step(object? current, string seg) => current switch
    {
        null => null,
        IReadOnlyDictionary<string, object?> d => d.TryGetValue(seg, out var v) ? v : null,
        IDictionary<string, object?> d => d.TryGetValue(seg, out var v) ? v : null,
        // Proyeksi list: items.amount -> [amount baris 1, amount baris 2, ...]
        IEnumerable<object?> list when current is not string => list.Select(x => Step(x, seg)).ToList(),
        _ => null
    };
}
