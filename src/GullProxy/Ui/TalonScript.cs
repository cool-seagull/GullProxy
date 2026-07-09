using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GullProxy.Ui;

/// <summary>Raised when a TalonScript program fails to parse or run.</summary>
public sealed class TalonError : Exception
{
    public TalonError(string message) : base(message) { }
}

/// <summary>
/// TalonScript — a small scripting language for HTTP automation, run inside Talon's
/// <c>&lt; {% %}</c> (pre-request) and <c>&gt; {% %}</c> (post-response) blocks. Hand-written
/// tree-walking interpreter — no third-party engine.
///
/// Values: null, booleans, numbers, strings, lists, objects. Literals for lists <c>[1,2]</c> and
/// objects <c>{a: 1}</c>. Statements: assignment (incl. <c>+= -= *= /=</c>), <c>log</c>,
/// <c>if/else</c>, <c>for x in …</c>, <c>while</c>, <c>repeat N</c>, <c>break</c>/<c>continue</c>.
/// Globals <c>request</c>/<c>response</c>/<c>vars</c> come from the host; bare names resolve to
/// <c>vars</c>. Rich builtin library (math, strings, regex, crypto, collections). See
/// docs/TalonScript.md.
/// </summary>
public sealed class TalonScript
{
    public List<string> Output { get; } = new();
    private readonly Dictionary<string, object?> _globals = new();
    private readonly Dictionary<string, object?> _vars;
    private readonly Dictionary<string, Func<object?[], object?>> _builtins;
    private static readonly Dictionary<string, object?> Consts = new()
    {
        ["PI"] = Math.PI, ["E"] = Math.E, ["TAU"] = Math.Tau, ["INF"] = double.PositiveInfinity,
    };
    private int _ops;
    private const int MaxOps = 2_000_000;

    public TalonScript(Dictionary<string, object?> vars)
    {
        _vars = vars;
        _globals["vars"] = vars;
        _builtins = Builtins();
    }

    public void SetGlobal(string name, object? value) => _globals[name] = value;

    public void Run(string code)
    {
        var tokens = new Lexer(code).Lex();
        var program = new Parser(tokens).ParseProgram();
        try { foreach (var s in program) Exec(s); }
        catch (LoopSignal) { throw new TalonError("'break'/'continue' used outside a loop"); }
    }

    // ===== AST ===============================================================================

    private abstract class Node { }
    private abstract class Expr : Node { }
    private sealed class Lit : Expr { public object? Value; }
    private sealed class NameExpr : Expr { public string Name = ""; }
    private sealed class Member : Expr { public Expr Target = null!; public string Name = ""; }
    private sealed class IndexExpr : Expr { public Expr Target = null!; public Expr Index = null!; }
    private sealed class Call : Expr { public Expr Callee = null!; public List<Expr> Args = new(); }
    private sealed class Unary : Expr { public string Op = ""; public Expr Operand = null!; }
    private sealed class Binary : Expr { public string Op = ""; public Expr L = null!, R = null!; }
    private sealed class ListLit : Expr { public List<Expr> Items = new(); }
    private sealed class ObjLit : Expr { public List<(string Key, Expr Value)> Pairs = new(); }

    private abstract class Stmt : Node { }
    private sealed class ExprStmt : Stmt { public Expr Expr = null!; }
    private sealed class Assign : Stmt { public Expr Target = null!; public Expr Value = null!; }
    private sealed class LogStmt : Stmt { public List<Expr> Args = new(); }
    private sealed class IfStmt : Stmt { public Expr Cond = null!; public List<Stmt> Then = new(); public List<Stmt>? Else; }
    private sealed class RepeatStmt : Stmt { public Expr Count = null!; public List<Stmt> Body = new(); }
    private sealed class ForStmt : Stmt { public string Var = ""; public Expr Iter = null!; public List<Stmt> Body = new(); }
    private sealed class WhileStmt : Stmt { public Expr Cond = null!; public List<Stmt> Body = new(); }
    private sealed class BreakStmt : Stmt { }
    private sealed class ContinueStmt : Stmt { }

    // ===== Lexer =============================================================================

