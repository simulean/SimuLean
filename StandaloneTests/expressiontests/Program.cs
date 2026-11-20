// Standalone tests of the common expression syntax (SimuLean.Expressions): parsing, values, texts, item / element /
// built-in references, functions, errors, and every place that uses it (times, priorities, routing, admission, lists).
// Run: dotnet run  (from StandaloneTests/expressiontests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Collections;
using SimuLean.Expressions;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static void Near(double actual, double expected, string what, double tol = 1e-9)
        => Check(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected}, got {actual}");

    static int Main()
    {
        Run("Syntax: arithmetic, precedence, comparisons, logic, conditional, functions, errors", TestSyntax);
        Run("Values: item / element properties, bare names, texts, built-ins, has(), now()", TestValues);
        Run("Uses: process times, priorities, routing, admission, list formulas", TestUses);
        Run("Data tables: table / cell / rows / sum in expressions, a process time read from a table", TestDataTables);
        Run("Evaluation of numbers does not allocate", TestNoAllocation);
        Run("Messages, true / false, state, list fields reach the model, unroutable items, no hidden sampler default", TestMessagesConditionsAndListFields);
        Run("Contexts refuse roots / functions; the build checks every name; a missing item label warns once; ?? and has()",
            TestContextsAndNameChecks);
        Run("A request priority draws from its own stream", TestRequestPriorityOwnStream);
        Run("Origin, stat / team / list / count through the result readers, exponential(rate)", TestResultReadersAndExponential);
        Run("NaN and infinity stop the run, naming time, element, field and expression", TestNotFinite);
        Run("Element names computed from a label written with known values are checked at build", TestComputedNames);
        Run("A text is not a condition (constant: at build; read: at run)", TestTextConditions);
        Run("has() takes any expression; names are case-sensitive; signal. outside a reaction fails the build", TestHasCaseSignal);
        Run("Constant names resolved once, computed list names without allocating", TestNameLookups);
        Run("Arithmetic on a text fails (constant: when parsed; read: at run); random functions refuse invalid parameters",
            TestTextArithmeticAndRandomParameters);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    sealed class Station : Element
    {
        public int Content;
        public Station(SimClock c, string n) : base(n, c) { }
        public override int GetQueueLength() => Content;
        public override int GetFreeCapacity() => 10 - Content;
        public override bool CheckAvailability(Item i) => true;
        public override bool Receive(Item i) => true;
        public override bool Unblock() => false;
    }

    sealed class Vars : IExpressionScope
    {
        public readonly Dictionary<string, ExprValue> V = new Dictionary<string, ExprValue>();
        public double Now => 42;
        public ExprValue Get(string root, string name) => V.TryGetValue((root == null ? "" : root + ".") + name, out var v) ? v : ExprValue.None;
    }

    static void TestSyntax()
    {
        var s = new Vars();
        s.V["a"] = ExprValue.Of(2); s.V["b"] = ExprValue.Of(5);
        double E(string x) => Expression.Parse(x).Number(s);
        Near(E("a + b * 2"), 12, "precedence");
        Near(E("(a + b) * 2"), 14, "parentheses");
        Near(E("-a + 10 % 4"), 0, "unary minus and modulo");
        Near(E("a < b && b <= 5"), 1, "comparisons and &&");
        Near(E("a > b || !(a == 2)"), 0, "|| and !");
        Near(E("a != 2 ? 100 : 200"), 200, "conditional");
        Near(E("a > 1 ? b > 4 ? 1 : 2 : 3"), 1, "nested conditional");
        Near(E("max(a, b, 3) + min(a, b) + abs(-1) + round(2.5) + floor(1.7) + ceil(1.2) + pow(2, 3) + sqrt(16)"),
             5 + 2 + 1 + 3 + 1 + 2 + 8 + 4, "functions");
        Near(E("if(a > 1, b, 0)"), 5, "if()");
        Near(E("missing + 1"), 1, "missing counts as 0");
        Near(E("1.5e1 / 3"), 5, "exponent");
        Near(E("now()"), 42, "now()");
        var ex = Expression.Parse("item.prio * 10 + age + element.speed");
        Check(ex.References.Count == 3 && ex.References.Contains("item.prio") && ex.References.Contains("age") && ex.References.Contains("element.speed"), "references");
        foreach (string bad in new[] { "", "a +", "(a", "foo(1)", "max()", "a $ b", "pow(1)", "'open", "item." })
        {
            bool threw = false;
            try { Expression.Parse(bad); } catch (FormatException) { threw = true; }
            Check(threw, $"'{bad}' rejected");
        }
        Check(Expression.Check("a +")?.Contains("position") == true && Expression.Check("a + 1") == null, "Check() gives the message with the position");
        // other languages' operators: the message names ours ("Unexpected 'o'" alone said nothing)
        Check(Expression.Check("a > 1 or b > 1")?.Contains("||") == true && Expression.Check("a > 1 and b > 1")?.Contains("&&") == true
              && Expression.Check("a ^ 2")?.Contains("pow(") == true && Expression.Check("(a ^ 2)")?.Contains("pow(") == true,
              "or / and / ^ point to || / && / pow: " + Expression.Check("a > 1 or b > 1") + " | " + Expression.Check("(a ^ 2)"));
    }

    static void TestValues()
    {
        var clock = new SimClock(1);
        var press = new Station(clock, "Press") { Content = 3 };
        press.Properties.SetDouble("speed", 2);
        press.Properties.SetString("zone", "A");
        var item = new Item(0);
        item.SetId("Box", 7, 4);
        item.SetLabelValue("weight", 12.5);
        item.SetLabelText("color", "red");
        item.SetLabelText("code", "007");
        var scope = new EntityScope().Set(item, press);
        double N(string x) => Expression.Parse(x).Number(scope);
        bool C(string x) => Expression.Parse(x).Condition(scope);
        Near(N("item.weight * element.speed"), 25, "item and element properties");
        Near(N("weight"), 12.5, "bare name = the item's property");
        Check(Expression.Check("station.speed") != null, "station is not a root (element is the one name)");
        Near(N("item['weight'] + element[\"speed\"]"), 14.5, "bracket names");
        Check(C("item.color == 'red'") && !C("item.color == 'blue'") && C("item.color != \"blue\""), "text comparisons");
        Check(C("element.zone == 'A' && item.weight > 10"), "texts and numbers together");
        Check(C("item.code == 7"), "a text against a number compares as numbers");
        Check(C("item.nothing == ''") && !C("item.color == ''"), "missing equals the empty text");
        Near(N("item.id + item.priority"), 11, "built-ins id and priority");
        Check(C("item.type == 'Box'") && C("element.name == 'Press'"), "built-ins type and name");
        Near(N("element.content + element.free"), 10, "built-ins content and free");
        Check(C("has(item.color)") && !C("has(item.size)") && C("has(element.speed)"), "has()");
        item.SetLabelValue("id", 99);
        Near(N("item.id"), 99, "a property wins over a built-in of the same name");
        Near(N("item.age"), 0, "age = now - created");
        Near(Expression.Parse("item.weight").Number(new EntityScope()), 0, "nothing in scope: 0");
    }

    static void TestUses()
    {
        var clock = new SimClock(1);
        var press = new Station(clock, "Press");
        press.Properties.SetDouble("secondsPerKg", 0.5);
        var item = new Item(0); item.SetLabelValue("weight", 10); item.SetLabelValue("processingTime", 3); item.SetLabelText("color", "red");

        // process times
        Near(Samplers.Sample(new ExpressionSampler("item.weight * element.secondsPerKg + 1"), item, press), 6, "time from item and station");
        Near(Samplers.Sample(new ExpressionSampler("processingTime"), item, press), 3, "a bare label (former LabelExpression scenes)");
        Near(Samplers.Sample(new ExpressionSampler("1 - item.weight"), item, press), 0, "negative times clamp to 0");
        Near(Samplers.Sample(new SimuLean.ConstantRandomProcess(4), item, press), 4, "other samplers unchanged");

        // priorities
        var prio = SimuLean.Serialization.ResourceBuild.PriorityOf("item.color == 'red' ? 10 : 1", press);
        Near(prio(item), 10, "priority expression");

        // routing: value = output index, or mapped
        var outs = new List<Element> { new Station(clock, "A"), new Station(clock, "B") };
        var byIndex = new ExpressionRoutingStrategy("item.color == 'red' ? 1 : 0");
        Check(byIndex.SelectOutput(outs, item, press, null) == 1, "routing by an index expression");
        var mapped = new ExpressionRoutingStrategy("weight", new Dictionary<double, int> { [10] = 0 }) { DefaultIndex = 1 };
        Check(mapped.SelectOutput(outs, item, press, null) == 0, "routing by a mapped value");
        var other = new Item(0); other.SetLabelValue("weight", 3);
        Check(mapped.SelectOutput(outs, other, press, null) == 1, "unmapped -> default");

        // admission
        var cond = new ConditionStrategy("item.weight <= element.maxWeight");
        press.Properties.SetDouble("maxWeight", 12);
        Check(cond.Admits(press, item, null), "admitted (10 <= 12)");
        press.Properties.SetDouble("maxWeight", 8);
        Check(!cond.Admits(press, item, null), "refused (10 > 8)");

        // lists: bare names are the entry's fields, item.x its item's properties
        var list = new SimList("l", clock);
        list.DefineFields(new[]
        {
            new ListFieldSpec("prio", ListFieldKind.Label, "prio"),
            new ListFieldSpec("score", ListFieldKind.Expression, "prio * 10 + (item.color == 'red' ? 5 : 0)"),
        });
        var i1 = new Item(0); i1.SetLabelValue("prio", 2); i1.SetLabelText("color", "red");
        var i2 = new Item(0); i2.SetLabelValue("prio", 3);
        list.PushItem(i1, null); list.PushItem(i2, null);
        Check(list.TryPull(ListQuery.All.OrderBy("score", true), out var first) && first.Item == i2, "orders by the formula (30 > 25)");
        bool threw = false;
        try { new SimList("bad", clock).DefineFields(new[] { new ListFieldSpec("x", ListFieldKind.Expression, "x + 1") }); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "a formula reading its own field is refused");
    }

    static void TestDataTables()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
        var arrivals = new Dictionary<string, List<string>>
        {
            ["Time"] = new List<string> { "0", "0", "0" }, ["Name"] = new List<string> { "src", "src", "src" },
            ["Q"] = new List<string> { "1", "1", "1" }, ["kind"] = new List<string> { "a", "b", "a" },
        };
        var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
        src.Set("mode", "Schedule"); src.Set("arrivalsTable", arrivals);
        var srv = new SimuLean.Serialization.ElementConfig("srv", "MultiServer", "srv");
        srv.Set("capacity", 1);
        srv.Set("delayStrategy.strategyType", "Expression");
        srv.Set("delayStrategy.expression", "table('times', item.kind, 'time')");
        var sink = new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink");
        c.Elements.Add(src); c.Elements.Add(srv); c.Elements.Add(sink);
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "srv", "General"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("srv", "sink", "General"));
        c.Tables.Add(new SimuLean.Serialization.DataTableConfig
        {
            Name = "times",
            Columns = new Dictionary<string, List<string>> { ["kind"] = new List<string> { "a", "b" }, ["time"] = new List<string> { "2", "5" } },
        });
        c.Tables.Add(new SimuLean.Serialization.DataTableConfig
        {
            Name = "orders",
            Columns = new Dictionary<string, List<string>>
            {
                ["id"] = new List<string> { "1", "2", "3", "4" }, ["line"] = new List<string> { "L1", "L2", "L1", "L1" },
                ["qty"] = new List<string> { "3.0", "4", "x", "5" }, ["note"] = new List<string> { "red", "", "blue", "red" },
            },
        });
        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        double last = double.NaN;
        model.Find("sink").ItemEntered += (el, it) => last = clock.GetSimulationTime();
        model.StartModel();
        clock.AdvanceClock(100);
        Near(last, 9, "process times read from the table by the item's kind: 2 + 5 + 2");

        var scope = new EntityScope().Set(null, model.Find("srv"));
        double N(string text) => Expression.Parse(text).Number(scope);
        string T(string text) => Expression.Parse(text).Evaluate(scope).ToString();
        Near(N("table('orders', 2, 'qty')"), 4, "key in the first column, a number matching '2'");
        Near(N("table('orders', 1, 'qty')"), 3, "a cell '3.0' is the number 3");
        Check(T("table('orders', 'red', 'id', 'note')") == "1", "another key column: the first match");
        Check(Expression.Parse("table('orders', 9, 'qty')").Evaluate(scope).Missing, "no such row: missing (0)");
        Check(T("table('orders', 3, 'qty')") == "x", "a cell that is not a number is a text");
        Check(T("cell('orders', 4, 'line')") == "L1" && Expression.Parse("cell('orders', 5, 'line')").Evaluate(scope).Missing, "cell by row (from 1)");
        Near(N("rows('orders')"), 4, "rows");
        Near(N("rows('orders', 'line', 'L1')"), 3, "rows with a value");
        Near(N("sum('orders', 'qty')"), 12, "sum of the numbers (a text cell is skipped)");
        Near(N("sum('orders', 'qty', 'line', 'L1')"), 8, "sum of the rows with a key");
        Near(N("table('times', item.kind, 'time') + 1"), 1, "no item: a missing key reads 0 (+1)");

        bool Fails(string text, string fragment)
        {
            try { Expression.Parse(text).Evaluate(scope); return false; }
            catch (Exception e) { return e.Message.Contains(fragment); }
        }
        Check(Fails("table('nope', 1, 'x')", "'nope'"), "an unknown table names it");
        Check(Fails("table('orders', 1, 'nope')", "'nope'"), "an unknown column names it");
        Check(Fails("cell('orders', 1, 'nope')", "'nope'"), "cell: an unknown column even past the last row");
        Check(Expression.Check("rows('orders', 'line')") != null && Expression.Check("sum('orders', 'qty', 'line')") != null
              && Expression.Check("table('orders', 1)") != null, "wrong argument counts fail when parsed");
        var noData = new Vars();
        bool threw = false;
        try { Expression.Parse("rows('orders')").Evaluate(noData); } catch (InvalidOperationException e) { threw = e.Message.Contains("no model data"); }
        Check(threw, "a scope without model data fails clearly");

        // a list's formula reads a table too
        var list = new SimList("l", clock);
        list.DefineFields(new[] { new ListFieldSpec("due", ListFieldKind.Expression, "table('orders', item.order, 'qty')") });
        var o = new Item(0); o.SetLabelValue("order", 4);
        var entry = list.PushItem(o, null);
        Near(list.GetNumber(entry, "due"), 5, "a list field formula reads a table");
    }

    static SimuLean.Serialization.ElementConfig Cfg(string id, string type) => new SimuLean.Serialization.ElementConfig(id, type, id);

    // src (parts at 0, 1, 2) -> router (capacity 10, routing expression) -> m1 | m2 (5 each) -> sink
    static SimuLean.Serialization.SimulationConfig RoutedModel(string routing)
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 50 };
        var src = Cfg("src", "Source");
        src.Set("mode", "Schedule");
        src.Set("arrivalsTable", new Dictionary<string, List<string>>
        {
            ["Time"] = new List<string> { "0", "1", "2" }, ["Name"] = new List<string> { "P", "P", "P" }, ["Q"] = new List<string> { "1", "1", "1" },
        });
        var router = Cfg("router", "MultiServer");
        router.Set("capacity", 10); router.Set("delayStrategy.strategyType", "Constant"); router.Set("delayStrategy.constantValue", 0.0);
        router.Set("outputStrategy.strategyType", "Expression"); router.Set("outputStrategy.routingExpression", routing);
        c.Elements.Add(src); c.Elements.Add(router);
        foreach (string m in new[] { "m1", "m2" })
        {
            var e = Cfg(m, "MultiServer");
            e.Set("capacity", 1); e.Set("delayStrategy.strategyType", "Constant"); e.Set("delayStrategy.constantValue", 5.0);
            c.Elements.Add(e);
        }
        c.Elements.Add(Cfg("sink", "Sink"));
        foreach (var (a, b) in new[] { ("src", "router"), ("router", "m1"), ("router", "m2"), ("m1", "sink"), ("m2", "sink") })
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(a, b, "General"));
        return c;
    }

    static void TestMessagesConditionsAndListFields()
    {
        // an unknown function or root says so, with the closest names
        string ln = Expression.Check("lg(2)"), root = Expression.Check("Item.w + 1");
        Check(ln != null && ln.Contains("Unknown function 'lg'") && ln.Contains("functions:") && ln.Contains("position 1"), "unknown function named: " + ln);
        Check(root != null && root.Contains("Unknown root 'Item'") && root.Contains("did you mean 'item'"), "unknown root named, with a suggestion: " + root);

        // true / false are 1 / 0, not item labels
        var item = new Item(0);
        var scope = new EntityScope().Set(item, null);
        Near(Expression.Parse("true + true").Number(scope), 2, "true = 1");
        Near(Expression.Parse("false").Number(scope), 0, "false = 0");
        Check(!Expression.Parse("item.flag == true").Condition(scope), "a missing flag is not == true (it was: 0 == 0)");
        item.SetLabelValue("flag", 1);
        Check(Expression.Parse("item.flag == true").Condition(scope), "a flag set to 1 == true");
        bool refused = false;
        try { SimuLean.LabelAssignments.Parse(new[] { "true = 1" }, "Src"); } catch (FormatException e) { refused = e.Message.Contains("constant"); }
        Check(refused, "'true' is refused as a label name");

        // an element's state, a text compared exactly
        var clock = new SimClock(1);
        var press = new Station(clock, "Press");
        var es = new EntityScope().Set(null, press);
        Check(Expression.Parse("element.state == 'IDLE' && get('Press', 'state') == 'IDLE'").Condition(es), "state reads the current state");
        Check(!Expression.Parse("element.state == 'idle'").Condition(es), "texts compare with their case");

        // a list field reads other elements and draws from its own stream, reproducibly
        double Draw()
        {
            var c2 = new SimClock(7);
            var p2 = new Station(c2, "Press") { Content = 3 };
            var list = new SimList("l", c2);
            list.DefineFields(new[] { new ListFieldSpec("f", ListFieldKind.Expression, "content('Press') + rand()") });
            var entry = list.PushItem(new Item(0), null);
            return list.GetNumber(entry, "f");
        }
        double d1 = Draw(), d2 = Draw();
        Check(d1 > 3 && d1 < 4 && d1 == d2, $"list field: content('Press') + rand() = {d1} (same for the seed: {d2})");

        // a routing index that names no output warns once and the diagnosis names it
        var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        model.BuildModel(RoutedModel("5"));
        model.StartModel();
        model.Clock.AdvanceClock(50);
        var warnings = model.Clock.Context.ModelWarnings;
        Check(warnings.Count == 1 && warnings[0].Contains("routing gives output 5 of 2") && warnings[0].Contains("'router'"),
              "one warning for the three unroutable parts: " + string.Join(" | ", warnings));
        var diag = SimuLean.Headless.ModelDiagnosis.Of(model, model.Clock);
        string waits = null;
        foreach (var o in (List<object>)diag["elements"])
        {
            var d = (Dictionary<string, object>)o;
            if ((string)d["name"] == "router") waits = d["waitsFor"] as string;
        }
        Check(waits != null && waits.Contains("routing gives output 5 of 2"), "the diagnosis names the routing, not the outputs: " + waits);
        Check(((List<string>)diag["warnings"]).Count == 1, "the diagnosis lists the model's warnings");

        // an Expression sampler needs its text
        var bad = RoutedModel("0");
        var m1 = bad.Elements.Find(e => e.Id == "m1");
        m1.Set("delayStrategy.strategyType", "Expression");
        string error = null;
        try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(bad); } catch (FormatException e) { error = e.Message; }
        Check(error != null && error.Contains("'m1'") && error.Contains("no expression text"), "an empty Expression sampler fails the build: " + error);
    }

    static string BuildError(SimuLean.Serialization.SimulationConfig c)
    {
        try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(c); return null; }
        catch (FormatException e) { return e.Message; }
    }

    static void TestContextsAndNameChecks()
    {
        // a context refuses what it does not serve, when parsed
        string sig = Expression.Check("signal.time > 2", ExpressionContext.Routing);
        Check(sig != null && sig.Contains("'signal.' is not available in a routing expression") && sig.Contains("roots here: item, element"), "a root the context does not serve: " + sig);
        string fit = Expression.Check("rand() + cost", ExpressionContext.Fitness);
        Check(fit != null && fit.Contains("'rand' is not available in a fitness"), "random functions in a fitness: " + fit);
        Check(Expression.Check("cost * 2", ExpressionContext.Fitness) == null, "a fitness reads its names");
        string bare = Expression.Check("waited > 2", ExpressionContext.ReactionTime);
        Check(bare != null && bare.Contains("bare name"), "a bare name where there is no item: " + bare);

        // the build checks the names: elements, properties, tables, columns — with the closest names
        string E(string routing) => BuildError(RoutedModel(routing));
        string e1 = E("content('m3') > 0 ? 1 : 0");
        Check(e1 != null && e1.Contains("'router' (routing)") && e1.Contains("no element 'm3'") && e1.Contains("did you mean"), "unknown element: " + e1);
        string e2 = E("get('m1', 'spede') > 0 ? 1 : 0");
        Check(e2 != null && e2.Contains("element 'm1' has no property 'spede'"), "unknown property of another element: " + e2);
        string e3 = E("element.spede > 0 ? 1 : 0");
        Check(e3 != null && e3.Contains("element 'router' has no property 'spede'"), "unknown property of its element: " + e3);
        string e4 = E("table('nope', 1, 'x')");
        Check(e4 != null && e4.Contains("no data table 'nope'"), "unknown table: " + e4);
        Check(E("get('m1', 'state') == 'IDLE' ? 0 : 1") == null && E("element.content > 1 ? 1 : 0") == null, "built-ins are known");
        var withProp = RoutedModel("element.lane");
        withProp.Elements.Find(e => e.Id == "router").Set(SimuLean.Serialization.PropertyRecords.Key, new List<SimuLean.Serialization.PropertySettings> { SimuLean.Serialization.PropertySettings.Number("lane", 1) });
        Check(BuildError(withProp) == null, "a declared property is known");

        // texts: a doubled quote, ordinal order of two texts
        Check(Expression.Parse("'it''s'").Evaluate(new Vars()).Text == "it's", "'' is a quote inside a text");
        Check(Expression.Parse("'abc' < 'abd' && 'b' > 'a' && !('10' < '9')").Condition(new Vars()), "two texts order as texts, two numbers as numbers");

        // ?? and has(): a missing value on purpose
        var item = new Item(0);
        var scope = new EntityScope().Set(item, null);
        Near(Expression.Parse("item.x ?? 5").Number(scope), 5, "?? gives the default when missing");
        item.SetLabelValue("x", 2);
        Near(Expression.Parse("item.x ?? 5").Number(scope), 2, "?? keeps a value");

        // a label read but not set warns once at run time, with the time, where and the name; ?? / has() do not warn
        int Warnings(string routing)
        {
            var m = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
            m.BuildModel(RoutedModel(routing));
            m.StartModel();
            m.Clock.AdvanceClock(50);
            var w = m.Clock.Context.ModelWarnings;
            if (routing.Contains("tpye ==")) Check(w.Count == 1 && w[0].Contains("'router' (routing)") && w[0].Contains("'tpye' is not set") && w[0].Contains("t=0"),
                                                   "the warning says when, where and what: " + string.Join(" | ", w));
            return w.Count;
        }
        Check(Warnings("item.tpye == 'P' ? 1 : 0") == 1, "a missing item label warns once for the three parts");
        Check(Warnings("(item.tpye ?? 'P') == 'P' ? 1 : 0") == 0 && Warnings("has(item.tpye) ? 1 : 0") == 0, "?? and has() say a missing label is expected");
    }

    static void TestRequestPriorityOwnStream()
    {
        var clock = new SimClock(7);
        var press = new Station(clock, "Press");
        var prio = SimuLean.Serialization.ResourceBuild.PriorityOf("rand()", press, "priority:processBy.priority");
        double first = prio(new Item(0));
        var expected = new SimuLean.Stochastics.Streams.DefaultRandomEngine(SimContext.DeriveStreamSeed(7, "Press/priority:processBy.priority")).NextDouble();
        var fallback = new SimuLean.Stochastics.Streams.DefaultRandomEngine(SimContext.DeriveStreamSeed(7, "Press/expr")).NextDouble();
        Check(first == expected && first != fallback, $"the priority's stream is Press/priority:processBy.priority ({first}; own {expected}, element's {fallback})");
    }

    static void TestResultReadersAndExponential()
    {
        // origin.x: the sending element in an admission condition; a root a context does not serve fails when parsed
        var clock = new SimClock(1);
        var press = new Station(clock, "Press");
        var a = new Station(clock, "A");
        var cond = new ConditionStrategy("origin.name == 'A'");
        Check(cond.Admits(press, new Item(0), a) && !cond.Admits(press, new Item(0), press), "admission reads origin.name");
        Check(Expression.Check("origin.content", ExpressionContext.Time) != null, "a time has no origin");

        // stat(): the element's statistics, live, by their ResultMetrics names; the run then goes as the rule says
        string Run3(string routing)
        {
            var m = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
            var el = m.BuildModel(RoutedModel(routing));
            var seen = new List<string>();
            foreach (var n in new[] { "m1", "m2" }) { string k = n; el[n].ItemEntered += (e, it) => seen.Add(k); }
            m.StartModel();
            m.Clock.AdvanceClock(50);
            return string.Join(" ", seen);
        }
        Check(Run3("stat('m1', 'input') < 1 ? 0 : 1") == "m1 m2 m2", "stat('m1', 'input'): " + Run3("stat('m1', 'input') < 1 ? 0 : 1"));
        Check(Run3("stat('m1', 'state:PROCESSING') > 0 ? 1 : 0") == "m1 m2 m2", "stat of a state share");
        {
            // count(): the items an element holds (here three unroutable parts) whose label / built-in has the value
            var held = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
            held.BuildModel(RoutedModel("5"));
            held.StartModel();
            held.Clock.AdvanceClock(50);
            var at = new EntityScope().Set(null, held.Find("m1"));
            Near(Expression.Parse("count('router', 'type', 'P')").Number(at), 3, "count(element, label, value) over the items inside");
            Near(Expression.Parse("count('router', 'type', 'Q') + count('m1', 'type', 'P')").Number(at), 0, "count of another value / an empty element");
        }
        string E(string routing) => BuildError(RoutedModel(routing));
        string e1 = E("stat('m1', 'outptu') > 1 ? 1 : 0");
        Check(e1 != null && e1.Contains("'outptu' is not a statistic") && e1.Contains("'output'"), "an unknown statistic fails the build: " + e1);
        string e2 = E("team('ops', 'free') > 0 ? 1 : 0");
        Check(e2 != null && e2.Contains("no resource team 'ops'"), "an unknown team fails the build: " + e2);
        string e3 = E("list('orders', 'content') > 0 ? 1 : 0");
        Check(e3 != null && e3.Contains("no list 'orders'"), "an unknown list fails the build: " + e3);

        // team() and list() read the same values as the results
        var c = RoutedModel("team('ops', 'free') > 0 && list('orders', 'content') == 0 ? 0 : 1");
        var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "ops", Name = "ops" };
        team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "op1" });
        c.ResourceTeams.Add(team);
        c.Lists.Add(new SimuLean.Serialization.ListConfig { Id = "orders", Name = "orders" });
        Check(BuildError(c) == null, "team() and list() build: " + BuildError(c));
        var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        model.BuildModel(c);
        model.StartModel();
        model.Clock.AdvanceClock(50);
        var results = SimuLean.Results.RunResults.Capture(model);
        Check(model.Find("m1").GetStatsInputCount() == 3, "free team, empty list: every part to m1");
        SimuLean.Results.TeamMetrics.TryRead(model.Teams["ops"], "members", 50, out double members);
        Check(members == results.Find(SimuLean.Results.ResultScope.Team, "ops")["members"], "team() and the results read one value");
        var c2 = RoutedModel("team('ops', 'fre') > 0 ? 0 : 1");
        c2.ResourceTeams.Add(team);
        string e4 = BuildError(c2);
        Check(e4 != null && e4.Contains("'fre' is not a team value") && e4.Contains("'free'"), "an unknown team value fails the build: " + e4);

        // exponential(rate): the sampler's parameter (mean 1 / rate)
        var sampler = new ExpressionSampler("exponential(0.5)");
        sampler.SetRandomEngine(new SimuLean.Stochastics.Streams.DefaultRandomEngine(3));
        double sum = 0;
        for (int i = 0; i < 20000; i++) sum += sampler.NextValue();
        Near(sum / 20000, 2, "exponential(0.5) has mean 2", 0.08);
    }

    // the message of the ExpressionException <paramref name="run"/> throws, or null
    static string RunError(Action run)
    {
        try { run(); return null; }
        catch (ExpressionException e) { return e.Message; }
    }

    static void TestNotFinite()
    {
        var clock = new SimClock(1);
        var press = new Station(clock, "Press");
        var item = new Item(0); item.SetLabelValue("k", 0); item.SetLabelValue("x", -1);
        var scope = new EntityScope().Set(item, press);
        string Fails(string text) => RunError(() => Expression.Parse(text, ExpressionContext.Time, "Press", "delayStrategy").Number(scope));
        string div = Fails("10 / item.k");
        Check(div != null && div.Contains("t=0 'Press' (delayStrategy): '10 / item.k'") && div.Contains("10 / 0 (a division by zero)")
              && div.Contains("infinite"), "a division by zero stops, naming time, element, field and text: " + div);
        Check(Fails("0 / item.k")?.Contains("NaN") == true && Fails("5 % item.k")?.Contains("division by zero") == true, "0 / 0 and % 0 are NaN");
        Check(Fails("sqrt(item.x)")?.Contains("sqrt(-1) gives NaN") == true, "sqrt(-1): " + Fails("sqrt(item.x)"));
        Check(Fails("pow(item.x, 0.5)")?.Contains("pow(-1, 0.5) gives NaN") == true, "pow(-1, 0.5): " + Fails("pow(item.x, 0.5)"));
        Check(Fails("pow(10, 400)")?.Contains("infinite") == true, "an overflow is infinite");
        Check(Fails("max(1, 10 / item.k)") != null && Fails("10 / item.k > 1 ? 1 : 0") != null && Fails("has(10 / item.k)") != null,
              "an intermediate value consumed by a function, a comparison or has() stops too");
        item.SetLabelValue("bad", double.NaN);
        Check(Fails("item.bad + 1")?.Contains("reading item.bad gives NaN") == true, "a label holding NaN stops when read: " + Fails("item.bad + 1"));
        Check(Expression.Check("1e999") != null, "a number literal too large is refused when parsed");
        Check(RunError(() => Expression.Parse("10 / item.k > 1").Condition(scope)) != null, "a condition over NaN / infinity stops (it was false)");
        Near(Expression.Parse("sqrt(item.k) + pow(2, 3) + 10 / 4").Number(scope), 10.5, "finite values are unchanged");

        // a routing that yields NaN stops the run (it went to output 0), a time that yields infinity too
        var routed = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        routed.BuildModel(RoutedModel("0 / 0"));
        routed.StartModel();
        string route = RunError(() => routed.Clock.AdvanceClock(50));
        Check(route != null && route.Contains("t=0 'router' (routing): '0 / 0'") && route.Contains("NaN"), "a NaN routing stops the run: " + route);
        var timed = RoutedModel("0");
        var m1 = timed.Elements.Find(e => e.Id == "m1");
        m1.Set("delayStrategy.strategyType", "Expression"); m1.Set("delayStrategy.expression", "5 / (item.id - 1)");
        var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        model.BuildModel(timed);
        model.StartModel();
        string time = RunError(() => model.Clock.AdvanceClock(50));
        Check(time != null && time.Contains("'m1' (delayStrategy)") && time.Contains("division by zero"), "a time that divides by zero stops the run: " + time);

        // a label record, a list field and a fitness
        var labelled = RoutedModel("0");
        labelled.Elements.Find(e => e.Id == "router").Set("entryLabels", new List<string> { "r = sqrt(0 - item.id)" });
        var lm = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        lm.BuildModel(labelled);
        lm.StartModel();
        string label = RunError(() => lm.Clock.AdvanceClock(50));
        Check(label != null && label.Contains("'router' (entryLabel:r)") && label.Contains("sqrt(-1)"), "a label record: " + label);
        var list = new SimList("l", clock);
        list.DefineFields(new[] { new ListFieldSpec("f", ListFieldKind.Expression, "1 / item.k") });
        string field = RunError(() => list.GetNumber(list.PushItem(item, null), "f"));
        Check(field != null && field.Contains("'l' (field f)") && field.Contains("division by zero"), "a list field: " + field);
        var fitness = Expression.Parse("cost / n", ExpressionContext.Fitness);
        var vars = new Vars(); vars.V["cost"] = ExprValue.Of(3); vars.V["n"] = ExprValue.Of(0);
        Check(RunError(() => fitness.Number(vars)) != null, "a fitness dividing by zero fails (it gave 0)");
    }

    static void TestComputedNames()
    {
        // src gives each part a label 'dest'; the router sends it to output 1 when content(item.dest) is not 0
        string Build(string record, string routing = "content(item.dest) > 9 ? 1 : 0", Action<SimuLean.Serialization.SimulationConfig> more = null)
        {
            var c = RoutedModel(routing);
            if (record != null) c.Elements.Find(e => e.Id == "src").Set("createLabels", new List<string> { record });
            more?.Invoke(c);
            return BuildError(c);
        }
        string e1 = Build("dest = 'm3'");
        Check(e1 != null && e1.Contains("'router' (routing)") && e1.Contains("item label 'dest'") && e1.Contains("'src' (createLabel:dest)")
              && e1.Contains("no element 'm3'") && e1.Contains("did you mean"), "a constant label naming no element fails the build: " + e1);
        Check(Build("dest = 'm1'") == null, "a constant label naming an element builds");
        string e2 = Build("dest = item.id > 1 ? 'm1' : 'mm2'");
        Check(e2 != null && e2.Contains("'mm2'") && !e2.Contains("'m1':"), "each branch of a choice is checked: " + e2);
        Check(Build("dest = pick(1, 'm1', 1, 'm2')") == null, "pick of known names builds");
        Check(Build("dest = item.type") == null, "a label computed at run time is not checked at build");
        Check(Build(null) == null, "a label nobody writes is not checked (set by code)");
        // a data-table column of literals, an arrivals table column
        string e3 = Build("dest = table('routes', item.type, 'to')", more: c => c.Tables.Add(new SimuLean.Serialization.DataTableConfig
        {
            Name = "routes",
            Columns = new Dictionary<string, List<string>> { ["type"] = new List<string> { "P", "Q" }, ["to"] = new List<string> { "m1", "m4" } },
        }));
        Check(e3 != null && e3.Contains("'m4'"), "a table column of literals is checked: " + e3);
        string e4 = Build(null, more: c =>
        {
            var arrivals = c.Elements.Find(e => e.Id == "src").Get<Dictionary<string, List<string>>>("arrivalsTable");
            arrivals["dest"] = new List<string> { "m1", "m2", "M2" };
        });
        Check(e4 != null && e4.Contains("'src' (arrivals table)") && e4.Contains("'M2'"), "an arrivals-table column is checked: " + e4);
        // other kinds: team / list names
        string e5 = Build("t = 'crew'", "team(item.t, 'free') > 0 ? 1 : 0");
        Check(e5 != null && e5.Contains("no resource team 'crew'"), "a team named by a label: " + e5);
        // still a run-time error when not known at build, with the time and the place
        var c2 = RoutedModel("content(item.dest) > 9 ? 1 : 0");
        c2.Elements.Find(e => e.Id == "src").Set("createLabels", new List<string> { "dest = item.type" });
        var m = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        m.BuildModel(c2);
        m.StartModel();
        string run = RunError(() => m.Clock.AdvanceClock(50));
        Check(run != null && run.Contains("t=0 'router' (routing)") && run.Contains("no element 'P'"), "an unknown computed name stops the run: " + run);
    }

    static void TestTextConditions()
    {
        foreach (string text in new[] { "'yes' ? 1 : 0", "'a' && 1", "1 || 'false'", "!'0'", "if('x', 1, 2)" })
        {
            string error = Expression.Check(text);
            Check(error != null && error.Contains("is not a condition"), $"a constant text as a condition is refused: {text} -> {error}");
        }
        bool refused = false;
        try { new ConditionStrategy("'0'"); } catch (FormatException e) { refused = e.Message.Contains("not a condition"); }
        Check(refused, "an admission condition that is a constant text fails the build");
        Check(Expression.Check("item.color == 'red' ? 'a' : 'b'") == null, "texts as values stay");
        var item = new Item(0); item.SetLabelText("color", "0"); item.SetLabelValue("n", 0);
        var scope = new EntityScope().Set(item, null);
        string run = RunError(() => Expression.Parse("item.color ? 1 : 2", ExpressionContext.Routing, "router", "routing").Number(scope));
        Check(run != null && run.Contains("'router' (routing)") && run.Contains("the text '0' is not a condition"), "a text read as a condition stops: " + run);
        Check(RunError(() => Expression.Parse("item.color").Condition(scope)) != null, "a text as the whole condition stops");
        Check(Expression.Parse("item.color == '0' && item.n == 0 && !item.n && !item.none").Condition(scope), "numbers, comparisons and missing values stay conditions");
    }

    static void TestHasCaseSignal()
    {
        var clock = new SimClock(1);
        var press = new Station(clock, "Press");
        press.Properties.SetDouble("speed", 2);
        var item = new Item(0); item.SetLabelValue("x", 1);
        var scope = new EntityScope().Set(item, press);
        bool C(string x) => Expression.Parse(x).Condition(scope);
        Check(C("has(get('Press', 'speed'))") && !C("has(get('Press', 'nope'))"), "has(get(...))");
        Check(C("has(item.x + 1)") && !C("has(item.y ?? item.z)") && C("has(1)"), "has() of any expression");
        Check(Expression.Check("has(table('t', item.k, 'v'))") == null, "has(table(...)) parses");

        string max = Expression.Check("MAX(1, 2)"), root = Expression.Check("Item.x + 1"), round = Expression.Check("Round(1.5)");
        Check(max != null && max.Contains("Unknown function 'MAX'") && max.Contains("did you mean 'max'") && max.Contains("case-sensitive"), "MAX: " + max);
        Check(round != null && round.Contains("did you mean 'round'"), "Round: " + round);
        Check(root != null && root.Contains("did you mean 'item'") && root.Contains("case-sensitive"), "Item.: " + root);
        Check(Expression.Check("max(1, 2)") == null, "lower-case functions");

        string sig = BuildError(RoutedModel("signal.time > 1 ? 1 : 0"));
        Check(sig != null && sig.Contains("'signal.' is not available in a routing expression"), "signal. outside a reaction fails the build: " + sig);
    }

    static void TestNameLookups()
    {
        var clock = new SimClock(1);
        var press = new Station(clock, "Press") { Content = 2 };
        var lists = new List<SimList>();
        for (int i = 0; i < 5; i++) lists.Add(new SimList("L" + i, clock));
        lists[3].PushValue("order", new Dictionary<string, object> { ["v"] = 1.0 }, null);
        var item = new Item(0); item.SetLabelText("l", "L3"); item.SetLabelText("e", "Press");
        var scope = new EntityScope().Set(item, press);
        Near(Expression.Parse("count('L3', 'v', 1) + count(item.l, 'v', 1)").Number(scope), 2, "count by a constant and a computed name");
        var constant = Expression.Parse("content('Press') + list('L3', 'content')");
        var computed = Expression.Parse("content(item.e) + list(item.l, 'content')");
        Near(constant.Number(scope), 3, "constant names");
        Near(computed.Number(scope), 3, "computed names");
        double sum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) sum += constant.Number(scope) + computed.Number(scope);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Check(after - before == 0, $"constant and computed names read without allocating ({after - before} bytes, sum {sum})");

        Near(sum, 60000, "the same values every time");
    }

    static void TestTextArithmeticAndRandomParameters()
    {
        // a constant text in arithmetic or a numeric argument fails the parse, naming the text and the position
        foreach (string text in new[] { "'a' + 1", "1 - 'a'", "-'x'", "'3' * 2", "6 / '2'", "5 % 'x'", "(item.k > 1 ? 'a' : 'b') + 1",
                                        "abs('x')", "max(1, 'x')", "pow('2', 2)", "uniform('a', 2)", "exponential('1')", "pick('1', 'a', 1, 'b')" })
        {
            string error = Expression.Check(text);
            Check(error != null && error.Contains("on the text") && error.Contains("position"), $"arithmetic on a constant text is refused: {text} -> {error}");
        }
        // texts as values, comparisons and orders stay
        foreach (string text in new[] { "item.color == 'red' ? 'a' : 'b'", "'abc' < 'abd'", "item.code == 7", "pick(1, 'a', 1, 'b')", "item.x ?? 'none'" })
            Check(Expression.Check(text) == null, $"texts as values / comparisons stay: {text} -> {Expression.Check(text)}");

        // a text read at run time in arithmetic stops the run, naming time, element, field and text
        var clock = new SimClock(1);
        var press = new Station(clock, "Press");
        var item = new Item(0); item.SetLabelText("color", "red"); item.SetLabelText("code", "007"); item.SetLabelValue("r", 0);
        item.SetLabelValue("lo", 5); item.SetLabelValue("sd", -1); item.SetLabelValue("w", -2);
        var scope = new EntityScope().Set(item, press);
        scope.RandomStream = new SimuLean.Stochastics.Streams.DefaultRandomEngine(1);
        string Fails(string text) => RunError(() => Expression.Parse(text, ExpressionContext.Time, "Press", "delayStrategy").Number(scope));
        string plus = Fails("item.color + 1");
        Check(plus != null && plus.Contains("t=0 'Press' (delayStrategy): 'item.color + 1'") && plus.Contains("'+' on the text 'red'"),
              "a text label in arithmetic stops the run: " + plus);
        Check(Fails("item.code * 2")?.Contains("on the text '007'") == true, "a text that reads as a number is still a text in arithmetic");
        Check(Fails("-item.color") != null && Fails("max(1, item.color)")?.Contains("'max' on the text") == true, "unary minus and a function argument");
        Check(Expression.Parse("item.code == 7 && item.none + 1 == 1").Condition(scope), "comparisons and missing values are unchanged");

        // random functions: an invalid constant parameter fails the parse, a computed one stops the run
        foreach (var (text, says) in new[] { ("exponential(0)", "rate must be > 0"), ("exponential(-1)", "rate must be > 0"),
                                             ("uniform(5, 2)", "minimum is above the maximum"), ("normal(10, -1)", "standard deviation must be >= 0"),
                                             ("pick(-1, 'a', 2, 'b')", "weights must be >= 0"), ("pick(0, 'a', 0, 'b')", "every weight is 0"),
                                             ("exponential(1 - 2)", "rate must be > 0") })
        {
            string error = Expression.Check(text);
            Check(error != null && error.Contains(says), $"an invalid constant parameter fails the parse: {text} -> {error}");
        }
        string rate = Fails("exponential(item.r)");
        Check(rate != null && rate.Contains("t=0 'Press' (delayStrategy): 'exponential(item.r)'") && rate.Contains("exponential(0): the rate must be > 0"),
              "a computed rate of 0 stops the run (it drew 0): " + rate);
        Check(Fails("uniform(item.lo, 2)")?.Contains("uniform(5, 2)") == true, "a computed uniform with min > max stops: " + Fails("uniform(item.lo, 2)"));
        Check(Fails("normal(1, item.sd)")?.Contains("standard deviation") == true, "a computed negative sd stops");
        Check(Fails("pick(item.w, 1, 1, 2)")?.Contains("weight 1 is -2") == true, "a computed negative weight stops (it counted as 0): " + Fails("pick(item.w, 1, 1, 2)"));
        Check(!Expression.Parse("pick(item.r, 1, item.r, 2)", ExpressionContext.Time).Evaluate(scope).IsText
              && Expression.Parse("has(pick(item.r, 1, item.r, 2))", ExpressionContext.Time).Number(scope) == 0, "computed weights all 0 pick nothing (missing)");
        Check(Fails("uniform(2, 2) + normal(1, 0) + exponential(item.lo)") == null, "valid edge parameters still draw");
    }

    static void TestNoAllocation()
    {
        var clock = new SimClock(1);
        var press = new Station(clock, "Press");
        press.Properties.SetDouble("k", 2);
        var item = new Item(0); item.SetLabelValue("w", 3);
        var ex = Expression.Parse("item.w * element.k + max(1, w) > 5 ? w : 0");
        var scope = new EntityScope();
        double sum = 0;
        for (int i = 0; i < 1000; i++) sum += ex.Number(scope.Set(item, press));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++) sum += ex.Number(scope.Set(item, press));
        long after = GC.GetAllocatedBytesForCurrentThread();
        Check(after - before == 0, $"no allocation ({after - before} bytes, sum {sum})");
    }
}
