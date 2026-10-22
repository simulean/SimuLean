using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Expressions
{
    /// <summary>A value of an expression: a number, or a text (<see cref="Text"/> not null).</summary>
    public readonly struct ExprValue
    {
        public readonly double Number;
        public readonly string Text;
        public readonly bool Missing;

        private ExprValue(double n, string t, bool missing) { Number = n; Text = t; Missing = missing; }

        public static ExprValue Of(double n) => new ExprValue(n, null, false);
        public static ExprValue Of(string t) => new ExprValue(0, t ?? "", false);
        public static readonly ExprValue None = new ExprValue(0, null, true);

        public bool IsText => Text != null;

        /// <summary>As a number: a text that reads as a number gives it, other texts 0; missing 0.</summary>
        public double AsNumber()
        {
            if (Text == null) return Number;
            return double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
        }

        public bool AsBool() => Text != null ? Text.Length > 0 : Number != 0 && !double.IsNaN(Number);

        public override string ToString() => Missing ? "(none)" : Text ?? Number.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Where an expression reads its values. <paramref name="root"/> is <c>item</c>, <c>element</c> (also
    /// <c>station</c>) or null for a bare name — whose meaning is the context's default (the item's property for
    /// times and priorities, the entry's field in a list).
    /// </summary>
    public interface IExpressionScope
    {
        ExprValue Get(string root, string name);
        /// <summary>Current simulation time (<c>now()</c>).</summary>
        double Now { get; }
    }

    /// <summary>
    /// A scope that reaches the model's data tables: the table functions read <see cref="Data"/> (null = no
    /// model data here, e.g. an optimiser's metric formula — the functions then fail naming the table).
    /// </summary>
    public interface IDataScope
    {
        SimuLean.Data.ModelData Data { get; }
    }

    /// <summary>
    /// A scope with a random stream: the random functions (<c>rand()</c>, <c>uniform</c>,
    /// <c>exponential</c>, <c>normal</c>, <c>pick</c>) draw from <see cref="Random"/> — an element's own stream, so runs are
    /// reproducible for a model seed. Null = no stream here (the functions then fail).
    /// </summary>
    public interface IRandomScope
    {
        SimuLean.Stochastics.Streams.IRandomEngine Random { get; }
    }

    /// <summary>
    /// A scope that reaches the model's other elements: <c>content('Agenda A')</c>,
    /// <c>free('Ward')</c>, <c>get('Ward', 'beds')</c> (a property of the element, else its <c>name</c> / <c>content</c> /
    /// <c>free</c>; review2 #25) — overflow and balancing rules in routing and admission.
    /// </summary>
    public interface IModelScope
    {
        /// <summary>The model's element of that name, or null.</summary>
        SimuLean.Element FindElement(string name);
    }

    /// <summary>
    /// <b>The SimuLean expression syntax</b> — one syntax for every decision a property can drive: process / cycle times,
    /// request priorities, routing, admission conditions, list fields. Parsed once, evaluated many
    /// times without allocating.
    /// <list type="bullet">
    /// <item>Values: numbers <c>12</c>, <c>3.5</c>; texts <c>'red'</c> or <c>"red"</c>.</item>
    /// <item>Properties: <c>item.weight</c>, <c>element.speed</c> (<c>station.speed</c> is the same),
    /// <c>item['any name']</c>; a bare <c>weight</c> is the context's default (the item for times and priorities, the
    /// entry's field in a list). Built-in values when no property has that name: <c>item.id</c>, <c>item.type</c>,
    /// <c>item.priority</c>, <c>item.created</c>, <c>item.age</c>; <c>element.name</c>, <c>element.content</c>,
    /// <c>element.free</c>. A missing value counts as 0 (or as no text).</item>
    /// <item>Operators: <c>+ - * / %</c>, comparisons <c>== != &lt; &lt;= &gt; &gt;=</c> (texts compare with
    /// <c>==</c> / <c>!=</c>; a true comparison is 1), <c>&amp;&amp; || !</c>, <c>cond ? a : b</c>, parentheses.</item>
    /// <item>Functions: <c>min max abs floor ceil round sqrt pow if(c, a, b) now() has(item.x)</c>; random (from
    /// the element's stream, <see cref="IRandomScope"/>): <c>rand()</c> (0–1), <c>uniform(a, b)</c>, <c>exponential(mean)</c>,
    /// <c>normal(mean, sd)</c>, <c>pick(w1, v1, w2, v2, …)</c> (a value with probability proportional to its weight); other
    /// elements by name (<see cref="IModelScope"/>): <c>content('X')</c>, <c>free('X')</c>, <c>get('X', 'property')</c>.</item>
    /// <item>Data tables (the model's named tables, <c>SimContext.Data</c>): <c>table('t', key, 'col')</c> —
    /// the cell of the first row whose first column equals <c>key</c> (<c>table('t', key, 'col', 'keyCol')</c> for
    /// another key column; a number matches a cell of the same value, <c>3</c> = <c>'3.0'</c>); <c>cell('t', row, 'col')</c>
    /// (rows from 1); <c>rows('t')</c> / <c>rows('t', 'col', value)</c> (how many / how many with that value);
    /// <c>sum('t', 'col')</c> / <c>sum('t', 'col', 'keyCol', key)</c>. A cell that reads as a number is a number, else a
    /// text; no such row (or an empty cell) is a missing value (0). An unknown table or column fails, naming it.</item>
    /// </list>
    /// </summary>
    public sealed class Expression
    {
        private readonly Node root;

        public string Text { get; }

        /// <summary>Properties it reads, as <c>root.name</c> (bare names without a root).</summary>
        public IReadOnlyCollection<string> References { get; }

        private Expression(string text, Node root, HashSet<string> refs) { Text = text; this.root = root; References = refs; }

        /// <summary>Parses <paramref name="text"/>; throws <see cref="FormatException"/> (with the position) on a syntax error.</summary>
        public static Expression Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty expression.");
            var p = new Parser(text);
            Node n = p.ParseExpression();
            p.ExpectEnd();
            return new Expression(text, n, p.Refs);
        }

        /// <summary>Null when <paramref name="text"/> parses, else the error message (editors).</summary>
        public static string Check(string text)
        {
            try { Parse(text); return null; } catch (FormatException e) { return e.Message; }
        }

        public ExprValue Evaluate(IExpressionScope scope) => root.Eval(scope);

        /// <summary>The value as a number (NaN-safe: missing / text that is not a number = 0).</summary>
        public double Number(IExpressionScope scope)
        {
            double v = root.Eval(scope).AsNumber();
            return double.IsNaN(v) ? 0 : v;
        }

        /// <summary>The value as a condition (non-zero number / non-empty text).</summary>
        public bool Condition(IExpressionScope scope) => root.Eval(scope).AsBool();

        public override string ToString() => Text;

        // ── syntax tree ──────────────────────────────────────────────────────

        private abstract class Node { public abstract ExprValue Eval(IExpressionScope s); }

        private sealed class Const : Node
        {
            private readonly ExprValue v; public Const(ExprValue v) { this.v = v; }
            public override ExprValue Eval(IExpressionScope s) => v;
        }

        private sealed class Ref : Node
        {
            public readonly string Root, Name;
            public Ref(string root, string name) { Root = root; Name = name; }
            public override ExprValue Eval(IExpressionScope s) => s.Get(Root, Name);
        }

        private sealed class Unary : Node
        {
            private readonly char op; private readonly Node a;
            public Unary(char op, Node a) { this.op = op; this.a = a; }
            public override ExprValue Eval(IExpressionScope s)
            {
                var x = a.Eval(s);
                return op == '-' ? ExprValue.Of(-x.AsNumber()) : ExprValue.Of(x.AsBool() ? 0 : 1);
            }
        }

        private sealed class Binary : Node
        {
            private readonly string op; private readonly Node a, b;
            public Binary(string op, Node a, Node b) { this.op = op; this.a = a; this.b = b; }
            public override ExprValue Eval(IExpressionScope s)
            {
                if (op == "&&") return ExprValue.Of(a.Eval(s).AsBool() && b.Eval(s).AsBool() ? 1 : 0);
                if (op == "||") return ExprValue.Of(a.Eval(s).AsBool() || b.Eval(s).AsBool() ? 1 : 0);
                var x = a.Eval(s); var y = b.Eval(s);
                // texts: equal / different as texts; a missing value equals the empty text; a text against a number
                // compares as numbers
                if ((op == "==" || op == "!=") && (x.IsText || y.IsText) && (!(x.IsText ^ y.IsText) || x.Missing || y.Missing))
                {
                    bool eq = x.IsText && y.IsText ? string.Equals(x.Text, y.Text, StringComparison.Ordinal)
                            : (x.Text ?? "").Length == 0 && (y.Text ?? "").Length == 0;
                    return ExprValue.Of((op == "==") == eq ? 1 : 0);
                }
                double p = x.AsNumber(), q = y.AsNumber();
                switch (op)
                {
                    case "+": return ExprValue.Of(p + q);
                    case "-": return ExprValue.Of(p - q);
                    case "*": return ExprValue.Of(p * q);
                    case "/": return ExprValue.Of(q == 0 ? 0 : p / q);
                    case "%": return ExprValue.Of(q == 0 ? 0 : p % q);
                    case "==": return ExprValue.Of(p == q ? 1 : 0);
                    case "!=": return ExprValue.Of(p != q ? 1 : 0);
                    case "<": return ExprValue.Of(p < q ? 1 : 0);
                    case "<=": return ExprValue.Of(p <= q ? 1 : 0);
                    case ">": return ExprValue.Of(p > q ? 1 : 0);
                    case ">=": return ExprValue.Of(p >= q ? 1 : 0);
                }
                throw new InvalidOperationException(op);
            }
        }

        private sealed class Cond : Node
        {
            private readonly Node c, a, b;
            public Cond(Node c, Node a, Node b) { this.c = c; this.a = a; this.b = b; }
            public override ExprValue Eval(IExpressionScope s) => c.Eval(s).AsBool() ? a.Eval(s) : b.Eval(s);
        }

        private sealed class Call : Node
        {
            private readonly string fn; private readonly Node[] args;
            public Call(string fn, Node[] args) { this.fn = fn; this.args = args; }
            public override ExprValue Eval(IExpressionScope s)
            {
                switch (fn)
                {
                    case "table": case "cell": case "rows": case "sum": return DataCall(s);
                    case "if": return args[0].Eval(s).AsBool() ? args[1].Eval(s) : args[2].Eval(s);
                    case "has": return ExprValue.Of(args[0].Eval(s).Missing ? 0 : 1);
                    case "now": return ExprValue.Of(s.Now);
                    case "rand": return ExprValue.Of(Rng(s).NextDouble());
                    case "uniform":
                    {
                        double a = args[0].Eval(s).AsNumber(), b = args[1].Eval(s).AsNumber();
                        return ExprValue.Of(a + (b - a) * Rng(s).NextDouble());
                    }
                    case "exponential": return ExprValue.Of(-args[0].Eval(s).AsNumber() * Math.Log(1.0 - Rng(s).NextDouble()));
                    case "normal":
                    {
                        double m = args[0].Eval(s).AsNumber(), sd = args[1].Eval(s).AsNumber();
                        var r = Rng(s);
                        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
                        return ExprValue.Of(m + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                    }
                    case "pick": return Pick(s);
                    case "content": return EntityScope.OfElement(Other(s), "content");
                    case "free": return EntityScope.OfElement(Other(s), "free");
                    case "get": { var e = Other(s); return EntityScope.OfElement(e, args[1].Eval(s).ToString()); }
                }
                double x = args.Length > 0 ? args[0].Eval(s).AsNumber() : 0;
                switch (fn)
                {
                    case "abs": return ExprValue.Of(Math.Abs(x));
                    case "floor": return ExprValue.Of(Math.Floor(x));
                    case "ceil": return ExprValue.Of(Math.Ceiling(x));
                    case "round": return ExprValue.Of(Math.Round(x, MidpointRounding.AwayFromZero));
                    case "sqrt": return ExprValue.Of(x < 0 ? 0 : Math.Sqrt(x));
                    case "pow": return ExprValue.Of(Math.Pow(x, args[1].Eval(s).AsNumber()));
                    case "min":
                    {
                        double m = x;
                        for (int i = 1; i < args.Length; i++) m = Math.Min(m, args[i].Eval(s).AsNumber());
                        return ExprValue.Of(m);
                    }
                    case "max":
                    {
                        double m = x;
                        for (int i = 1; i < args.Length; i++) m = Math.Max(m, args[i].Eval(s).AsNumber());
                        return ExprValue.Of(m);
                    }
                }
                throw new InvalidOperationException(fn);
            }

            // the element named by the first argument (I9); an unknown name fails, naming it
            private SimuLean.Element Other(IExpressionScope s)
            {
                string name = args[0].Eval(s).ToString();
                var model = s as IModelScope;
                if (model == null) throw new InvalidOperationException($"'{fn}': no model here (it reads elements in an element's expressions).");
                return model.FindElement(name) ?? throw new InvalidOperationException($"'{fn}': no element '{name}' in the model.");
            }

            private SimuLean.Stochastics.Streams.IRandomEngine Rng(IExpressionScope s)
                => (s as IRandomScope)?.Random
                   ?? throw new InvalidOperationException($"'{fn}': no random stream here (random functions work in an element's expressions).");

            // pick(w1, v1, w2, v2, …): the value of a pair drawn with probability proportional to its weight
            private ExprValue Pick(IExpressionScope s)
            {
                if (args.Length % 2 != 0) throw new InvalidOperationException("'pick' takes weight, value pairs.");
                // each weight evaluated once (review2 #26: twice, two passes disagreed with random weights and drew more)
                Span<double> w = stackalloc double[args.Length / 2];
                double total = 0;
                for (int i = 0; i < w.Length; i++) total += w[i] = Math.Max(0, args[2 * i].Eval(s).AsNumber());
                if (total <= 0) return ExprValue.None;
                double u = Rng(s).NextDouble() * total;
                for (int i = 0; i < w.Length; i++)
                {
                    u -= w[i];
                    if (u < 0) return args[2 * i + 1].Eval(s);
                }
                return args[args.Length - 1].Eval(s);
            }

            // table / cell / rows / sum on the model's data tables
            private ExprValue DataCall(IExpressionScope s)
            {
                string name = args[0].Eval(s).ToString();
                var data = (s as IDataScope)?.Data;
                if (data == null) throw new InvalidOperationException($"'{fn}': no model data tables here (table '{name}').");
                var t = data[name];
                switch (fn)
                {
                    case "table":
                    {
                        string keyColumn = args.Length > 3 ? args[3].Eval(s).ToString() : (t.Columns.Count > 0 ? t.Columns[0] : "");
                        int row = FindRow(t, keyColumn, args[1].Eval(s));
                        return row < 0 ? ExprValue.None : CellValue(t.Text(row, args[2].Eval(s).ToString()));
                    }
                    case "cell":
                    {
                        string column = args[2].Eval(s).ToString();
                        t.Require(column);
                        int row = (int)Math.Round(args[1].Eval(s).AsNumber()) - 1;
                        return row < 0 || row >= t.RowCount ? ExprValue.None : CellValue(t.Text(row, column));
                    }
                    case "rows":
                    {
                        if (args.Length == 1) return ExprValue.Of(t.RowCount);
                        var cells = t.Column(args[1].Eval(s).ToString());
                        var key = args[2].Eval(s);
                        int n = 0;
                        for (int r = 0; r < cells.Count; r++) if (Matches(cells[r], key)) n++;
                        return ExprValue.Of(n);
                    }
                    default:   // sum
                    {
                        var values = t.Column(args[1].Eval(s).ToString());
                        IReadOnlyList<string> keys = null;
                        ExprValue key = ExprValue.None;
                        if (args.Length > 2) { keys = t.Column(args[2].Eval(s).ToString()); key = args[3].Eval(s); }
                        double total = 0;
                        for (int r = 0; r < values.Count; r++)
                            if ((keys == null || Matches(keys[r], key)) && SimuLean.Data.ModelTable.TryNumber(values[r], out double v)) total += v;
                        return ExprValue.Of(total);
                    }
                }
            }

            private static int FindRow(SimuLean.Data.ModelTable t, string keyColumn, ExprValue key)
            {
                if (key.Missing) return -1;
                int row = t.Find(keyColumn, key.ToString());
                if (row >= 0 || key.IsText) return row;
                var cells = t.Column(keyColumn);   // a number: the same value written otherwise ('3.0', '3')
                for (int r = 0; r < cells.Count; r++) if (Matches(cells[r], key)) return r;
                return -1;
            }

            private static bool Matches(string cell, ExprValue key)
            {
                if (key.Missing) return string.IsNullOrEmpty(cell);
                if (key.IsText) return string.Equals(cell, key.Text, StringComparison.Ordinal);
                return SimuLean.Data.ModelTable.TryNumber(cell, out double v) && v == key.Number;
            }

            private static ExprValue CellValue(string text)
            {
                if (string.IsNullOrEmpty(text)) return ExprValue.None;
                return SimuLean.Data.ModelTable.TryNumber(text, out double v) ? ExprValue.Of(v) : ExprValue.Of(text);
            }
        }

        // ── parser (recursive descent) ───────────────────────────────────────

        private static readonly Dictionary<string, (int min, int max)> Functions = new Dictionary<string, (int, int)>
        {
            ["min"] = (1, 64), ["max"] = (1, 64), ["abs"] = (1, 1), ["floor"] = (1, 1), ["ceil"] = (1, 1),
            ["round"] = (1, 1), ["sqrt"] = (1, 1), ["pow"] = (2, 2), ["if"] = (3, 3), ["has"] = (1, 1), ["now"] = (0, 0),
            ["rand"] = (0, 0), ["uniform"] = (2, 2), ["exponential"] = (1, 1), ["normal"] = (2, 2), ["pick"] = (2, 64),
            ["content"] = (1, 1), ["free"] = (1, 1), ["get"] = (2, 2),
            ["table"] = (3, 4), ["cell"] = (3, 3), ["rows"] = (1, 3), ["sum"] = (2, 4),
        };

        private sealed class Parser
        {
            private readonly string s;
            private int i;
            public readonly HashSet<string> Refs = new HashSet<string>();

            public Parser(string s) { this.s = s; }

            private void Skip() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
            private bool Eat(string tok) { Skip(); if (string.CompareOrdinal(s, i, tok, 0, tok.Length) == 0) { i += tok.Length; return true; } return false; }
            private FormatException Error(string what) => new FormatException($"{what} at position {i + 1} in '{s}'.");

            public void ExpectEnd() { Skip(); if (i < s.Length) throw Error($"Unexpected '{s[i]}'"); }

            public Node ParseExpression()
            {
                Node c = ParseOr();
                if (Eat("?"))
                {
                    Node a = ParseExpression();
                    if (!Eat(":")) throw Error("Expected ':'");
                    Node b = ParseExpression();
                    return new Cond(c, a, b);
                }
                return c;
            }

            private Node ParseOr() { Node n = ParseAnd(); while (Eat("||")) n = new Binary("||", n, ParseAnd()); return n; }
            private Node ParseAnd() { Node n = ParseCmp(); while (Eat("&&")) n = new Binary("&&", n, ParseCmp()); return n; }

            private Node ParseCmp()
            {
                Node n = ParseAdd();
                while (true)
                {
                    string op = Eat("==") ? "==" : Eat("!=") ? "!=" : Eat("<=") ? "<=" : Eat(">=") ? ">=" : Eat("<") ? "<" : Eat(">") ? ">" : null;
                    if (op == null) return n;
                    n = new Binary(op, n, ParseAdd());
                }
            }

            private Node ParseAdd()
            {
                Node n = ParseMul();
                while (true)
                {
                    if (Eat("+")) n = new Binary("+", n, ParseMul());
                    else if (Eat("-")) n = new Binary("-", n, ParseMul());
                    else return n;
                }
            }

            private Node ParseMul()
            {
                Node n = ParseUnary();
                while (true)
                {
                    if (Eat("*")) n = new Binary("*", n, ParseUnary());
                    else if (Eat("/")) n = new Binary("/", n, ParseUnary());
                    else if (Eat("%")) n = new Binary("%", n, ParseUnary());
                    else return n;
                }
            }

            private Node ParseUnary()
            {
                if (Eat("-")) return new Unary('-', ParseUnary());
                if (Eat("!")) return new Unary('!', ParseUnary());
                if (Eat("+")) return ParseUnary();
                return ParsePrimary();
            }

            private Node ParsePrimary()
            {
                Skip();
                if (i >= s.Length) throw Error("Unexpected end");
                char c = s[i];
                if (c == '(')
                {
                    i++;
                    Node n = ParseExpression();
                    if (!Eat(")")) throw Error("Expected ')'");
                    return n;
                }
                if (c == '\'' || c == '"') return new Const(ExprValue.Of(ReadString()));
                if (char.IsDigit(c) || c == '.')
                {
                    int start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                    {
                        i++;
                        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                        while (i < s.Length && char.IsDigit(s[i])) i++;
                    }
                    if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        throw Error("Bad number");
                    return new Const(ExprValue.Of(v));
                }
                if (char.IsLetter(c) || c == '_')
                {
                    string id = ReadIdent();
                    Skip();
                    if (i < s.Length && s[i] == '(' && Functions.TryGetValue(id.ToLowerInvariant(), out var arity))
                    {
                        i++;
                        var args = new List<Node>();
                        Skip();
                        if (!Eat(")"))
                        {
                            do args.Add(ParseExpression()); while (Eat(","));
                            if (!Eat(")")) throw Error("Expected ')'");
                        }
                        string fn = id.ToLowerInvariant();
                        if (args.Count < arity.min || args.Count > arity.max) throw Error($"'{fn}' takes {arity.min}–{arity.max} arguments");
                        if (fn == "has" && !(args[0] is Ref)) throw Error("'has' needs a property, e.g. has(item.x)");
                        if (fn == "rows" && args.Count == 2) throw Error("'rows' takes the table, or the table, a column and a value");
                        if (fn == "sum" && args.Count == 3) throw Error("'sum' takes the table and a column, plus a key column and a key");
                        return new Call(fn, args.ToArray());
                    }
                    string root = Root(id);
                    if (root != null)
                    {
                        if (Eat(".")) { Skip(); return Reference(root, ReadIdent()); }
                        if (Eat("[")) { Skip(); string n = ReadString(); if (!Eat("]")) throw Error("Expected ']'"); return Reference(root, n); }
                    }
                    return Reference(null, id);
                }
                throw Error($"Unexpected '{c}'");
            }

            private Node Reference(string root, string name)
            {
                Refs.Add(root == null ? name : root + "." + name);
                return new Ref(root, name);
            }

            private static string Root(string id)
            {
                switch (id)
                {
                    case "item": return "item";
                    case "element": case "station": return "element";
                }
                return null;
            }

            private string ReadIdent()
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                if (i == start) throw Error("Expected a name");
                return s.Substring(start, i - start);
            }

            private string ReadString()
            {
                char q = s[i];
                if (q != '\'' && q != '"') throw Error("Expected a quoted text");
                int end = s.IndexOf(q, i + 1);
                if (end < 0) throw Error("Unclosed text");
                string v = s.Substring(i + 1, end - i - 1);
                i = end + 1;
                return v;
            }
        }
    }
}
