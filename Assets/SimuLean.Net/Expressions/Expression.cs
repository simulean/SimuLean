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

        /// <summary>As a number: a text that reads as a finite number gives it, other texts 0; missing 0.</summary>
        public double AsNumber()
        {
            if (Text == null) return Number;
            return double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && IsFinite(d) ? d : 0;
        }

        /// <summary>As a condition: a non-zero number is true, 0 and missing are false. A text is not a condition
        /// (<c>'0'</c> and <c>'false'</c> would be true while <c>'0' == 0</c> is true too): it throws — compare it or test it with
        /// <c>has()</c>.</summary>
        public bool AsBool()
        {
            if (Text != null)
                throw new InvalidOperationException($"the text '{Text}' is not a condition (a condition is a number or true / false): " +
                                                    $"compare it (x == '{Text}') or test it with has(x)");
            return Number != 0 && !double.IsNaN(Number);
        }

        /// <summary>True for a number that is neither NaN nor infinite.</summary>
        public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

        public override string ToString() => Missing ? "(none)" : Text ?? Number.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// An expression failed while the model ran: a value that is not a finite number (a division by zero,
    /// <c>sqrt(-1)</c>…), a text used as a condition, a name computed at run time that the model does not have. The run stops;
    /// the message names the simulation time, the element, the field and the expression.
    /// </summary>
    public sealed class ExpressionException : InvalidOperationException
    {
        /// <summary>The simulation time of the evaluation.</summary>
        public double Time { get; }
        /// <summary>The expression's text.</summary>
        public string Expression { get; }
        public ExpressionException(string message, double time, string expression, Exception inner) : base(message, inner)
        { Time = time; Expression = expression; }
    }

    /// <summary>
    /// Where an expression reads its values. <paramref name="root"/> is a root of the context (<c>item</c>, <c>element</c>,
    /// <c>signal</c>…; <see cref="ExpressionContext.Roots"/>) or null for a bare name, whose meaning is the context's default
    /// (<see cref="ExpressionContext.Bare"/>: the item's label for times and priorities, the entry's field in a list).
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
    /// <c>exponential</c>, <c>normal</c>, <c>pick</c>) draw from <see cref="Random"/> — the use's own stream, so runs are
    /// reproducible for a model seed. Null = no stream here (the functions then fail).
    /// </summary>
    public interface IRandomScope
    {
        SimuLean.Stochastics.Streams.IRandomEngine Random { get; }
    }

    /// <summary>
    /// A scope that reaches the model: the functions that read other
    /// elements (<c>content('Ward')</c>, <c>free('Ward')</c>, <c>get('Ward', 'beds')</c>), and the model's warnings (an item
    /// label read but not set).
    /// </summary>
    public interface IModelScope
    {
        /// <summary>The model the expression is evaluated in (null = none).</summary>
        SimuLean.SimContext Model { get; }
    }

    /// <summary>
    /// <b>The SimuLean expression syntax</b> — one syntax for every decision a property can drive: process / cycle times,
    /// request priorities, routing, admission conditions, labels, list fields, reactions (the
    /// reference is <c>Assets/Docs/Expressions.md</c>). Parsed once — for a context (<see cref="ExpressionContext"/>), which
    /// says the roots, the bare name and the function families the place serves —, evaluated many times without allocating.
    /// <list type="bullet">
    /// <item>Values: numbers <c>12</c>, <c>3.5</c>; texts <c>'red'</c> or <c>"red"</c> (a doubled quote inside: <c>'it''s'</c>);
    /// <c>true</c> / <c>false</c> (1 / 0).</item>
    /// <item>References: <c>root.name</c> or <c>root['any name']</c> for the roots of the context; a bare <c>name</c> is the
    /// context's default root. A missing value counts as 0 (or as no text); <c>has(x)</c> tests it and <c>x ?? v</c> gives
    /// <c>v</c> instead.</item>
    /// <item>Operators: <c>+ - * / %</c>, comparisons <c>== != &lt; &lt;= &gt; &gt;=</c> (two texts compare as texts, exact
    /// case; a true comparison is 1), <c>&amp;&amp; || !</c>, <c>cond ? a : b</c>, <c>a ?? b</c>, parentheses.</item>
    /// <item>Functions: <see cref="FunctionArities"/> — math, random (from the use's stream), the model's elements, the data
    /// tables.</item>
    /// </list>
    /// When the model is built, every expression of its config is checked (<see cref="ExpressionCheck"/>): the elements,
    /// tables, columns and element properties it names must exist. An item label is read at run time: the first read of one
    /// the item does not have warns once (<see cref="SimContext.ModelWarnings"/>).
    /// </summary>
    public sealed class Expression
    {
        private readonly Node root;
        private readonly List<Ref> refNodes;
        private readonly List<Call> callNodes;

        public string Text { get; }

        /// <summary>Properties it reads, as <c>root.name</c> (bare names without a root).</summary>
        public IReadOnlyCollection<string> References { get; }

        /// <summary>Where it is written (the roots and functions it may use).</summary>
        public ExpressionContext Context { get; }

        /// <summary>The element (or list, team…) it belongs to, for messages; null when not given.</summary>
        public string Owner { get; }

        /// <summary>The field it is written in (a config key or a description), for messages; null when not given.</summary>
        public string Field { get; }

        /// <summary>The item label its value is written to (a label record, a reaction's item label), or null: the build check
        /// reads the values it may give (<see cref="ExpressionCheck"/>).</summary>
        internal string WritesLabel { get; set; }

        /// <summary>The values it may give when they are all known when the model is built (constants, the branches of
        /// <c>?:</c> / <c>if</c> / <c>pick</c> / <c>??</c>, a constant table's column), else null.</summary>
        internal List<ExprValue> StaticValues(SimContext model)
        {
            var values = new List<ExprValue>();
            return root.Values(model, values) ? values : null;
        }

        /// <summary>
        /// A name computed from an item label (<c>content(item.dest)</c>): when every write of that label the build sees
        /// gives values known then (<paramref name="labels"/>), each must name an object of the function's kind; adds the
        /// messages to <paramref name="errors"/>. Labels written by anything else stay a run-time check.
        /// </summary>
        internal void CheckLabelNames(SimContext model, LabelWrites labels, List<string> errors)
        {
            if (labels == null) return;
            foreach (var c in callNodes)
            {
                if (c.NameKind == null || !(c.First is Ref r) || !IsItemLabel(r)) continue;
                var values = labels.Known(r.Name);
                if (values == null) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (value, by) in values)
                {
                    if (!seen.Add(value)) continue;
                    string problem = c.NameProblem(model, value);
                    if (problem != null)
                        errors.Add($"{Where}: '{Text}': '{c.Fn}' reads the {c.NameKind} named by the item label '{r.Name}', which {by} " +
                                   $"sets to '{value}': {problem}");
                }
            }
        }

        // an item label: item.x, or a bare name where it is the item's label
        private bool IsItemLabel(Ref r) => r.Root == "item" || (r.Root == null && Context.Bare == "item");

        private Expression(string text, Node root, Parser p, ExpressionContext context, string owner, string field)
        {
            Text = text; this.root = root; References = p.Refs; refNodes = p.RefNodes; callNodes = p.CallNodes;
            Context = context; Owner = owner; Field = field;
            foreach (var r in refNodes) r.Expression = this;
        }

        /// <summary>Parses <paramref name="text"/> for code that evaluates it over its own scope (every root and function);
        /// throws <see cref="FormatException"/> (with the position) on a syntax error.</summary>
        public static Expression Parse(string text) => Parse(text, ExpressionContext.Any);

        /// <summary>
        /// Parses <paramref name="text"/> written in <paramref name="context"/>: a syntax error, an unknown function or root, a
        /// root or a function the context does not serve all throw <see cref="FormatException"/> naming the position.
        /// <paramref name="owner"/> / <paramref name="field"/> name where it is (messages; the build check of
        /// <see cref="ExpressionCheck"/> collects every expression parsed with an owner while a model is built).
        /// </summary>
        public static Expression Parse(string text, ExpressionContext context, string owner = null, string field = null)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty expression.");
            context = context ?? ExpressionContext.Any;
            var p = new Parser(text, context);
            Node n = p.ParseExpression();
            p.ExpectEnd();
            var e = new Expression(text, n, p, context, owner, field);
            if (owner != null) ExpressionCheck.Collect(e);
            return e;
        }

        /// <summary>Null when <paramref name="text"/> parses, else the error message (editors).</summary>
        public static string Check(string text) => Check(text, ExpressionContext.Any);

        /// <summary>Null when <paramref name="text"/> parses for <paramref name="context"/>, else the error message (editors,
        /// agent commands).</summary>
        public static string Check(string text, ExpressionContext context)
        {
            try { Parse(text, context); return null; } catch (FormatException e) { return e.Message; }
        }

        /// <summary>
        /// The value over <paramref name="scope"/>. Every number it computes or reads is finite: a NaN or an infinite value (a
        /// division by zero, <c>sqrt(-1)</c>, <c>pow(-1, 0.5)</c>, a label holding NaN) throws an
        /// <see cref="ExpressionException"/> naming the time, the owner, the field and the text —
        /// as does any other failure of the evaluation (a text used as a condition, a computed name the model does not have).
        /// </summary>
        public ExprValue Evaluate(IExpressionScope scope)
        {
            try { return root.Eval(scope); }
            catch (ExpressionException) { throw; }
            catch (InvalidOperationException e) { throw Failure(scope, e); }
        }

        private ExpressionException Failure(IExpressionScope scope, Exception e)
        {
            double now = 0;
            try { now = scope?.Now ?? 0; } catch (Exception) { /* the time is only for the message */ }
            return new ExpressionException($"t={now.ToString("0.###", CultureInfo.InvariantCulture)} {Where}: '{Text}': {e.Message}", now, Text, e);
        }

        /// <summary>True for <c>true</c> / <c>false</c>: constants (1 / 0) of the syntax, never a label or property name.</summary>
        public static bool IsConstantName(string name) => name == "true" || name == "false";

        /// <summary>The value as a number (always finite: <see cref="Evaluate"/>; missing / a text that is not a number = 0).</summary>
        public double Number(IExpressionScope scope) => Evaluate(scope).AsNumber();

        /// <summary>The value as a condition: a non-zero number is true, 0 and missing false; a text throws an
        /// <see cref="ExpressionException"/> (a condition is a number or true / false).</summary>
        public bool Condition(IExpressionScope scope)
        {
            var v = Evaluate(scope);
            if (v.IsText) throw Failure(scope, new InvalidOperationException(TextCondition(v.Text)));
            return v.Number != 0;
        }

        /// <summary>
        /// Parses <paramref name="text"/> written as a condition (an admission, a reaction's condition, a study's <c>where</c>…):
        /// as <see cref="Parse(string, ExpressionContext, string, string)"/>, and a constant text (<c>'yes'</c>) is refused — a
        /// condition is a number or true / false.
        /// </summary>
        public static Expression ParseCondition(string text, ExpressionContext context, string owner = null, string field = null)
        {
            var e = Parse(text, context, owner, field);
            if (e.root is Const c && c.Value.IsText) throw new FormatException(TextCondition(c.Value.Text) + $" in '{text}'.");
            return e;
        }

        internal static string TextCondition(string text)
            => $"the text '{text}' is not a condition (a condition is a number or true / false): compare it (x == '{text}') or test it with has(x)";

        private static string Fmt(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        // a number that is not finite stops the run: what gave it, and what it is
        private static InvalidOperationException NotFinite(double r, string what)
            => new InvalidOperationException($"{what} gives {(double.IsNaN(r) ? "NaN (not a number)" : "an infinite value")}; " +
                                             "an expression's values must be finite numbers");

        // arithmetic on a text: a text is not a number
        internal static string TextArithmetic(string what, string text)
            => $"{what} on the text '{text}': arithmetic takes numbers (a text compares with == / != / < ...; '3' is a text, 3 a number)";

        // a number operand / argument of op or function fn (the message is built only when it fails: no allocation)
        private static double Num(ExprValue v, string fn)
            => v.IsText ? throw new InvalidOperationException(TextArithmetic("'" + fn + "'", v.Text)) : v.Number;

        /// <summary>Why the parameters of the function <paramref name="fn"/> are invalid, or null:
        /// <c>uniform(a, b)</c> needs a &lt;= b, <c>exponential(rate)</c> a rate &gt; 0, <c>normal(mean, sd)</c> sd &gt;= 0, <c>triangular(min, mode, max)</c> min &lt;= mode &lt;= max,
        /// <c>lognormal(mu, sigma)</c> sigma &gt;= 0, <c>clamp(x, lo, hi)</c> lo &lt;= hi (<c>pick</c>: see
        /// <see cref="PickWeightProblem"/>).</summary>
        internal static string RandomProblem(string fn, double a, double b, double c = 0)
        {
            switch (fn)
            {
                case "triangular":
                    return a <= b && b <= c ? null
                        : $"triangular({Fmt(a)}, {Fmt(b)}, {Fmt(c)}): needs min <= mode <= max (triangular(min, mode, max), as the Triangular sampler)";
                case "lognormal":
                    return b < 0 ? $"lognormal({Fmt(a)}, {Fmt(b)}): sigma must be >= 0 (lognormal(mu, sigma): the mean and sd of ln x, as the LogNormal sampler)" : null;
                case "clamp":
                    return b <= c ? null : $"clamp({Fmt(a)}, {Fmt(b)}, {Fmt(c)}): the lower bound is above the upper one (clamp(x, lo, hi) needs lo <= hi)";
                case "uniform":
                    return a > b ? $"uniform({Fmt(a)}, {Fmt(b)}): the minimum is above the maximum (uniform(min, max) needs min <= max)" : null;
                case "exponential":
                    return a > 0 ? null : $"exponential({Fmt(a)}): the rate must be > 0 (exponential(rate): the mean is 1 / rate)";
                case "normal":
                    return b < 0 ? $"normal({Fmt(a)}, {Fmt(b)}): the standard deviation must be >= 0" : null;
            }
            return null;
        }

        internal static string PickWeightProblem(int k, double w)
            => w < 0 ? $"pick: weight {k + 1} is {Fmt(w)}; the weights must be >= 0" : null;

        /// <summary>True when argument <paramref name="i"/> of <paramref name="fn"/> is a number (math and random functions;
        /// <c>pick</c>: its weights).</summary>
        internal static bool NumericArgument(string fn, int i)
        {
            switch (fn)
            {
                case "min": case "max": case "abs": case "floor": case "ceil": case "round": case "sqrt": case "pow":
                case "ln": case "exp": case "clamp":
                case "uniform": case "exponential": case "normal": case "triangular": case "lognormal": return true;
                case "pick": return i % 2 == 0;
            }
            return false;
        }

        public override string ToString() => Text;

        /// <summary>"'M1' (delayStrategy)" — where it is, for messages.</summary>
        public string Where => Owner == null ? Context.Name : $"'{Owner}' ({Field ?? Context.Name})";

        // ── static check (ExpressionCheck) ───────────────────────────────────

        /// <summary>
        /// The names it reads that the built model does not have: an element, a table or a column named by
        /// a constant in a function, an element property (<c>element.p</c>, <c>get('X', 'p')</c>) that is neither a built-in,
        /// a property of the element nor one a reaction sets. <paramref name="owner"/> is its element (null when it belongs to
        /// none). Adds the messages to <paramref name="errors"/>; resolves the constant names once, so the run does not look
        /// them up again.
        /// </summary>
        internal void Resolve(SimContext model, Element owner, ICollection<string> reactionProperties, List<string> errors)
        {
            void Error(string what) => errors.Add($"{Where}: '{Text}': {what}");
            foreach (var c in callNodes) c.Resolve(model, reactionProperties, Error);
            foreach (var r in refNodes)
            {
                if (r.Root != "element" && r.Root != "origin" && r.Root != "destination" && r.Root != "out") continue;
                if (r.Root == "element" && Context.ElementIsOwner && owner != null)
                {
                    if (!PropertyKnown(owner, r.Name, reactionProperties))
                        Error($"element '{owner.Name}' has no property '{r.Name}'" + Suggest(r.Name, PropertyNames(owner, reactionProperties)));
                }
                else
                {
                    bool any = false;
                    var all = new List<string>(EntityScope.ElementBuiltIns);
                    foreach (var e in model.Elements) { any |= PropertyKnown(e, r.Name, reactionProperties); if (e.HasProperties) all.AddRange(e.Properties.Names); }
                    all.AddRange(reactionProperties);
                    if (!any) Error($"no element has a property '{r.Name}'" + Suggest(r.Name, all));
                }
            }
        }

        /// <summary>The element property names it reads: <c>element.p</c>, <c>origin.p</c>,
        /// <c>destination.p</c>, <c>out.p</c> and <c>get(…, 'p')</c>; null when one is computed (<c>get('X', item.p)</c>: any
        /// property may be read).</summary>
        public HashSet<string> ElementPropertiesRead()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in refNodes)
                if (r.Root == "element" || r.Root == "origin" || r.Root == "destination" || r.Root == "out") names.Add(r.Name);
            foreach (var c in callNodes)
            {
                if (c.Fn != "get") continue;
                if (c.SecondName == null) return null;
                names.Add(c.SecondName);
            }
            return names;
        }

        /// <summary>What it reads, as texts for the logic viewer (<see cref="SimuLean.Logic.ModelLogic"/>), each
        /// once in order of appearance: a value as <c>root.name</c> (<c>item.family</c>, <c>element.open</c>, <c>out.content</c>;
        /// a bare name under the context's bare root), a model or table read as its function and the name it reads
        /// (<c>content('Buffer')</c>, <c>get('M1', 'open')</c>, <c>table('Prices')</c>; a computed name as <c>…</c>), the
        /// clock as <c>now()</c> and a random draw as <c>random</c>.</summary>
        public List<string> Reads()
        {
            var reads = new List<string>();
            void Add(string s) { if (!reads.Contains(s)) reads.Add(s); }
            string Q(string name) => name == null ? "…" : "'" + name + "'";
            foreach (var r in refNodes)
                Add(r.Root != null ? r.Root + "." + r.Name : Context.Bare != null ? Context.Bare + "." + r.Name : r.Name);
            foreach (var c in callNodes)
            {
                if (c.NameKind != null) Add(c.Fn + "(" + Q(c.FirstName) + (c.Fn == "get" ? ", " + Q(c.SecondName) : "") + ")");
                else if (c.Fn == "now") Add("now()");
                else if (((IList<string>)RandomFunctions).Contains(c.Fn)) Add("random");
            }
            return reads;
        }

        /// <summary>The element names it gives as constants to the functions that read elements (<c>content('Queue A')</c>…):
        /// what an editor checks against its scene before the model is built.</summary>
        public IEnumerable<string> NamedElements()
        {
            foreach (var c in callNodes)
                if ((c.Fn == "content" || c.Fn == "free" || c.Fn == "get" || c.Fn == "stat") && c.FirstName != null) yield return c.FirstName;
        }

        internal static bool PropertyKnown(Element e, string name, ICollection<string> reactionProperties)
            => ((IList<string>)EntityScope.ElementBuiltIns).Contains(name) || (e.HasProperties && e.Properties.Has(name))
               || reactionProperties.Contains(name);

        internal static List<string> PropertyNames(Element e, ICollection<string> reactionProperties)
        {
            var names = new List<string>(EntityScope.ElementBuiltIns);
            if (e.HasProperties) names.AddRange(e.Properties.Names);
            names.AddRange(reactionProperties);
            return names;
        }

        private static List<string> ElementNames(SimContext model)
        {
            var names = new List<string>();
            foreach (var x in model.Elements) if (x.Name != null) names.Add(x.Name);
            return names;
        }

        private static List<string> ListNames(SimContext model)
        {
            var names = new List<string>();
            foreach (var l in model.Lists) if (l.Name != null) names.Add(l.Name);
            return names;
        }

        internal static string Suggest(string given, IEnumerable<string> options)
        {
            var close = SimuLean.Serialization.NameSuggestions.Closest(given, options);
            return close.Count == 0 ? "" : "; did you mean " + string.Join(" or ", close.ConvertAll(x => "'" + x + "'")) + "?";
        }

        // the first read of an item label (or a list entry's field) that is not set: warn once per reference
        private void WarnMissing(Ref r, IExpressionScope s)
        {
            r.Warned = true;
            string name = r.Root == null ? r.Name : r.Root + "." + r.Name;
            string what = r.Root == null && Context.Bare == "entry" ? "the entry's field" : "the item's label";
            string message = $"[SimuLean] t={s.Now.ToString("0.###", CultureInfo.InvariantCulture)} {Where}: {what} '{r.Name}' is not set " +
                             $"(read as missing, 0 / '') in '{Text}'. Write has({name}) or {name} ?? value where it may be missing; warned once.";
            var model = (s as IModelScope)?.Model;
            if (model != null) model.Warn(message); else SimLog.Warning(message);
        }

        // ── syntax tree ──────────────────────────────────────────────────────

        private abstract class Node
        {
            public abstract ExprValue Eval(IExpressionScope s);
            /// <summary>Adds every value it can give, when they are known when the model is built (constants, the branches of
            /// <c>?:</c> / <c>if</c> / <c>pick</c>, a constant table's column); false when they are not.</summary>
            public virtual bool Values(SimContext model, List<ExprValue> into) => false;
            /// <summary>True when it reads nothing and draws nothing (constants, operators and math functions over them): its
            /// value is known when it is parsed.</summary>
            public virtual bool IsConstant => false;
        }

        private sealed class Const : Node
        {
            public readonly ExprValue Value; public Const(ExprValue v) { Value = v; }
            public override ExprValue Eval(IExpressionScope s) => Value;
            public override bool Values(SimContext model, List<ExprValue> into) { into.Add(Value); return true; }
            public override bool IsConstant => true;
        }

        private sealed class Ref : Node
        {
            public readonly string Root, Name;
            /// <summary>An item label / entry field: read at run time, warned once when missing.</summary>
            public bool Dynamic;
            /// <summary>Inside <c>has()</c> or left of <c>??</c>: a missing value is expected there.</summary>
            public bool Quiet;
            public bool Warned;
            public Expression Expression;
            public Ref(string root, string name) { Root = root; Name = name; }
            public override ExprValue Eval(IExpressionScope s)
            {
                var v = s.Get(Root, Name);
                if (v.Missing) { if (Dynamic && !Quiet && !Warned) Expression.WarnMissing(this, s); return v; }
                return v.Text != null || ExprValue.IsFinite(v.Number) ? v : throw NotFinite(v.Number, "reading " + (Root == null ? Name : Root + "." + Name));
            }
        }

        private sealed class Unary : Node
        {
            private readonly char op; private readonly Node a;
            public Unary(char op, Node a) { this.op = op; this.a = a; }
            public override bool IsConstant => a.IsConstant;
            public override ExprValue Eval(IExpressionScope s)
            {
                var x = a.Eval(s);
                if (op != '-') return ExprValue.Of(x.AsBool() ? 0 : 1);
                return x.Missing ? ExprValue.Of(0) : ExprValue.Of(-Num(x, "-"));
            }
        }

        // a ?? b: a, or b when a is missing
        private sealed class Coalesce : Node
        {
            private readonly Node a, b;
            public Coalesce(Node a, Node b) { this.a = a; this.b = b; }
            public override bool IsConstant => a.IsConstant && b.IsConstant;
            public override ExprValue Eval(IExpressionScope s) { var v = a.Eval(s); return v.Missing ? b.Eval(s) : v; }
            public override bool Values(SimContext model, List<ExprValue> into) => a.Values(model, into) && b.Values(model, into);
        }

        private sealed class Binary : Node
        {
            private readonly string op; private readonly Node a, b;
            public Binary(string op, Node a, Node b) { this.op = op; this.a = a; this.b = b; }
            public override bool IsConstant => a.IsConstant && b.IsConstant;
            public override ExprValue Eval(IExpressionScope s)
            {
                if (op == "&&") return ExprValue.Of(a.Eval(s).AsBool() && b.Eval(s).AsBool() ? 1 : 0);
                if (op == "||") return ExprValue.Of(a.Eval(s).AsBool() || b.Eval(s).AsBool() ? 1 : 0);
                var x = a.Eval(s); var y = b.Eval(s);
                // texts: equal / different as texts (exact case); a missing value equals the empty text; a text against a
                // number compares as numbers
                if ((op == "==" || op == "!=") && (x.IsText || y.IsText) && (!(x.IsText ^ y.IsText) || x.Missing || y.Missing))
                {
                    bool eq = x.IsText && y.IsText ? string.Equals(x.Text, y.Text, StringComparison.Ordinal)
                            : (x.Text ?? "").Length == 0 && (y.Text ?? "").Length == 0;
                    return ExprValue.Of((op == "==") == eq ? 1 : 0);
                }
                // two texts that are not both numbers order as texts (ordinal, not both as 0)
                if (x.IsText && y.IsText && op.Length > 0 && (op[0] == '<' || op[0] == '>') && !(IsNumber(x.Text) && IsNumber(y.Text)))
                {
                    int c = string.CompareOrdinal(x.Text, y.Text);
                    bool ok = op == "<" ? c < 0 : op == "<=" ? c <= 0 : op == ">" ? c > 0 : c >= 0;
                    return ExprValue.Of(ok ? 1 : 0);
                }
                // arithmetic on a text stops the run; comparisons keep their rules above (a text that reads as a number
                // compares as a number with a number)
                if ((op == "+" || op == "-" || op == "*" || op == "/" || op == "%") && (x.IsText || y.IsText))
                    throw new InvalidOperationException(TextArithmetic($"'{op}'", x.IsText ? x.Text : y.Text));
                double p = x.AsNumber(), q = y.AsNumber(), r;
                switch (op)
                {
                    case "+": r = p + q; break;
                    case "-": r = p - q; break;
                    case "*": r = p * q; break;
                    // a division by zero is infinite (or NaN): it stops the run
                    case "/": r = p / q; break;
                    case "%": r = p % q; break;
                    case "==": return ExprValue.Of(p == q ? 1 : 0);
                    case "!=": return ExprValue.Of(p != q ? 1 : 0);
                    case "<": return ExprValue.Of(p < q ? 1 : 0);
                    case "<=": return ExprValue.Of(p <= q ? 1 : 0);
                    case ">": return ExprValue.Of(p > q ? 1 : 0);
                    case ">=": return ExprValue.Of(p >= q ? 1 : 0);
                    default: throw new InvalidOperationException(op);
                }
                if (ExprValue.IsFinite(r)) return ExprValue.Of(r);
                throw NotFinite(r, $"{Fmt(p)} {op} {Fmt(q)}" + (q == 0 && (op == "/" || op == "%") ? " (a division by zero)" : ""));
            }

            private static bool IsNumber(string t) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        private sealed class Cond : Node
        {
            private readonly Node c, a, b;
            public Cond(Node c, Node a, Node b) { this.c = c; this.a = a; this.b = b; }
            public override bool IsConstant => c.IsConstant && a.IsConstant && b.IsConstant;
            public override ExprValue Eval(IExpressionScope s) => c.Eval(s).AsBool() ? a.Eval(s) : b.Eval(s);
            public override bool Values(SimContext model, List<ExprValue> into) => a.Values(model, into) && b.Values(model, into);
        }

        private sealed class Call : Node
        {
            public readonly string Fn; private readonly Node[] args;
            public Call(string fn, Node[] args) { Fn = fn; this.args = args; }

            /// <summary>A math function over constants (not a random, model or table function).</summary>
            public override bool IsConstant
            {
                get
                {
                    switch (Fn)
                    {
                        case "min": case "max": case "abs": case "floor": case "ceil": case "round": case "sqrt": case "pow": case "if":
                        case "ln": case "exp": case "clamp": break;
                        default: return false;
                    }
                    foreach (var a in args) if (!a.IsConstant) return false;
                    return true;
                }
            }

            // a numeric argument: a text stops the run
            private double Arg(IExpressionScope s, int i) => Num(args[i].Eval(s), Fn);

            // The object a constant first argument names: resolved once per model — when the model is built
            // (Resolve) or at its first evaluation — so an evaluation does not look it up by name again. One immutable record
            // replaced as a whole, so a model never reads another model's half.
            private sealed class Target
            {
                public SimContext Model; public Element Element;
                public SimuLean.Resources.ResourceDispatcher Team; public SimuLean.Collections.SimList List;
            }
            private Target target;

            private string ConstText(int i) => i < args.Length && args[i] is Const c && c.Value.IsText ? c.Value.Text : null;
            public string FirstName => ConstText(0);
            /// <summary>The second argument when it is a constant text (<c>get('X', 'p')</c>: the property), else null.</summary>
            public string SecondName => ConstText(1);
            public int Arity => args.Length;
            /// <summary>The first argument (the name of what it reads).</summary>
            public Node First => args.Length > 0 ? args[0] : null;

            /// <summary>The kind of object the first argument names, or null when the function names none.</summary>
            public string NameKind
            {
                get
                {
                    switch (Fn)
                    {
                        case "content": case "free": case "get": case "stat": return "element";
                        case "count": return "element or list";
                        case "team": return "resource team";
                        case "list": return "list";
                        case "table": case "cell": case "rows": case "sum": return "data table";
                    }
                    return null;
                }
            }

            /// <summary>Why <paramref name="name"/> is not an object of the right kind in <paramref name="model"/> (with the
            /// closest names), or null when it is.</summary>
            public string NameProblem(SimContext model, string name)
            {
                switch (Fn)
                {
                    case "content": case "free": case "get": case "stat":
                        if (model.FindElement(name) != null) return null;
                        return $"no element '{name}' in the model" + (model.GetList(name) != null ? $" ('{name}' is a list)" : "") + Suggest(name, ElementNames(model));
                    case "count":
                    {
                        if (model.GetList(name) != null || model.FindElement(name) != null) return null;
                        var names = ElementNames(model); names.AddRange(ListNames(model));
                        return $"no element or list '{name}' in the model" + Suggest(name, names);
                    }
                    case "team":
                        return model.FindTeam(name) != null ? null : $"no resource team '{name}' in the model" + Suggest(name, model.TeamNames);
                    case "list":
                        return model.GetList(name) != null ? null : $"no list '{name}' in the model" + Suggest(name, ListNames(model));
                    case "table": case "cell": case "rows": case "sum":
                    {
                        var data = model.Data;
                        return data != null && data.TryGet(name, out _) ? null : $"no data table '{name}' in the model" + Suggest(name, data?.Names ?? new string[0]);
                    }
                }
                return null;
            }

            public override bool Values(SimContext model, List<ExprValue> into)
            {
                switch (Fn)
                {
                    case "if": return args[1].Values(model, into) && args[2].Values(model, into);
                    case "pick":
                        for (int i = 1; i < args.Length; i += 2) if (!args[i].Values(model, into)) return false;
                        return true;
                    case "table": case "cell":
                    {
                        // a constant table's column: every value it may give (an empty cell is missing)
                        string table = ConstText(0), column = ConstText(2);
                        if (table == null || column == null || model?.Data == null || !model.Data.TryGet(table, out var t) || !t.HasColumn(column)) return false;
                        foreach (string cell in t.Column(column)) { var v = CellValue(cell); if (!v.Missing) into.Add(v); }
                        return true;
                    }
                }
                return false;
            }

            public void Resolve(SimContext model, ICollection<string> reactionProperties, Action<string> error)
            {
                string first = ConstText(0);
                if (first == null) return;
                string problem = NameProblem(model, first);
                if (problem != null) { error($"'{Fn}': {problem}"); return; }
                switch (Fn)
                {
                    case "content": case "free": case "get":
                    {
                        var e = model.FindElement(first);
                        target = new Target { Model = model, Element = e };
                        string p = Fn == "get" ? ConstText(1) : null;
                        if (p != null && !PropertyKnown(e, p, reactionProperties))
                            error($"'get': element '{first}' has no property '{p}'" + Suggest(p, PropertyNames(e, reactionProperties)));
                        return;
                    }
                    case "stat":
                    {
                        var e = model.FindElement(first);
                        target = new Target { Model = model, Element = e };
                        string m = ConstText(1);
                        if (m != null && !SimuLean.Results.ElementMetrics.IsKnown(e, m))
                            error($"'stat': '{m}' is not a statistic of element '{first}'" + Suggest(m, SimuLean.Results.ElementMetrics.Names) +
                                  " (statistics: the ResultMetrics element names, state:X, tally:NAME:STAT)");
                        return;
                    }
                    case "team":
                    {
                        var d = model.FindTeam(first);
                        target = new Target { Model = model, Team = d };
                        string w = ConstText(1);
                        if (w != null && !((IList<string>)SimuLean.Results.TeamMetrics.Names).Contains(w))
                            error($"'team': '{w}' is not a team value" + Suggest(w, SimuLean.Results.TeamMetrics.Names) + $" ({string.Join(", ", SimuLean.Results.TeamMetrics.Names)})");
                        return;
                    }
                    case "list":
                    {
                        var l = model.GetList(first);
                        target = new Target { Model = model, List = l };
                        string w = ConstText(1);
                        if (w != null && !SimuLean.Results.ListMetrics.IsKnown(l, w))
                            error($"'list': '{w}' is not a list value" + Suggest(w, SimuLean.Results.ListMetrics.Names) + $" ({string.Join(", ", SimuLean.Results.ListMetrics.Names)})");
                        return;
                    }
                    case "count":
                    {
                        var l = model.GetList(first);
                        target = new Target { Model = model, List = l, Element = l == null ? model.FindElement(first) : null };
                        return;
                    }
                    case "table": case "cell": case "rows": case "sum":
                    {
                        model.Data.TryGet(first, out var t);
                        // the column arguments: table(t, key, col[, keyCol]), cell(t, row, col), rows(t, col, v), sum(t, col[, keyCol, key])
                        int[] columns = Fn == "table" ? new[] { 2, 3 } : Fn == "cell" ? new[] { 2 } : Fn == "rows" ? new[] { 1 } : new[] { 1, 2 };
                        foreach (int i in columns)
                        {
                            string col = ConstText(i);
                            if (col != null && !t.HasColumn(col))
                                error($"'{Fn}': table '{first}' has no column '{col}'" + Suggest(col, t.Columns));
                        }
                        return;
                    }
                }
            }

            public override ExprValue Eval(IExpressionScope s)
            {
                var v = Value(s);
                return v.Text != null || v.Missing || ExprValue.IsFinite(v.Number) ? v : throw NotFinite(v.Number, $"'{Fn}'");
            }

            private ExprValue Value(IExpressionScope s)
            {
                switch (Fn)
                {
                    case "table": case "cell": case "rows": case "sum": return DataCall(s);
                    case "if": return args[0].Eval(s).AsBool() ? args[1].Eval(s) : args[2].Eval(s);
                    case "has": return ExprValue.Of(args[0].Eval(s).Missing ? 0 : 1);
                    case "now": return ExprValue.Of(s.Now);
                    case "rand": return ExprValue.Of(Rng(s).NextDouble());
                    // an invalid parameter stops the run (a constant one fails the parse): RandomProblem
                    case "uniform":
                    {
                        double a = Arg(s, 0), b = Arg(s, 1);
                        Valid(a, b);
                        return ExprValue.Of(a + (b - a) * Rng(s).NextDouble());
                    }
                    // exponential(rate): the same parameter as the Exponential sampler (rate = 1 / mean)
                    case "exponential":
                    {
                        double rate = Arg(s, 0);
                        Valid(rate, 0);
                        double u = Rng(s).NextDouble();
                        return ExprValue.Of(SimuLean.Stochastics.Distributions.ExponentialDistribution.FromUniform(u, rate));
                    }
                    case "normal":
                    {
                        double m = Arg(s, 0), sd = Arg(s, 1);
                        Valid(m, sd);
                        var r = Rng(s);
                        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
                        return ExprValue.Of(SimuLean.Stochastics.Distributions.BoxMuller.Normal(m, sd, u1, u2));
                    }
                    // triangular(min, mode, max) and lognormal(mu, sigma): the Triangular / LogNormal samplers' parameters
                    case "triangular":
                    {
                        double lo = Arg(s, 0), mode = Arg(s, 1), hi = Arg(s, 2);
                        Valid(lo, mode, hi);
                        double u = Rng(s).NextDouble();
                        return ExprValue.Of(SimuLean.Stochastics.Distributions.TriangularDistribution.FromUniform(u, lo, mode, hi));
                    }
                    case "lognormal":
                    {
                        double mu = Arg(s, 0), sigma = Arg(s, 1);
                        Valid(mu, sigma);
                        var r = Rng(s);
                        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
                        double v = Math.Exp(SimuLean.Stochastics.Distributions.BoxMuller.Normal(mu, sigma, u1, u2));
                        return ExprValue.IsFinite(v) ? ExprValue.Of(v) : throw NotFinite(v, $"lognormal({Fmt(mu)}, {Fmt(sigma)})");
                    }
                    case "pick": return Pick(s);
                    case "content": return EntityScope.OfElement(Other(s), "content");
                    case "free": return EntityScope.OfElement(Other(s), "free");
                    case "get": { var e = Other(s); return EntityScope.OfElement(e, args[1].Eval(s).ToString()); }
                    case "stat":
                    {
                        var e = Other(s);
                        string metric = args[1].Eval(s).ToString();
                        // a statistic with no value yet (NaN) is missing, as one the element does not have yet
                        if (SimuLean.Results.ElementMetrics.TryRead(e, metric, out double v)) return ExprValue.IsFinite(v) ? ExprValue.Of(v) : ExprValue.None;
                        if (!SimuLean.Results.ElementMetrics.IsKnown(e, metric))
                            throw new InvalidOperationException($"'stat': '{metric}' is not a statistic of an element (statistics: {string.Join(", ", SimuLean.Results.ElementMetrics.Names)}, state:X, tally:…).");
                        return ExprValue.None;   // a statistic it does not have yet (a last entry before any)
                    }
                    case "team":
                    {
                        var d = TeamOf(s);
                        string what = args[1].Eval(s).ToString();
                        if (SimuLean.Results.TeamMetrics.TryRead(d, what, s.Now, out double v)) return ExprValue.IsFinite(v) ? ExprValue.Of(v) : ExprValue.None;
                        throw new InvalidOperationException($"'team': '{what}' is not a team value ({string.Join(", ", SimuLean.Results.TeamMetrics.Names)}).");
                    }
                    case "list":
                    {
                        var l = ListOf(s);
                        string what = args[1].Eval(s).ToString();
                        if (SimuLean.Results.ListMetrics.TryRead(l, what, s.Now, out double v)) return ExprValue.IsFinite(v) ? ExprValue.Of(v) : ExprValue.None;
                        throw new InvalidOperationException($"'list': '{what}' is not a list value ({string.Join(", ", SimuLean.Results.ListMetrics.Names)}).");
                    }
                    case "count": return Count(s);
                }
                double x = args.Length > 0 ? Arg(s, 0) : 0;
                switch (Fn)
                {
                    case "abs": return ExprValue.Of(Math.Abs(x));
                    case "floor": return ExprValue.Of(Math.Floor(x));
                    case "ceil": return ExprValue.Of(Math.Ceiling(x));
                    case "round": return ExprValue.Of(Math.Round(x, MidpointRounding.AwayFromZero));
                    case "sqrt": return x >= 0 ? ExprValue.Of(Math.Sqrt(x)) : throw NotFinite(double.NaN, $"sqrt({Fmt(x)})");
                    // ln of 0 or a negative number, an exp that overflows: not finite, stops the run (as sqrt(-1))
                    case "ln": return x > 0 ? ExprValue.Of(Math.Log(x)) : throw NotFinite(x == 0 ? double.NegativeInfinity : double.NaN, $"ln({Fmt(x)})");
                    case "exp":
                    {
                        double r = Math.Exp(x);
                        return ExprValue.IsFinite(r) ? ExprValue.Of(r) : throw NotFinite(r, $"exp({Fmt(x)})");
                    }
                    case "clamp":
                    {
                        double lo = Arg(s, 1), hi = Arg(s, 2);
                        string problem = RandomProblem("clamp", x, lo, hi);
                        if (problem != null) throw new InvalidOperationException(problem);
                        return ExprValue.Of(x < lo ? lo : x > hi ? hi : x);
                    }
                    case "pow":
                    {
                        double y = Arg(s, 1), r = Math.Pow(x, y);
                        return ExprValue.IsFinite(r) ? ExprValue.Of(r) : throw NotFinite(r, $"pow({Fmt(x)}, {Fmt(y)})");
                    }
                    case "min":
                    {
                        double m = x;
                        for (int i = 1; i < args.Length; i++) m = Math.Min(m, Arg(s, i));
                        return ExprValue.Of(m);
                    }
                    case "max":
                    {
                        double m = x;
                        for (int i = 1; i < args.Length; i++) m = Math.Max(m, Arg(s, i));
                        return ExprValue.Of(m);
                    }
                }
                throw new InvalidOperationException(Fn);
            }

            private SimContext ModelOf(IExpressionScope s)
                => (s as IModelScope)?.Model ?? throw new InvalidOperationException($"'{Fn}': no model here (it reads the model in an element's expressions).");

            // the record of a constant name for this model, or null (a computed name, or one not resolved yet)
            private Target Cached(SimContext model) { var t = target; return t != null && ReferenceEquals(t.Model, model) ? t : null; }
            // only a constant name is kept (a computed one may name another object at the next evaluation)
            private bool Constant => args[0] is Const;

            // the element named by the first argument; an unknown name fails, naming it
            private Element Other(IExpressionScope s)
            {
                var model = ModelOf(s);
                var t = Cached(model);
                if (t?.Element != null) return t.Element;
                string name = args[0].Eval(s).ToString();
                var e = model.FindElement(name) ?? throw new InvalidOperationException($"'{Fn}': " + NameProblem(model, name));
                if (Constant) target = new Target { Model = model, Element = e };
                return e;
            }

            private SimuLean.Collections.SimList ListOf(IExpressionScope s)
            {
                var model = ModelOf(s);
                var t = Cached(model);
                if (t?.List != null) return t.List;
                string name = args[0].Eval(s).ToString();
                var l = model.GetList(name) ?? throw new InvalidOperationException($"'{Fn}': " + NameProblem(model, name));
                if (Constant) target = new Target { Model = model, List = l };
                return l;
            }

            private SimuLean.Resources.ResourceDispatcher TeamOf(IExpressionScope s)
            {
                var model = ModelOf(s);
                var t = Cached(model);
                if (t?.Team != null) return t.Team;
                string name = args[0].Eval(s).ToString();
                var d = model.FindTeam(name) ?? throw new InvalidOperationException($"'{Fn}': " + NameProblem(model, name));
                if (Constant) target = new Target { Model = model, Team = d };
                return d;
            }

            // count(X, field, value): the items X holds (its link-layer content) whose label / built-in equals the value, or
            // the entries of the list X whose field does — read-only
            private ExprValue Count(IExpressionScope s)
            {
                var model = ModelOf(s);
                string field = args[1].Eval(s).ToString();
                var want = args[2].Eval(s);
                var t = Cached(model);
                SimuLean.Collections.SimList list;
                Element element;
                if (t != null) { list = t.List; element = t.Element; }
                else
                {
                    string name = args[0].Eval(s).ToString();
                    list = model.GetList(name);
                    element = list == null ? model.FindElement(name) : null;
                    if (list == null && element == null) throw new InvalidOperationException("'count': " + NameProblem(model, name));
                    if (Constant) target = new Target { Model = model, List = list, Element = element };
                }
                int n = 0;
                if (list != null)
                {
                    foreach (var e in list.Entries)
                    {
                        double v = list.GetNumber(e, field);
                        var have = !double.IsNaN(v) ? ExprValue.Of(v) : list.GetText(e, field) is string text ? ExprValue.Of(text) : ExprValue.None;
                        if (Same(have, want)) n++;
                    }
                    return ExprValue.Of(n);
                }
                var inside = element.StatsCollector?.ItemsInside;
                if (inside == null) return ExprValue.Of(0);
                foreach (var item in inside)
                    if (Same(EntityScope.OfItem(item, field, s.Now), want)) n++;
                return ExprValue.Of(n);
            }

            private static bool Same(ExprValue a, ExprValue b)
            {
                if (a.Missing || b.Missing) return a.Missing && b.Missing;
                if (a.IsText && b.IsText) return string.Equals(a.Text, b.Text, StringComparison.Ordinal);
                return a.AsNumber() == b.AsNumber();
            }

            // a random function's parameters (uniform: a, b; exponential: the rate; normal: mean, sd; triangular: min, mode, max;
            // lognormal: mu, sigma)
            private void Valid(double a, double b, double c = 0)
            {
                string problem = RandomProblem(Fn, a, b, c);
                if (problem != null) throw new InvalidOperationException(problem);
            }

            private SimuLean.Stochastics.Streams.IRandomEngine Rng(IExpressionScope s)
                => (s as IRandomScope)?.Random
                   ?? throw new InvalidOperationException($"'{Fn}': no random stream here (random functions work in an element's expressions).");

            // pick(w1, v1, w2, v2, …): the value of a pair drawn with probability proportional to its weight
            private ExprValue Pick(IExpressionScope s)
            {
                if (args.Length % 2 != 0) throw new InvalidOperationException("'pick' takes weight, value pairs.");
                // each weight evaluated once (two passes disagreed with random weights and drew more); a
                // negative weight stops the run; every weight 0 picks nothing (missing)
                Span<double> w = stackalloc double[args.Length / 2];
                double total = 0;
                for (int i = 0; i < w.Length; i++)
                {
                    w[i] = Arg(s, 2 * i);
                    if (w[i] < 0) throw new InvalidOperationException(PickWeightProblem(i, w[i]));
                    total += w[i];
                }
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
                if (data == null) throw new InvalidOperationException($"'{Fn}': no model data tables here (table '{name}').");
                var t = data[name];
                switch (Fn)
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
                            if ((keys == null || Matches(keys[r], key)) && SimuLean.Data.ModelTable.TryNumber(values[r], out double v) && ExprValue.IsFinite(v)) total += v;
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
                // a cell reading as a finite number is a number ('NaN' / 'Infinity' stay texts)
                return SimuLean.Data.ModelTable.TryNumber(text, out double v) && ExprValue.IsFinite(v) ? ExprValue.Of(v) : ExprValue.Of(text);
            }
        }

        // ── parser (recursive descent) ───────────────────────────────────────

        /// <summary>The functions an expression may call and their arities (min, max arguments) — the one list the parser
        /// checks against (and <c>simulean_vocabulary</c> shows).</summary>
        public static IReadOnlyDictionary<string, (int min, int max)> FunctionArities => Functions;

        /// <summary>The roots of a reference — <c>item</c>, <c>element</c>, <c>origin</c>, <c>destination</c>, <c>entry</c>,
        /// <c>signal</c>, <c>out</c> (a routing score's output; one meaning each); a context serves some of them
        /// (<see cref="ExpressionContext.Roots"/>), a bare name is its default.</summary>
        public static readonly IReadOnlyList<string> Roots = new[] { "item", "element", "origin", "destination", "entry", "signal", "out" };

        /// <summary>The random functions (they draw from the use's stream; <see cref="ExpressionContext.Random"/>).</summary>
        public static readonly IReadOnlyList<string> RandomFunctions = new[] { "rand", "uniform", "exponential", "normal", "triangular", "lognormal", "pick" };
        /// <summary>The functions that read the model (<see cref="ExpressionContext.Model"/>).</summary>
        public static readonly IReadOnlyList<string> ModelFunctions = new[] { "content", "free", "get", "stat", "team", "list", "count" };
        /// <summary>The data-table functions (<see cref="ExpressionContext.Data"/>).</summary>
        public static readonly IReadOnlyList<string> DataFunctions = new[] { "table", "cell", "rows", "sum" };

        private static readonly Dictionary<string, (int min, int max)> Functions = new Dictionary<string, (int, int)>
        {
            ["min"] = (1, 64), ["max"] = (1, 64), ["abs"] = (1, 1), ["floor"] = (1, 1), ["ceil"] = (1, 1),
            ["round"] = (1, 1), ["sqrt"] = (1, 1), ["pow"] = (2, 2), ["if"] = (3, 3), ["has"] = (1, 1), ["now"] = (0, 0),
            ["ln"] = (1, 1), ["exp"] = (1, 1), ["clamp"] = (3, 3),
            ["rand"] = (0, 0), ["uniform"] = (2, 2), ["exponential"] = (1, 1), ["normal"] = (2, 2), ["pick"] = (2, 64),
            ["triangular"] = (3, 3), ["lognormal"] = (2, 2),
            ["content"] = (1, 1), ["free"] = (1, 1), ["get"] = (2, 2), ["stat"] = (2, 2), ["team"] = (2, 2), ["list"] = (2, 2), ["count"] = (3, 3),
            ["table"] = (3, 4), ["cell"] = (3, 3), ["rows"] = (1, 3), ["sum"] = (2, 4),
        };

        private sealed class Parser
        {
            private readonly string s;
            private readonly ExpressionContext context;
            private int i;
            public readonly HashSet<string> Refs = new HashSet<string>();
            public readonly List<Ref> RefNodes = new List<Ref>();
            public readonly List<Call> CallNodes = new List<Call>();

            public Parser(string s, ExpressionContext context) { this.s = s; this.context = context; }

            private void Skip() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
            private bool Eat(string tok) { Skip(); if (string.CompareOrdinal(s, i, tok, 0, tok.Length) == 0) { i += tok.Length; return true; } return false; }
            private bool Peek(string tok) { Skip(); return string.CompareOrdinal(s, i, tok, 0, tok.Length) == 0; }
            private FormatException Error(string what) => new FormatException($"{what} at position {i + 1} in '{s}'.");
            private FormatException ErrorAt(int at, string what, string hint = "") => new FormatException($"{what} at position {at + 1} in '{s}'{hint}.");

            // "; did you mean 'min'? (functions: abs, ceil, …)"
            private static string Suggest(string given, IEnumerable<string> options, string what)
            {
                var all = new List<string>(options);
                all.Sort(StringComparer.Ordinal);
                return Expression.Suggest(given, all) + $" ({what}: {string.Join(", ", all)})";
            }

            public void ExpectEnd() { Skip(); if (i < s.Length) throw Error($"Unexpected '{s[i]}'" + OperatorHint()); }

            // The operators of other languages, read here as a stray name or character ("Unexpected 'o'" said nothing)
            private string OperatorHint()
            {
                if (i >= s.Length) return "";
                if (s[i] == '^') return " (a power is pow(x, y))";
                int j = i;
                while (j < s.Length && char.IsLetter(s[j])) j++;
                switch (s.Substring(i, j - i))
                {
                    case "or": return " (or is written ||)";
                    case "and": return " (and is written &&)";
                    case "not": return " (not is written !)";
                    default: return "";
                }
            }

            // a node read as a condition (?:, &&, ||, !, if): a constant text is refused here — a text read at run time
            // as a condition stops the run (ExprValue.AsBool)
            private Node AsCondition(Node n, int at)
            {
                if (n is Const c && c.Value.IsText) throw ErrorAt(at, TextCondition(c.Value.Text));
                return n;
            }

            private int Here() { Skip(); return i; }

            // a text known when parsed (a constant, a choice of constants) used in arithmetic is refused here; a text read
            // at run time in arithmetic stops the run
            private static string StaticText(Node n)
            {
                var values = new List<ExprValue>();
                if (!n.Values(null, values)) return null;
                foreach (var v in values) if (v.IsText) return v.Text;
                return null;
            }

            private Node Arithmetic(string op, Node a, Node b, int at, int bAt)
            {
                string t = StaticText(a);
                if (t != null) throw ErrorAt(at, TextArithmetic($"'{op}'", t));
                t = StaticText(b);
                if (t != null) throw ErrorAt(bAt, TextArithmetic($"'{op}'", t));
                return new Binary(op, a, b);
            }

            // a function's numeric arguments hold no constant text; a random function's constant parameters are valid
            private void CheckArguments(string fn, List<Node> args, List<int> at)
            {
                for (int k = 0; k < args.Count; k++)
                {
                    if (!NumericArgument(fn, k)) continue;
                    string t = StaticText(args[k]);
                    if (t != null) throw ErrorAt(at[k], TextArithmetic($"'{fn}'", t));
                }
                if (fn != "uniform" && fn != "exponential" && fn != "normal" && fn != "triangular" && fn != "lognormal" && fn != "pick"
                    && fn != "clamp") return;
                var values = new List<double>();
                for (int k = 0; k < args.Count; k++)
                {
                    if (fn == "pick" && k % 2 != 0) continue;
                    if (fn == "clamp" && k == 0) { values.Add(0); continue; }   // the bounds decide; x may be anything
                    if (!args[k].IsConstant) return;   // a parameter computed at run time is checked when drawn
                    try { values.Add(args[k].Eval(null).AsNumber()); }
                    catch (InvalidOperationException) { return; }   // 1 / 0 and the like stop the run
                }
                string problem = null;
                if (fn == "pick")
                {
                    for (int k = 0; k < values.Count && problem == null; k++) problem = PickWeightProblem(k, values[k]);
                    if (problem == null && values.TrueForAll(v => v == 0)) problem = "pick: every weight is 0 (nothing to pick)";
                }
                else problem = RandomProblem(fn, values[0], values.Count > 1 ? values[1] : 0, values.Count > 2 ? values[2] : 0);
                if (problem != null) throw ErrorAt(at[0], problem);
            }

            public Node ParseExpression()
            {
                int at = Here();
                Node c = ParseCoalesce();
                if (!Peek("??") && Eat("?"))
                {
                    AsCondition(c, at);
                    Node a = ParseExpression();
                    if (!Eat(":")) throw Error("Expected ':'");
                    Node b = ParseExpression();
                    return new Cond(c, a, b);
                }
                return c;
            }

            // a ?? b (right-associative, below || and above ?:): a missing a is expected, so it is not warned
            private Node ParseCoalesce()
            {
                Node n = ParseOr();
                if (!Eat("??")) return n;
                if (n is Ref r) r.Quiet = true;
                return new Coalesce(n, ParseCoalesce());
            }

            private Node ParseOr()
            {
                int at = Here();
                Node n = ParseAnd();
                while (Eat("||")) { AsCondition(n, at); at = Here(); n = new Binary("||", n, AsCondition(ParseAnd(), at)); }
                return n;
            }

            private Node ParseAnd()
            {
                int at = Here();
                Node n = ParseCmp();
                while (Eat("&&")) { AsCondition(n, at); at = Here(); n = new Binary("&&", n, AsCondition(ParseCmp(), at)); }
                return n;
            }

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
                int at = Here();
                Node n = ParseMul();
                while (true)
                {
                    string op = Eat("+") ? "+" : Eat("-") ? "-" : null;
                    if (op == null) return n;
                    int bAt = Here();
                    n = Arithmetic(op, n, ParseMul(), at, bAt);
                }
            }

            private Node ParseMul()
            {
                int at = Here();
                Node n = ParseUnary();
                while (true)
                {
                    string op = Eat("*") ? "*" : Eat("/") ? "/" : Eat("%") ? "%" : null;
                    if (op == null) return n;
                    int bAt = Here();
                    n = Arithmetic(op, n, ParseUnary(), at, bAt);
                }
            }

            private Node ParseUnary()
            {
                if (Eat("-"))
                {
                    int at = Here();
                    Node a = ParseUnary();
                    string t = StaticText(a);
                    if (t != null) throw ErrorAt(at, TextArithmetic("'-'", t));
                    return new Unary('-', a);
                }
                if (Eat("!")) { int at = Here(); return new Unary('!', AsCondition(ParseUnary(), at)); }
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
                    if (!Eat(")")) throw Error("Expected ')'" + OperatorHint());
                    return n;
                }
                if (c == '\'' || c == '"') return new Const(ExprValue.Of(ReadString()));
                if (char.IsDigit(c) || c == '.')
                {
                    int numberAt = i;
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
                    if (!ExprValue.IsFinite(v)) throw ErrorAt(numberAt, "A number too large (not finite)");
                    return new Const(ExprValue.Of(v));
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int at = i;
                    string id = ReadIdent();
                    Skip();
                    // true / false are the numbers 1 / 0
                    if (IsConstantName(id))
                    {
                        if (i < s.Length && (s[i] == '.' || s[i] == '[' || s[i] == '(')) throw ErrorAt(at, $"'{id}' is a constant (1 / 0), not a root or a function");
                        return new Const(ExprValue.Of(id == "true" ? 1 : 0));
                    }
                    if (i < s.Length && s[i] == '(')
                    {
                        // names are case-sensitive; functions are lower-case
                        string fn = id;
                        if (!Functions.TryGetValue(fn, out var arity))
                        {
                            string lower = id.ToLowerInvariant();
                            throw ErrorAt(at, $"Unknown function '{id}'", Functions.ContainsKey(lower)
                                ? $"; did you mean '{lower}'? (names are case-sensitive: functions are lower-case)" : Suggest(id, Functions.Keys, "functions"));
                        }
                        string family = Family(fn);
                        if (family != null) throw ErrorAt(at, $"'{fn}' is not available in {context.Name} ({family})");
                        i++;
                        var args = new List<Node>();
                        var argAt = new List<int>();
                        Skip();
                        int firstAt = i;
                        if (!Eat(")"))
                        {
                            do { argAt.Add(Here()); args.Add(ParseExpression()); } while (Eat(","));
                            if (!Eat(")")) throw Error("Expected ')'" + OperatorHint());
                        }
                        if (args.Count < arity.min || args.Count > arity.max) throw Error($"'{fn}' takes {arity.min}–{arity.max} arguments");
                        CheckArguments(fn, args, argAt);
                        // has(x): any expression, true when its value is not missing; a missing
                        // label read directly inside it is expected, so it is not warned
                        if (fn == "has" && args[0] is Ref hr) hr.Quiet = true;
                        if (fn == "if") AsCondition(args[0], firstAt);
                        if (fn == "rows" && args.Count == 2) throw Error("'rows' takes the table, or the table, a column and a value");
                        if (fn == "sum" && args.Count == 3) throw Error("'sum' takes the table and a column, plus a key column and a key");
                        var call = new Call(fn, args.ToArray());
                        CallNodes.Add(call);
                        return call;
                    }
                    string root = Root(id);
                    // 'out' is a root only before '.' / '[' (a routing score's output): a bare label named out stays a label
                    if (root == "out" && !(i < s.Length && (s[i] == '.' || s[i] == '['))) root = null;
                    if (root != null)
                    {
                        if (!context.Roots.ContainsKey(root) && context != ExpressionContext.Any)
                            throw ErrorAt(at, $"'{id}.' is not available in {context.Name}", Served());
                        if (Eat(".")) { Skip(); return Reference(root, ReadIdent()); }
                        if (Eat("[")) { Skip(); string n = ReadString(); if (!Eat("]")) throw Error("Expected ']'"); return Reference(root, n); }
                    }
                    else if (i < s.Length && (s[i] == '.' || s[i] == '['))
                    {
                        string lower = id.ToLowerInvariant();
                        throw ErrorAt(at, $"Unknown root '{id}'", Root(lower) != null
                            ? $"; did you mean '{lower}'? (names are case-sensitive: roots are lower-case)" : Suggest(id, Roots, "roots"));
                    }
                    if (context.Bare == null)
                        throw ErrorAt(at, $"A bare name ('{id}') is not available in {context.Name}: write it with its root", Served());
                    return Reference(null, id);
                }
                throw Error($"Unexpected '{c}'" + OperatorHint());
            }

            private string Served()
            {
                var roots = new List<string>(context.Roots.Keys);
                return roots.Count == 0 ? " (no roots here)" : $" (roots here: {string.Join(", ", roots)})";
            }

            // why a function is not available here (null = it is)
            private string Family(string fn)
            {
                if (!context.Random && ((IList<string>)RandomFunctions).Contains(fn)) return "random functions draw from an element's stream";
                if (!context.Model && ((IList<string>)ModelFunctions).Contains(fn)) return "it reads the model's elements";
                if (!context.Data && ((IList<string>)DataFunctions).Contains(fn)) return "it reads the model's data tables";
                return null;
            }

            private Node Reference(string root, string name)
            {
                Refs.Add(root == null ? name : root + "." + name);
                var r = new Ref(root, name)
                {
                    Dynamic = root == "item" || (root == null && context.BareIsDynamic),
                };
                RefNodes.Add(r);
                return r;
            }

            private static string Root(string id)
            {
                switch (id)
                {
                    case "item": return "item";
                    case "element": return "element";
                    case "signal": return "signal";
                    case "origin": return "origin";
                    case "destination": return "destination";
                    case "entry": return "entry";
                    case "out": return "out";
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

            // a quoted text; the quote doubled inside it is the quote itself ('it''s')
            private string ReadString()
            {
                char q = s[i];
                if (q != '\'' && q != '"') throw Error("Expected a quoted text");
                var sb = new System.Text.StringBuilder();
                int k = i + 1;
                while (true)
                {
                    if (k >= s.Length) throw Error("Unclosed text");
                    if (s[k] == q)
                    {
                        if (k + 1 < s.Length && s[k + 1] == q) { sb.Append(q); k += 2; continue; }
                        break;
                    }
                    sb.Append(s[k++]);
                }
                i = k + 1;
                return sb.ToString();
            }
        }
    }
}
