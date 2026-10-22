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
        Near(E("1.5e1 / 0"), 0, "division by zero = 0; exponent");
        Near(E("now()"), 42, "now()");
        var ex = Expression.Parse("item.prio * 10 + age + element.speed");
        Check(ex.References.Count == 3 && ex.References.Contains("item.prio") && ex.References.Contains("age") && ex.References.Contains("element.speed"), "references");
        foreach (string bad in new[] { "", "a +", "(a", "foo(1)", "max()", "a $ b", "pow(1)", "'open", "item.", "has(1)" })
        {
            bool threw = false;
            try { Expression.Parse(bad); } catch (FormatException) { threw = true; }
            Check(threw, $"'{bad}' rejected");
        }
        Check(Expression.Check("a +")?.Contains("position") == true && Expression.Check("a + 1") == null, "Check() gives the message with the position");
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
        Near(N("station.speed"), 2, "station is element");
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
        var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
        src.Parameters["sourceMode"] = "Schedule"; src.Parameters["arrivalsTable"] = arrivals;
        var srv = new SimuLean.Serialization.ElementConfig("srv", "UnityMultiServer", "srv");
        srv.Parameters["capacity"] = 1;
        srv.Parameters["delayStrategyType"] = "Expression";
        srv.Parameters["expression"] = "table('times', item.kind, 'time')";
        var sink = new SimuLean.Serialization.ElementConfig("sink", "UnitySink", "sink");
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
