using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PrintHelper
{
    /// <summary>
    /// Everything a report needs to resolve {expressions}: current data rows, group rows, variables, page numbers.
    /// </summary>
    internal sealed class EvalContext
    {
        public DataRegistry Data;
        public IDictionary<string, object> Vars;
        public readonly Dictionary<string, DataRow> Rows = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
        public string DefaultSource;      // bare {Field} resolves here (Detail/Group bands, table rows)
        public string GroupSource;        // set inside group header/footer bands
        public List<DataRow> GroupRows;   // Sum/Count/... use these inside group bands
        public int RowNumber;             // RowNo()
        public int Page = 1, Pages = 1;

        // ------------------------------------------------------------ text templates

        public string Expand(string t)
        {
            if (string.IsNullOrEmpty(t) || (t.IndexOf('{') < 0 && t.IndexOf('}') < 0)) return t ?? "";
            var sb = new StringBuilder();
            int i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '{')
                {
                    if (i + 1 < t.Length && t[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                    int end = FindClose(t, i + 1);
                    if (end < 0) { sb.Append(t, i, t.Length - i); break; }
                    string inner = t.Substring(i + 1, end - i - 1), expr = inner, fmt = null;
                    int colon = TopLevelColon(inner);
                    if (colon >= 0) { expr = inner.Substring(0, colon); fmt = inner.Substring(colon + 1); }
                    object v;
                    if (TryEval(expr, out v)) sb.Append(Format(v, fmt));
                    else sb.Append('{').Append(inner).Append('}');   // unknown text stays visible so mistakes are easy to spot
                    i = end + 1;
                    continue;
                }
                if (c == '}' && i + 1 < t.Length && t[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static int FindClose(string t, int from)
        {
            int depth = 1;
            char quote = '\0';
            for (int i = from; i < t.Length; i++)
            {
                char c = t[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') quote = c;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }

        static int TopLevelColon(string s)
        {
            int paren = 0;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') quote = c;
                else if (c == '(' || c == '[') paren++;
                else if (c == ')' || c == ']') paren--;
                else if (c == ':' && paren == 0) return i;
            }
            return -1;
        }

        public static string Format(object v, string fmt)
        {
            if (v == null || v is DBNull) return "";
            var f = v as IFormattable;
            if (f != null && !string.IsNullOrEmpty(fmt)) return f.ToString(fmt, CultureInfo.CurrentCulture);
            return Convert.ToString(v, CultureInfo.CurrentCulture);
        }

        // ------------------------------------------------------------ evaluation

        public object Eval(string expr) { return Formula.Compile(expr)(this); }

        public bool TryEval(string expr, out object v)
        {
            try { v = Eval(expr); return true; }
            catch (Exception) { v = null; return false; }
        }

        public bool EvalBool(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return true;
            object v;
            return !TryEval(expr, out v) || Formula.Truthy(v);   // a broken condition does not hide the element
        }

        internal object ResolveIdent(string name)
        {
            object v;
            if (Vars != null && Vars.TryGetValue(name, out v)) return Formula.Norm(v);
            switch (name.ToLowerInvariant())
            {
                case "page": return (decimal)Page;
                case "pages": return (decimal)Pages;
                case "date": case "today": return DateTime.Today;
                case "now": return DateTime.Now;
            }
            int dot = name.IndexOf('.');
            if (dot > 0)
            {
                if (TryField(name.Substring(0, dot), name.Substring(dot + 1), out v)) return Formula.Norm(v);
                throw new FormatException("Unknown field " + name);
            }
            if (DefaultSource != null && TryField(DefaultSource, name, out v)) return Formula.Norm(v);
            foreach (var key in Rows.Keys.ToList())
                if (TryField(key, name, out v)) return Formula.Norm(v);
            throw new FormatException("Unknown name " + name);
        }

        bool TryField(string ds, string field, out object v)
        {
            v = null;
            var t = Data.Get(ds);
            if (t == null || !t.Columns.Contains(field)) return false;
            DataRow row;
            if (!Rows.TryGetValue(ds, out row)) row = t.Rows.Count > 0 ? t.Rows[0] : null;
            v = row == null ? null : row[field];
            return true;
        }

        internal object Aggregate(string fn, bool all, Func<EvalContext, object> arg, string path, string hint)
        {
            string src = null;
            bool argIsTable = false;
            if (path != null)
            {
                int dot = path.IndexOf('.');
                if (dot > 0) src = path.Substring(0, dot);
                else if (Data.Get(path) != null)
                {
                    var def = DefaultSource == null ? null : Data.Get(DefaultSource);
                    if (def == null || !def.Columns.Contains(path)) { src = path; argIsTable = true; }
                }
            }
            if (src == null && hint != null && Data.Get(hint) != null) src = hint;
            if (src == null) src = all ? (DefaultSource ?? GroupSource) : (GroupSource ?? DefaultSource);
            if (src == null) throw new FormatException(fn + " needs a data source");
            var table = Data.Get(src);
            if (table == null) throw new FormatException("Unknown data source " + src);

            List<DataRow> rows = (!all && GroupRows != null && string.Equals(GroupSource, src, StringComparison.OrdinalIgnoreCase))
                ? GroupRows : table.Rows.Cast<DataRow>().ToList();

            if (arg == null || argIsTable)
            {
                if (fn == "count") return (decimal)rows.Count;
                throw new FormatException(fn + " needs a field");
            }

            var vals = new List<object>();
            DataRow saved;
            bool had = Rows.TryGetValue(src, out saved);
            try
            {
                foreach (var r in rows) { Rows[src] = r; vals.Add(arg(this)); }
            }
            finally
            {
                if (had) Rows[src] = saved; else Rows.Remove(src);
            }

            if (fn == "count") return (decimal)vals.Count(x => x != null);
            var present = vals.Where(x => x != null).ToList();
            if (present.Count == 0) return 0m;
            if ((fn == "min" || fn == "max") && present.All(x => x is DateTime))
                return fn == "min" ? present.Min(x => (DateTime)x) : present.Max(x => (DateTime)x);
            var nums = present.Select(Formula.Num).ToList();
            switch (fn)
            {
                case "sum": return nums.Sum();
                case "avg": return nums.Sum() / nums.Count;
                case "min": return nums.Min();
                default: return nums.Max();
            }
        }
    }

    /// <summary>Small expression language: + - * / %, comparisons, &amp;&amp; || !, IIF, string/date/number functions, aggregates.</summary>
    internal static class Formula
    {
        static readonly ConcurrentDictionary<string, Func<EvalContext, object>> Cache = new ConcurrentDictionary<string, Func<EvalContext, object>>();
        static readonly HashSet<string> Aggs = new HashSet<string> { "sum", "count", "avg", "min", "max", "sumall", "countall", "avgall", "minall", "maxall" };

        public static Func<EvalContext, object> Compile(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) throw new FormatException("Empty expression");
            return Cache.GetOrAdd(expr, e => new Parser(e).Parse());
        }

        // ------------------------------------------------------------ value helpers

        public static object Norm(object v)
        {
            if (v == null || v is DBNull) return null;
            if (v is decimal || v is string || v is bool || v is DateTime) return v;
            if (v is char) return v.ToString();
            if (v is int || v is long || v is short || v is byte || v is sbyte || v is uint || v is ulong || v is ushort) return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
            if (v is double || v is float)
            {
                var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) return 0m;
                return (decimal)d;
            }
            return v;
        }

        public static decimal Num(object v)
        {
            v = Norm(v);
            if (v == null) return 0m;
            if (v is decimal) return (decimal)v;
            if (v is bool) return (bool)v ? 1m : 0m;
            decimal r;
            if (v is string && decimal.TryParse((string)v, NumberStyles.Any, CultureInfo.InvariantCulture, out r)) return r;
            throw new FormatException("Not a number: " + v);
        }

        public static bool Truthy(object v)
        {
            v = Norm(v);
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is decimal) return (decimal)v != 0m;
            if (v is string) return ((string)v).Length > 0 && !string.Equals((string)v, "false", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        static string S(object v) { return EvalContext.Format(Norm(v), null); }

        static int Compare(object a, object b)
        {
            a = Norm(a); b = Norm(b);
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            if (a is decimal && b is decimal) return ((decimal)a).CompareTo((decimal)b);
            if (a is DateTime && b is DateTime) return ((DateTime)a).CompareTo((DateTime)b);
            if (a is bool && b is bool) return ((bool)a).CompareTo((bool)b);
            decimal x, y;
            if ((a is decimal || a is string) && (b is decimal || b is string) &&
                TryNum(a, out x) && TryNum(b, out y)) return x.CompareTo(y);
            return string.Compare(S(a), S(b), StringComparison.OrdinalIgnoreCase);
        }

        static bool TryNum(object v, out decimal d)
        {
            if (v is decimal) { d = (decimal)v; return true; }
            return decimal.TryParse(v as string, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
        }

        /// <summary>Comparer used for sorting and grouping keys.</summary>
        public static int CompareValues(object a, object b) { return Compare(a, b); }

        // ------------------------------------------------------------ tokens

        enum K { Num, Str, Ident, Op, LP, RP, Comma, End }

        sealed class Tok
        {
            public K Kind;
            public string Text;
            public decimal Num;
        }

        sealed class Parser
        {
            readonly List<Tok> _t = new List<Tok>();
            int _p;

            public Parser(string s) { Lex(s); }

            void Lex(string s)
            {
                int i = 0;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (char.IsWhiteSpace(c)) { i++; continue; }
                    if ((c >= '0' && c <= '9') || (c == '.' && i + 1 < s.Length && s[i + 1] >= '0' && s[i + 1] <= '9'))
                    {
                        int st = i;
                        while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.')) i++;
                        _t.Add(new Tok { Kind = K.Num, Num = decimal.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture) });
                        continue;
                    }
                    if (c == '\'' || c == '"')
                    {
                        var sb = new StringBuilder();
                        i++;
                        while (true)
                        {
                            if (i >= s.Length) throw new FormatException("Unterminated string");
                            if (s[i] == c) { if (i + 1 < s.Length && s[i + 1] == c) { sb.Append(c); i += 2; continue; } i++; break; }
                            sb.Append(s[i++]);
                        }
                        _t.Add(new Tok { Kind = K.Str, Text = sb.ToString() });
                        continue;
                    }
                    if (c == '[')
                    {
                        int e = s.IndexOf(']', i);
                        if (e < 0) throw new FormatException("Missing ]");
                        var name = s.Substring(i + 1, e - i - 1);
                        i = e + 1;
                        // [Ds].[Field] -> Ds.Field
                        if (i + 1 < s.Length && s[i] == '.' && s[i + 1] == '[')
                        {
                            int e2 = s.IndexOf(']', i + 2);
                            if (e2 < 0) throw new FormatException("Missing ]");
                            name += "." + s.Substring(i + 2, e2 - i - 2);
                            i = e2 + 1;
                        }
                        _t.Add(new Tok { Kind = K.Ident, Text = name });
                        continue;
                    }
                    if (char.IsLetter(c) || c == '_')
                    {
                        int st = i;
                        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' ||
                               (s[i] == '.' && i + 1 < s.Length && (char.IsLetterOrDigit(s[i + 1]) || s[i + 1] == '_')))) i++;
                        _t.Add(new Tok { Kind = K.Ident, Text = s.Substring(st, i - st) });
                        continue;
                    }
                    if (c == '(') { _t.Add(new Tok { Kind = K.LP }); i++; continue; }
                    if (c == ')') { _t.Add(new Tok { Kind = K.RP }); i++; continue; }
                    if (c == ',') { _t.Add(new Tok { Kind = K.Comma }); i++; continue; }
                    string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                    if (two == "==" || two == "!=" || two == "<>" || two == "<=" || two == ">=" || two == "&&" || two == "||")
                    { _t.Add(new Tok { Kind = K.Op, Text = two }); i += 2; continue; }
                    if ("+-*/%<>=!".IndexOf(c) >= 0) { _t.Add(new Tok { Kind = K.Op, Text = c.ToString() }); i++; continue; }
                    throw new FormatException("Unexpected character '" + c + "'");
                }
                _t.Add(new Tok { Kind = K.End });
            }

            Tok Cur { get { return _t[_p]; } }
            bool IsOp(params string[] ops) { return Cur.Kind == K.Op && Array.IndexOf(ops, Cur.Text) >= 0; }
            bool IsWord(string w) { return Cur.Kind == K.Ident && string.Equals(Cur.Text, w, StringComparison.OrdinalIgnoreCase); }

            public Func<EvalContext, object> Parse()
            {
                var f = Or();
                if (Cur.Kind != K.End) throw new FormatException("Unexpected '" + (Cur.Text ?? Cur.Kind.ToString()) + "'");
                return f;
            }

            Func<EvalContext, object> Or()
            {
                var l = And();
                while (IsOp("||") || IsWord("or"))
                {
                    _p++;
                    var a = l; var b = And();
                    l = c => Truthy(a(c)) || Truthy(b(c));
                }
                return l;
            }

            Func<EvalContext, object> And()
            {
                var l = Cmp();
                while (IsOp("&&") || IsWord("and"))
                {
                    _p++;
                    var a = l; var b = Cmp();
                    l = c => Truthy(a(c)) && Truthy(b(c));
                }
                return l;
            }

            Func<EvalContext, object> Cmp()
            {
                var l = Add();
                while (IsOp("==", "=", "!=", "<>", "<", ">", "<=", ">="))
                {
                    string op = Cur.Text; _p++;
                    var a = l; var b = Add();
                    switch (op)
                    {
                        case "==": case "=": l = c => Compare(a(c), b(c)) == 0; break;
                        case "!=": case "<>": l = c => Compare(a(c), b(c)) != 0; break;
                        case "<": l = c => Compare(a(c), b(c)) < 0; break;
                        case ">": l = c => Compare(a(c), b(c)) > 0; break;
                        case "<=": l = c => Compare(a(c), b(c)) <= 0; break;
                        default: l = c => Compare(a(c), b(c)) >= 0; break;
                    }
                }
                return l;
            }

            Func<EvalContext, object> Add()
            {
                var l = Mul();
                while (IsOp("+", "-"))
                {
                    bool plus = Cur.Text == "+"; _p++;
                    var a = l; var b = Mul();
                    if (plus) l = c => Plus(a(c), b(c)); else l = c => Minus(a(c), b(c));
                }
                return l;
            }

            static object Plus(object x, object y)
            {
                x = Norm(x); y = Norm(y);
                if (x is string || y is string) return S(x) + S(y);
                if (x is DateTime && (y == null || y is decimal)) return ((DateTime)x).AddDays((double)Num(y));
                return Num(x) + Num(y);
            }

            static object Minus(object x, object y)
            {
                x = Norm(x); y = Norm(y);
                if (x is DateTime && y is DateTime) return (decimal)((DateTime)x - (DateTime)y).TotalDays;
                if (x is DateTime) return ((DateTime)x).AddDays(-(double)Num(y));
                return Num(x) - Num(y);
            }

            Func<EvalContext, object> Mul()
            {
                var l = Unary();
                while (IsOp("*", "/", "%"))
                {
                    string op = Cur.Text; _p++;
                    var a = l; var b = Unary();
                    if (op == "*") l = c => Num(a(c)) * Num(b(c));
                    else if (op == "/") l = c => { var d = Num(b(c)); return d == 0 ? 0m : Num(a(c)) / d; };
                    else l = c => { var d = Num(b(c)); return d == 0 ? 0m : Num(a(c)) % d; };
                }
                return l;
            }

            Func<EvalContext, object> Unary()
            {
                if (IsOp("-")) { _p++; var e = Unary(); return c => -Num(e(c)); }
                if (IsOp("+")) { _p++; return Unary(); }
                if (IsOp("!") || IsWord("not")) { _p++; var e = Unary(); return c => !Truthy(e(c)); }
                return Primary();
            }

            Func<EvalContext, object> Primary()
            {
                var t = Cur;
                switch (t.Kind)
                {
                    case K.Num: _p++; { object n = t.Num; return c => n; }
                    case K.Str: _p++; { object s = t.Text; return c => s; }
                    case K.LP:
                        {
                            _p++;
                            var e = Or();
                            Expect(K.RP);
                            return e;
                        }
                    case K.Ident:
                        {
                            _p++;
                            string name = t.Text;
                            if (Cur.Kind == K.LP) return Call(name);
                            switch (name.ToLowerInvariant())
                            {
                                case "true": return c => true;
                                case "false": return c => false;
                                case "null": return c => null;
                            }
                            return c => c.ResolveIdent(name);
                        }
                }
                throw new FormatException("Unexpected end of expression");
            }

            void Expect(K k)
            {
                if (Cur.Kind != k) throw new FormatException("Expected " + k);
                _p++;
            }

            Func<EvalContext, object> Call(string name)
            {
                string fn = name.ToLowerInvariant();
                _p++; // (
                if (Aggs.Contains(fn))
                {
                    bool all = fn.EndsWith("all");
                    string baseFn = all ? fn.Substring(0, fn.Length - 3) : fn;
                    Func<EvalContext, object> arg = null;
                    string path = null, hint = null;
                    if (Cur.Kind != K.RP)
                    {
                        int start = _p;
                        arg = Or();
                        if (_p - start == 1 && _t[start].Kind == K.Ident) path = _t[start].Text;
                        for (int i = start; i < _p && hint == null; i++)
                            if (_t[i].Kind == K.Ident && _t[i].Text.IndexOf('.') > 0) hint = _t[i].Text.Substring(0, _t[i].Text.IndexOf('.'));
                    }
                    Expect(K.RP);
                    var a = arg; var pth = path; var hnt = hint;
                    return c => c.Aggregate(baseFn, all, a, pth, hnt);
                }

                var args = new List<Func<EvalContext, object>>();
                if (Cur.Kind != K.RP)
                {
                    args.Add(Or());
                    while (Cur.Kind == K.Comma) { _p++; args.Add(Or()); }
                }
                Expect(K.RP);
                return Function(fn, args);
            }

            static Func<EvalContext, object> Function(string fn, List<Func<EvalContext, object>> a)
            {
                Func<int, Func<EvalContext, object>> need = n =>
                {
                    if (a.Count < n) throw new FormatException(fn + " needs " + n + " argument(s)");
                    return null;
                };
                switch (fn)
                {
                    case "iif": case "if": need(3); return c => Truthy(a[0](c)) ? a[1](c) : a[2](c);
                    case "isnull": need(2); return c => { var v = Norm(a[0](c)); return v == null || (v is string && ((string)v).Length == 0) ? a[1](c) : v; };
                    case "round": need(1); return c => Math.Round(Num(a[0](c)), a.Count > 1 ? (int)Num(a[1](c)) : 0, MidpointRounding.AwayFromZero);
                    case "abs": need(1); return c => Math.Abs(Num(a[0](c)));
                    case "int": case "floor": need(1); return c => Math.Floor(Num(a[0](c)));
                    case "ceil": case "ceiling": need(1); return c => Math.Ceiling(Num(a[0](c)));
                    case "max2": need(2); return c => Math.Max(Num(a[0](c)), Num(a[1](c)));
                    case "min2": need(2); return c => Math.Min(Num(a[0](c)), Num(a[1](c)));
                    case "upper": need(1); return c => S(a[0](c)).ToUpperInvariant();
                    case "lower": need(1); return c => S(a[0](c)).ToLowerInvariant();
                    case "trim": need(1); return c => S(a[0](c)).Trim();
                    case "len": need(1); return c => (decimal)S(a[0](c)).Length;
                    case "str": case "text": need(1); return c => S(a[0](c));
                    case "val": need(1); return c => { decimal d; return TryNum(Norm(a[0](c)), out d) ? d : 0m; };
                    case "left": need(2); return c => { var s = S(a[0](c)); int n = Math.Max(0, (int)Num(a[1](c))); return s.Substring(0, Math.Min(n, s.Length)); };
                    case "right": need(2); return c => { var s = S(a[0](c)); int n = Math.Max(0, (int)Num(a[1](c))); return s.Substring(s.Length - Math.Min(n, s.Length)); };
                    case "mid": case "substr":
                        need(2);
                        return c =>
                        {
                            var s = S(a[0](c));
                            int st = Math.Max(1, (int)Num(a[1](c))) - 1;
                            if (st >= s.Length) return "";
                            int len = a.Count > 2 ? Math.Max(0, (int)Num(a[2](c))) : s.Length - st;
                            return s.Substring(st, Math.Min(len, s.Length - st));
                        };
                    case "replace": need(3); return c => S(a[0](c)).Replace(S(a[1](c)), S(a[2](c)));
                    case "contains": need(2); return c => S(a[0](c)).IndexOf(S(a[1](c)), StringComparison.OrdinalIgnoreCase) >= 0;
                    case "concat": return c => string.Concat(a.Select(x => S(x(c))));
                    case "format": need(2); return c => EvalContext.Format(Norm(a[0](c)), S(a[1](c)));
                    case "now": return c => DateTime.Now;
                    case "today": return c => DateTime.Today;
                    case "year": need(1); return c => (decimal)((DateTime)Norm(a[0](c))).Year;
                    case "month": need(1); return c => (decimal)((DateTime)Norm(a[0](c))).Month;
                    case "day": need(1); return c => (decimal)((DateTime)Norm(a[0](c))).Day;
                    case "rowno": return c => (decimal)c.RowNumber;
                }
                throw new FormatException("Unknown function " + fn);
            }
        }
    }
}