    private enum TK { Num, Str, Ident, Op, Kw, Eof }
    private readonly record struct Token(TK Kind, string Text, double Num);

    private static readonly HashSet<string> Keywords = new()
    { "if", "else", "repeat", "for", "in", "while", "break", "continue", "and", "or", "not", "log", "true", "false", "null" };

    private static readonly string[] TwoCharOps = { "==", "!=", "<=", ">=", "+=", "-=", "*=", "/=" };

    private sealed class Lexer
    {
        private readonly string _s;
        private int _i;
        public Lexer(string s) => _s = s ?? "";

        public List<Token> Lex()
        {
            var t = new List<Token>();
            while (true)
            {
                SkipTrivia();
                if (_i >= _s.Length) { t.Add(new(TK.Eof, "", 0)); return t; }
                char c = _s[_i];
                if (char.IsDigit(c) || (c == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1]))) t.Add(LexNumber());
                else if (c == '"' || c == '\'') t.Add(LexString(c));
                else if (char.IsLetter(c) || c == '_') t.Add(LexIdent());
                else t.Add(LexOp());
            }
        }

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsWhiteSpace(c)) { _i++; continue; }
                if (c == '#') { while (_i < _s.Length && _s[_i] != '\n') _i++; continue; }
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/') { while (_i < _s.Length && _s[_i] != '\n') _i++; continue; }
                break;
            }
        }

        private Token LexNumber()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            string txt = _s[start.._i];
            if (!double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                throw new TalonError($"invalid number '{txt}'");
            return new(TK.Num, txt, n);
        }

        private Token LexString(char quote)
        {
            _i++;
            var sb = new StringBuilder();
            while (_i < _s.Length && _s[_i] != quote)
            {
                char c = _s[_i++];
                if (c == '\\' && _i < _s.Length)
                {
                    char e = _s[_i++];
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '"' => '"', '\'' => '\'', '\\' => '\\', '/' => '/', _ => e });
                }
                else sb.Append(c);
            }
            if (_i >= _s.Length) throw new TalonError("unterminated string");
            _i++;
            return new(TK.Str, sb.ToString(), 0);
        }

        private Token LexIdent()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            string txt = _s[start.._i];
            return new(Keywords.Contains(txt) ? TK.Kw : TK.Ident, txt, 0);
        }

        private Token LexOp()
        {
            foreach (var op in TwoCharOps)
                if (_i + 1 < _s.Length && _s[_i] == op[0] && _s[_i + 1] == op[1]) { _i += 2; return new(TK.Op, op, 0); }
            char c = _s[_i++];
            if ("=+-*/%<>.,()[]{}:".IndexOf(c) < 0) throw new TalonError($"unexpected character '{c}'");
            return new(TK.Op, c.ToString(), 0);
        }
    }

    // ===== Parser ============================================================================

    private sealed class Parser
    {
        private readonly List<Token> _t;
        private int _i;
        public Parser(List<Token> t) => _t = t;

        private Token Peek => _t[_i];
        private Token Next() => _t[_i++];
        private bool IsOp(string s) => Peek.Kind == TK.Op && Peek.Text == s;
        private bool IsKw(string s) => Peek.Kind == TK.Kw && Peek.Text == s;
        private void Expect(string s) { if (!IsOp(s)) throw new TalonError($"expected '{s}'"); _i++; }

        public List<Stmt> ParseProgram()
        {
            var list = new List<Stmt>();
            while (Peek.Kind != TK.Eof) list.Add(Statement());
            return list;
        }

        private List<Stmt> Block()
        {
            Expect("{");
            var list = new List<Stmt>();
            while (!IsOp("}") && Peek.Kind != TK.Eof) list.Add(Statement());
            Expect("}");
            return list;
        }

        private Stmt Statement()
        {
            if (IsKw("log")) { Next(); return new LogStmt { Args = ArgList() }; }
            if (IsKw("if")) return IfStatement();
            if (IsKw("repeat")) { Next(); var c = Expression(); return new RepeatStmt { Count = c, Body = Block() }; }
            if (IsKw("while")) { Next(); var c = Expression(); return new WhileStmt { Cond = c, Body = Block() }; }
            if (IsKw("break")) { Next(); return new BreakStmt(); }
            if (IsKw("continue")) { Next(); return new ContinueStmt(); }
            if (IsKw("for"))
            {
                Next();
                if (Peek.Kind != TK.Ident) throw new TalonError("expected a name after 'for'");
                string v = Next().Text;
                if (!IsKw("in")) throw new TalonError("expected 'in' in for-loop");
                Next();
                var iter = Expression();
                return new ForStmt { Var = v, Iter = iter, Body = Block() };
            }

            var e = Expression();
            if (Peek.Kind == TK.Op && Peek.Text is "=" or "+=" or "-=" or "*=" or "/=")
            {
                string op = Next().Text;
                if (e is not (NameExpr or Member or IndexExpr)) throw new TalonError("left side of assignment is not assignable");
                var rhs = Expression();
                Expr val = op == "=" ? rhs : new Binary { Op = op[..1], L = e, R = rhs };
                return new Assign { Target = e, Value = val };
            }
            return new ExprStmt { Expr = e };
        }

        private Stmt IfStatement()
        {
            Next();
            var cond = Expression();
            var then = Block();
            List<Stmt>? els = null;
            if (IsKw("else")) { Next(); els = IsKw("if") ? new List<Stmt> { IfStatement() } : Block(); }
            return new IfStmt { Cond = cond, Then = then, Else = els };
        }

        private List<Expr> ArgList()
        {
            var list = new List<Expr>();
            if (IsOp(")") || IsOp("]")) return list;
            list.Add(Expression());
            while (IsOp(",")) { Next(); if (IsOp(")") || IsOp("]")) break; list.Add(Expression()); }
            return list;
        }

        private Expr Expression() => Or();
        private Expr Or() { var l = And(); while (IsKw("or")) { Next(); l = new Binary { Op = "or", L = l, R = And() }; } return l; }
        private Expr And() { var l = Equality(); while (IsKw("and")) { Next(); l = new Binary { Op = "and", L = l, R = Equality() }; } return l; }
        private Expr Equality() { var l = Comparison(); while (IsOp("==") || IsOp("!=")) { var o = Next().Text; l = new Binary { Op = o, L = l, R = Comparison() }; } return l; }
        private Expr Comparison() { var l = Additive(); while (IsOp("<") || IsOp(">") || IsOp("<=") || IsOp(">=")) { var o = Next().Text; l = new Binary { Op = o, L = l, R = Additive() }; } return l; }
        private Expr Additive() { var l = Multiplicative(); while (IsOp("+") || IsOp("-")) { var o = Next().Text; l = new Binary { Op = o, L = l, R = Multiplicative() }; } return l; }
        private Expr Multiplicative() { var l = Unary(); while (IsOp("*") || IsOp("/") || IsOp("%")) { var o = Next().Text; l = new Binary { Op = o, L = l, R = Unary() }; } return l; }

        private Expr Unary()
        {
            if (IsKw("not")) { Next(); return new Unary { Op = "not", Operand = Unary() }; }
            if (IsOp("-")) { Next(); return new Unary { Op = "-", Operand = Unary() }; }
            return Postfix();
        }

        private Expr Postfix()
        {
            var e = Primary();
            while (true)
            {
                if (IsOp("."))
                {
                    Next();
                    if (Peek.Kind != TK.Ident && Peek.Kind != TK.Kw) throw new TalonError("expected name after '.'");
                    e = new Member { Target = e, Name = Next().Text };
                }
                else if (IsOp("[")) { Next(); var idx = Expression(); Expect("]"); e = new IndexExpr { Target = e, Index = idx }; }
                else if (IsOp("(")) { Next(); var args = ArgList(); Expect(")"); e = new Call { Callee = e, Args = args }; }
                else break;
            }
            return e;
        }

        private Expr Primary()
        {
            var t = Peek;
            if (t.Kind == TK.Num) { Next(); return new Lit { Value = t.Num }; }
            if (t.Kind == TK.Str) { Next(); return new Lit { Value = t.Text }; }
            if (IsKw("true")) { Next(); return new Lit { Value = true }; }
            if (IsKw("false")) { Next(); return new Lit { Value = false }; }
            if (IsKw("null")) { Next(); return new Lit { Value = null }; }
            if (t.Kind == TK.Ident) { Next(); return new NameExpr { Name = t.Text }; }
            if (IsOp("(")) { Next(); var e = Expression(); Expect(")"); return e; }
            if (IsOp("[")) { Next(); var items = ArgList(); Expect("]"); return new ListLit { Items = items }; }
            if (IsOp("{")) return ObjectLiteral();
            throw new TalonError($"unexpected token '{t.Text}'");
        }

        private Expr ObjectLiteral()
        {
            Expect("{");
            var pairs = new List<(string, Expr)>();
            while (!IsOp("}") && Peek.Kind != TK.Eof)
            {
                string key;
                if (Peek.Kind == TK.Str || Peek.Kind == TK.Ident || Peek.Kind == TK.Kw) key = Next().Text;
                else throw new TalonError("expected an object key");
                Expect(":");
                pairs.Add((key, Expression()));
                if (IsOp(",")) Next(); else break;
            }
            Expect("}");
            return new ObjLit { Pairs = pairs };
        }
    }

    // ===== Interpreter =======================================================================

    private sealed class LoopSignal : Exception { public bool IsBreak; }

    private void Tick() { if (++_ops > MaxOps) throw new TalonError("script exceeded operation limit (possible infinite loop)"); }

    private void Exec(Stmt s)
    {
        Tick();
        switch (s)
        {
            case ExprStmt es: Eval(es.Expr); break;
            case LogStmt ls: Output.Add(string.Join(' ', ls.Args.Select(a => Stringify(Eval(a))))); break;
            case Assign a: DoAssign(a); break;
            case BreakStmt: throw new LoopSignal { IsBreak = true };
            case ContinueStmt: throw new LoopSignal { IsBreak = false };
            case IfStmt f:
                if (Truthy(Eval(f.Cond))) RunBlock(f.Then);
                else if (f.Else is not null) RunBlock(f.Else);
                break;
            case RepeatStmt r:
                int n = (int)ToNumber(Eval(r.Count));
                RunLoop(() => { for (int k = 0; k < n; k++) if (!LoopBody(r.Body)) break; });
                break;
            case WhileStmt w:
                RunLoop(() => { while (Truthy(Eval(w.Cond))) if (!LoopBody(w.Body)) break; });
                break;
            case ForStmt fr:
                var seq = Iterable(Eval(fr.Iter));
                RunLoop(() => { foreach (var item in seq) { _vars[fr.Var] = item; if (!LoopBody(fr.Body)) break; } });
                break;
        }
    }

    private void RunBlock(List<Stmt> body) { foreach (var st in body) Exec(st); }

    /// <summary>Runs one loop iteration body; returns false to stop the loop (break).</summary>
    private bool LoopBody(List<Stmt> body)
    {
        Tick();
        try { RunBlock(body); }
        catch (LoopSignal sig) { if (sig.IsBreak) return false; }
        return true;
    }

    private static void RunLoop(Action loop)
    {
        try { loop(); }
        catch (LoopSignal sig) { if (!sig.IsBreak) return; else return; }
    }

    private static IEnumerable<object?> Iterable(object? v) => v switch
    {
        List<object?> l => l,
        Dictionary<string, object?> d => d.Keys.Cast<object?>().ToList(),
        string s => s.Select(c => (object?)c.ToString()).ToList(),
        _ => throw new TalonError("value is not iterable (expected a list, object, or string)"),
    };

    private void DoAssign(Assign a)
    {
        object? val = Eval(a.Value);
        switch (a.Target)
        {
            case NameExpr n: _vars[n.Name] = val; break;
            case Member m:
                if (Eval(m.Target) is Dictionary<string, object?> d) d[m.Name] = val;
                else throw new TalonError($"cannot set '.{m.Name}' on a non-object");
                break;
            case IndexExpr ix:
                object? obj = Eval(ix.Target);
                object? key = Eval(ix.Index);
                if (obj is Dictionary<string, object?> od) od[Stringify(key)] = val;
                else if (obj is List<object?> ol)
                {
                    int i = (int)ToNumber(key);
                    if (i == ol.Count) ol.Add(val);
                    else if (i >= 0 && i < ol.Count) ol[i] = val;
                    else throw new TalonError("list index out of range");
                }
                else throw new TalonError("cannot index-assign a non-collection");
                break;
        }
    }

    private object? Eval(Expr e)
    {
        Tick();
        switch (e)
        {
            case Lit l: return l.Value;
            case NameExpr n: return Resolve(n.Name);
            case Member m: return GetMember(Eval(m.Target), m.Name);
            case IndexExpr ix: return GetIndex(Eval(ix.Target), Eval(ix.Index));
            case Unary u: return EvalUnary(u);
            case Binary b: return EvalBinary(b);
            case Call c: return EvalCall(c);
            case ListLit ll: return ll.Items.Select(Eval).ToList();
            case ObjLit ol:
                var d = new Dictionary<string, object?>();
                foreach (var (k, ve) in ol.Pairs) d[k] = Eval(ve);
                return d;
            default: return null;
        }
    }

    private object? Resolve(string name)
    {
        if (_globals.TryGetValue(name, out var g)) return g;
        if (_builtins.TryGetValue(name, out var f)) return (Func<object?[], object?>)f;
        if (Consts.TryGetValue(name, out var c)) return c;
        return _vars.TryGetValue(name, out var v) ? v : null;
    }

    private static object? GetMember(object? target, string name) =>
        target is Dictionary<string, object?> d && d.TryGetValue(name, out var v) ? v : null;

    private static object? GetIndex(object? target, object? key)
    {
        if (target is Dictionary<string, object?> d) return d.TryGetValue(Stringify(key), out var v) ? v : null;
        if (target is List<object?> l) { int i = (int)ToNumber(key); return i >= 0 && i < l.Count ? l[i] : null; }
        if (target is string s) { int i = (int)ToNumber(key); return i >= 0 && i < s.Length ? s[i].ToString() : null; }
        return null;
    }

    private object? EvalUnary(Unary u)
    {
        var v = Eval(u.Operand);
        return u.Op == "not" ? !Truthy(v) : -ToNumber(v);
    }

    private object? EvalBinary(Binary b)
    {
        if (b.Op == "and") { var l = Eval(b.L); return Truthy(l) ? Eval(b.R) : l; }
        if (b.Op == "or") { var l = Eval(b.L); return Truthy(l) ? l : Eval(b.R); }

        object? a = Eval(b.L), c = Eval(b.R);
        switch (b.Op)
        {
            case "==": return AreEqual(a, c);
            case "!=": return !AreEqual(a, c);
            case "+":
                if (a is string || c is string) return Stringify(a) + Stringify(c);
                if (a is List<object?> la && c is List<object?> lc) return la.Concat(lc).ToList();
                return ToNumber(a) + ToNumber(c);
            case "-": return ToNumber(a) - ToNumber(c);
            case "*": return ToNumber(a) * ToNumber(c);
            case "/": return ToNumber(a) / ToNumber(c);
            case "%": return ToNumber(a) % ToNumber(c);
            case "<": return Compare(a, c) < 0;
            case ">": return Compare(a, c) > 0;
            case "<=": return Compare(a, c) <= 0;
            case ">=": return Compare(a, c) >= 0;
            default: throw new TalonError($"unknown operator '{b.Op}'");
        }
    }

    private object? EvalCall(Call c)
    {
        var callee = Eval(c.Callee);
        var args = c.Args.Select(Eval).ToArray();
        if (callee is Func<object?[], object?> fn) return fn(args);
        throw new TalonError("attempted to call a non-function");
    }

    // ===== Builtins ==========================================================================

    private static double N(object?[] a, int i) => i < a.Length ? ToNumber(a[i]) : 0;
    private static string S(object?[] a, int i) => i < a.Length ? Stringify(a[i]) : "";
    private static object? Arg(object?[] a, int i) => i < a.Length ? a[i] : null;

    private Dictionary<string, Func<object?[], object?>> Builtins() => new()
    {
        // ---- core / type ----
        ["str"] = a => Stringify(Arg(a, 0)),
        ["num"] = a => ToNumber(Arg(a, 0)),
        ["bool"] = a => Truthy(Arg(a, 0)),
        ["type"] = a => TypeName(Arg(a, 0)),
        ["isNull"] = a => Arg(a, 0) is null,
        ["default"] = a => Arg(a, 0) ?? Arg(a, 1),
        ["len"] = a => Arg(a, 0) switch { string s => (double)s.Length, List<object?> l => (double)l.Count, Dictionary<string, object?> d => (double)d.Count, _ => 0.0 },
        ["json"] = a => JsonToValue(S(a, 0)),
        ["jsonStringify"] = a => { try { return JsonSerializer.Serialize(Arg(a, 0)); } catch { return "null"; } },
        ["print"] = a => { Output.Add(string.Join(' ', a.Select(Stringify))); return null; },

        // ---- math ----
        ["abs"] = a => Math.Abs(N(a, 0)),
        ["floor"] = a => Math.Floor(N(a, 0)),
        ["ceil"] = a => Math.Ceiling(N(a, 0)),
        ["round"] = a => a.Length >= 2 ? Math.Round(N(a, 0), (int)N(a, 1)) : Math.Round(N(a, 0)),
        ["trunc"] = a => Math.Truncate(N(a, 0)),
        ["int"] = a => Math.Truncate(N(a, 0)),
        ["sqrt"] = a => Math.Sqrt(N(a, 0)),
        ["pow"] = a => Math.Pow(N(a, 0), N(a, 1)),
        ["exp"] = a => Math.Exp(N(a, 0)),
        ["ln"] = a => Math.Log(N(a, 0)),
        ["log10"] = a => Math.Log10(N(a, 0)),
        ["sign"] = a => (double)Math.Sign(N(a, 0)),
        ["sin"] = a => Math.Sin(N(a, 0)),
        ["cos"] = a => Math.Cos(N(a, 0)),
        ["tan"] = a => Math.Tan(N(a, 0)),
        ["atan"] = a => Math.Atan(N(a, 0)),
        ["atan2"] = a => Math.Atan2(N(a, 0), N(a, 1)),
        ["min"] = a => Reduce(a, Math.Min),
        ["max"] = a => Reduce(a, Math.Max),
        ["clamp"] = a => Math.Clamp(N(a, 0), N(a, 1), N(a, 2)),
        ["random"] = _ => Random.Shared.NextDouble(),
        ["randomInt"] = a => (double)Random.Shared.Next((int)N(a, 0), (int)N(a, 1) + 1),
        ["parseInt"] = a => (double)(long)Math.Truncate(ToNumber(Arg(a, 0))),
        ["parseFloat"] = a => ToNumber(Arg(a, 0)),

        // ---- strings ----
        ["upper"] = a => S(a, 0).ToUpperInvariant(),
        ["lower"] = a => S(a, 0).ToLowerInvariant(),
        ["trim"] = a => S(a, 0).Trim(),
        ["contains"] = a => Arg(a, 0) is List<object?> l ? l.Any(x => AreEqual(x, Arg(a, 1))) : S(a, 0).Contains(S(a, 1)),
        ["startsWith"] = a => S(a, 0).StartsWith(S(a, 1), StringComparison.Ordinal),
        ["endsWith"] = a => S(a, 0).EndsWith(S(a, 1), StringComparison.Ordinal),
        ["indexOf"] = a => (double)(Arg(a, 0) is List<object?> l ? l.FindIndex(x => AreEqual(x, Arg(a, 1))) : S(a, 0).IndexOf(S(a, 1), StringComparison.Ordinal)),
        ["replace"] = a => S(a, 0).Replace(S(a, 1), S(a, 2)),
        ["split"] = a => S(a, 0).Split(S(a, 1)).Select(x => (object?)x).ToList(),
        ["join"] = a => Arg(a, 0) is List<object?> l ? string.Join(S(a, 1), l.Select(Stringify)) : "",
        ["substring"] = a => Substring(S(a, 0), a),
        ["repeat"] = a => string.Concat(Enumerable.Repeat(S(a, 0), Math.Max(0, (int)N(a, 1)))),
        ["padStart"] = a => S(a, 0).PadLeft((int)N(a, 1), a.Length >= 3 ? S(a, 2)[0] : ' '),
        ["padEnd"] = a => S(a, 0).PadRight((int)N(a, 1), a.Length >= 3 ? S(a, 2)[0] : ' '),
        ["chars"] = a => S(a, 0).Select(c => (object?)c.ToString()).ToList(),

        // ---- regex ----
        ["regexTest"] = a => Rx(S(a, 1)).IsMatch(S(a, 0)),
        ["regexMatch"] = a => { var m = Rx(S(a, 1)).Match(S(a, 0)); return m.Success ? m.Value : null; },
        ["regexReplace"] = a => Rx(S(a, 1)).Replace(S(a, 0), S(a, 2)),
        ["regexAll"] = a => Rx(S(a, 1)).Matches(S(a, 0)).Select(m => (object?)m.Value).ToList(),

        // ---- url / encoding ----
        ["urlEncode"] = a => Uri.EscapeDataString(S(a, 0)),
        ["urlDecode"] = a => Uri.UnescapeDataString(S(a, 0)),
        ["base64"] = a => Convert.ToBase64String(Encoding.UTF8.GetBytes(S(a, 0))),
        ["base64decode"] = a => Encoding.UTF8.GetString(Convert.FromBase64String(S(a, 0))),
        ["base64url"] = a => Convert.ToBase64String(Encoding.UTF8.GetBytes(S(a, 0))).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        ["hex"] = a => Convert.ToHexString(Encoding.UTF8.GetBytes(S(a, 0))).ToLowerInvariant(),

        // ---- crypto / hashing ----
        ["md5"] = a => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(S(a, 0)))).ToLowerInvariant(),
        ["sha1"] = a => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(S(a, 0)))).ToLowerInvariant(),
        ["sha256"] = a => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(S(a, 0)))).ToLowerInvariant(),
        ["hmacSha256"] = a => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(S(a, 0)), Encoding.UTF8.GetBytes(S(a, 1)))).ToLowerInvariant(),

        // ---- collections ----
        ["list"] = a => a.ToList(),
        ["range"] = a => Range(a),
        ["push"] = a => { if (Arg(a, 0) is List<object?> l) l.Add(Arg(a, 1)); return Arg(a, 0); },
        ["first"] = a => Arg(a, 0) is List<object?> { Count: > 0 } l ? l[0] : null,
        ["last"] = a => Arg(a, 0) is List<object?> { Count: > 0 } l ? l[^1] : null,
        ["reverse"] = a => Arg(a, 0) is List<object?> l ? Enumerable.Reverse(l).ToList() : null,
        ["sort"] = a => Sort(Arg(a, 0)),
        ["sum"] = a => Arg(a, 0) is List<object?> l ? l.Sum(ToNumber) : 0.0,
        ["slice"] = a => Slice(Arg(a, 0), a),
        ["keys"] = a => Arg(a, 0) is Dictionary<string, object?> d ? new List<object?>(d.Keys) : new List<object?>(),
        ["values"] = a => Arg(a, 0) is Dictionary<string, object?> d ? new List<object?>(d.Values) : new List<object?>(),
        ["has"] = a => Arg(a, 0) is Dictionary<string, object?> d && d.ContainsKey(S(a, 1)),
        ["get"] = a => Arg(a, 0) is Dictionary<string, object?> d && d.TryGetValue(S(a, 1), out var v) ? v : Arg(a, 2),
        ["merge"] = a => Merge(Arg(a, 0), Arg(a, 1)),
        ["object"] = _ => new Dictionary<string, object?>(),

        // ---- time / misc ----
        ["now"] = _ => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        ["timestamp"] = _ => (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["timestampMs"] = _ => (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ["uuid"] = _ => Guid.NewGuid().ToString(),
        ["env"] = a => Environment.GetEnvironmentVariable(S(a, 0)),
    };

    private static object Reduce(object?[] a, Func<double, double, double> f)
    {
        IEnumerable<object?> src = a.Length == 1 && a[0] is List<object?> l ? l : a;
        var nums = src.Select(ToNumber).ToList();
        if (nums.Count == 0) return 0.0;
        double acc = nums[0];
        for (int i = 1; i < nums.Count; i++) acc = f(acc, nums[i]);
        return acc;
    }

    private static object Range(object?[] a)
    {
        int start = a.Length >= 2 ? (int)ToNumber(a[0]) : 0;
        int end = a.Length >= 2 ? (int)ToNumber(a[1]) : (int)ToNumber(Arg(a, 0));
        var list = new List<object?>();
        for (int i = start; i < end; i++) list.Add((double)i);
        return list;
    }

    private static object? Sort(object? v)
    {
        if (v is not List<object?> l) return v;
        var copy = new List<object?>(l);
        copy.Sort((x, y) => x is string sx && y is string sy ? string.CompareOrdinal(sx, sy) : ToNumber(x).CompareTo(ToNumber(y)));
        return copy;
    }

    private static object? Slice(object? v, object?[] a)
    {
        if (v is not List<object?> l) return v;
        int start = (int)N(a, 1);
        int end = a.Length >= 3 ? (int)N(a, 2) : l.Count;
        start = Math.Clamp(start, 0, l.Count);
        end = Math.Clamp(end, start, l.Count);
        return l.GetRange(start, end - start);
    }

    private static object Merge(object? a, object? b)
    {
        var d = new Dictionary<string, object?>();
        if (a is Dictionary<string, object?> da) foreach (var kv in da) d[kv.Key] = kv.Value;
        if (b is Dictionary<string, object?> db) foreach (var kv in db) d[kv.Key] = kv.Value;
        return d;
    }

    private static string Substring(string s, object?[] a)
    {
        int start = (int)N(a, 1);
        int end = a.Length >= 3 ? (int)N(a, 2) : s.Length;
        start = Math.Clamp(start, 0, s.Length);
        end = Math.Clamp(end, start, s.Length);
        return s[start..end];
    }

    private static Regex Rx(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
        catch (Exception ex) { throw new TalonError($"invalid regex: {ex.Message}"); }
    }

    // ===== Value helpers =====================================================================

    public static bool Truthy(object? v) => v switch
    {
        null => false, bool b => b, double d => d != 0, string s => s.Length > 0, List<object?> l => l.Count > 0, _ => true,
    };

    public static double ToNumber(object? v) => v switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : throw new TalonError($"cannot convert '{s}' to a number"),
        null => 0,
        _ => throw new TalonError("cannot convert value to a number"),
    };

    private static string TypeName(object? v) => v switch
    {
        null => "null", bool => "bool", double => "number", string => "string",
        List<object?> => "list", Dictionary<string, object?> => "object", Func<object?[], object?> => "function", _ => "unknown",
    };

    private static bool AreEqual(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is double || b is double) { try { return ToNumber(a) == ToNumber(b); } catch { return false; } }
        return a.Equals(b);
    }

    private static int Compare(object? a, object? b)
    {
        if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
        return ToNumber(a).CompareTo(ToNumber(b));
    }

    public static string Stringify(object? v)
    {
        switch (v)
        {
            case null: return "null";
            case bool b: return b ? "true" : "false";
            case string s: return s;
            case double d:
                if (double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
                return d.ToString(CultureInfo.InvariantCulture);
            default:
                try { return JsonSerializer.Serialize(v, JsonOpts); } catch { return v.ToString() ?? ""; }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static object? JsonToValue(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return FromElement(doc.RootElement); }
        catch { return null; }
    }

    private static object? FromElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Aggregate(new Dictionary<string, object?>(), (d, p) => { d[p.Name] = FromElement(p.Value); return d; }),
        JsonValueKind.Array => new List<object?>(e.EnumerateArray().Select(FromElement)),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
