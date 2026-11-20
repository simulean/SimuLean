// Configs: sampler specs, parameter conversion, tables and records, culture, time and physical units, build errors, element lookup.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
    // ── #18 ──────────────────────────────────────────────────────────────────

    static void TestSamplerSpecErrors()
    {
        var clock = new SimClock(1);
        foreach (string bad in new[] { "Exponential~abc", "Foo~1", null, "", "Exponential", "Uniform~1" })
        {
            bool threw = false;
            try { SamplerSpec.Decode(bad, clock); }
            catch (FormatException) { threw = true; }
            catch (Exception ex) { Console.WriteLine($"  ('{bad}' threw {ex.GetType().Name})"); }
            Check(threw, $"'{bad ?? "null"}' raises FormatException");
        }
        Near(SamplerSpec.Decode("Constant~5", clock).NextValue(), 5, "valid spec still decodes");
        Check(SamplerSpec.Decode("Beta~2~5", clock) != null, "optional Beta bounds");
    }

    static void TestScheduleCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "es-ES", "en-US" })
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
                var clock = new SimClock(1);
                var table = new Dictionary<string, List<string>>
                {
                    ["Time"] = new List<string> { "1.5", "2,5" }, ["Name"] = new List<string> { "A", "B" },
                    ["Q"] = new List<string> { "1", "2" },
                };
                var src = new ScheduleSource("S", clock, arrivalsTable: table);
                var sink = new Sink("K", clock);
                GeneralLink.CreateLink(src, new List<Element> { sink });
                src.Start(); sink.Start();
                clock.AdvanceClock(2);
                Check(sink.StatsCollector.TotalInput == 1, $"{culture}: the 1.5 row has arrived by t = 2");
                clock.AdvanceClock(3);
                Check(sink.StatsCollector.TotalInput == 3, $"{culture}: the '2,5' row (Q = 2) has arrived by t = 3");
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    static void TestModelErrorsFail()
    {
        bool Throws(SimulationConfig config, string expected)
        {
            try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(config); }
            catch (FormatException e) { return e.Message.Contains(expected); }
            return false;
        }
        var unknown = new SimulationConfig();
        unknown.Elements.Add(new ElementConfig("a", "NoSuchType", "A"));
        Check(Throws(unknown, "NoSuchType"), "an unknown element type fails the build, naming the type");

        var missing = new SimulationConfig();
        missing.Elements.Add(new ElementConfig("k", "Sink", "K"));
        missing.Connections.Add(new ConnectionConfig("k", "ghost"));
        Check(Throws(missing, "ghost"), "a connection to a missing element fails the build, naming it");
    }

    static void TestConfigParameters()
    {
        var dt = new DowntimeConfig("d", "MtbfMttr");
        dt.Set("periodicPeriod", 3);
        Near(dt.Get<double>("periodicPeriod", 0), 3, "downtime: int -> double (was the default)");
        var el = new ElementConfig("e", "Queue", "E");
        bool threw = false;
        try { el.Set("capacity", "abc"); } catch (FormatException e) { threw = e.Message.Contains("capacity"); }
        Check(threw, "a value that cannot be converted to the setting's type fails, naming the key (when it is written)");
        Check(el.Get<int>("capacity", 7) == 100, "a setting not given is its default (the settings class's, a Queue's 100)");
        bool unknown = false;
        try { el.Set("capacty", 3); } catch (ArgumentException e) { unknown = e.Message.Contains("'capacity'") && e.Message.Contains("'E'"); }
        Check(unknown, "an unknown setting fails, naming the element and the closest one");
    }

    static void TestTableReader()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "simulean_tables");
        System.IO.Directory.CreateDirectory(dir);
        string csv = System.IO.Path.Combine(dir, "t.csv");
        System.IO.File.WriteAllText(csv, "Time,Name,Note\r\n1.5,A,\"x, y\"\r\n2,B,\"say \"\"hi\"\"\"\r\n\r\n3,C\r\n");
        var t = SimuLean.Data.TableReader.Read(csv);
        Check(t.Count == 3 && t["Time"].Count == 3, $"three columns, three rows (blank line skipped; got {t["Time"].Count})");
        Check(t["Note"][0] == "x, y", $"a quoted comma stays in the field (got '{t["Note"][0]}')");
        Check(t["Note"][1] == "say \"hi\"", $"doubled quotes (got '{t["Note"][1]}')");
        Check(t["Note"][2] == "", "a short row leaves the missing cell empty");
        string data = System.IO.Path.Combine(dir, "t.data");
        System.IO.File.WriteAllText(data, "Time  Q\n0\t2\n5 1\n");
        var d = SimuLean.Data.TableReader.Read(data);
        Check(d["Q"].Count == 2 && d["Q"][0] == "2" && d["Time"][1] == "5", ".data: whitespace-separated");
        bool threw = false;
        try { SimuLean.Data.TableReader.Read(System.IO.Path.Combine(dir, "t.xlsx")); } catch (NotSupportedException) { threw = true; }
        Check(threw, "Excel is not read by the engine");

        var clock = new SimClock(1);
        var src = new ScheduleSource("S", clock, t);
        Check(src.GetDataItemCount() == 3, "the source creates the rows of the table");
    }

    static void TestUndatedTimeUnit()
    {
        var config = new SimuLean.Serialization.SimulationConfig { TimeUnitSeconds = 3600 };
        var cal = config.CreateCalendar();
        Check(cal.SecondsPerUnit == 3600 && cal.IsDefault, $"no start date: undated calendar with the 1 h unit (unit {cal.SecondsPerUnit}, undated {cal.IsDefault})");
        Check(cal.ToDateTime(46) == new DateTime(2000, 1, 2, 22, 0, 0), $"t = 46 → {cal.ToDateTime(46):yyyy-MM-dd HH:mm}");
        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(config);
        clock.AdvanceClock(46);
        Check(clock.GetDateTime() == new DateTime(2000, 1, 2, 22, 0, 0), $"the model clock's date at t = 46: {clock.GetDateTime():yyyy-MM-dd HH:mm}");
        Check(new SimuLean.Serialization.SimulationConfig().CreateCalendar() == SimCalendar.Default, "no unit, no date: the default calendar");
        Check(SimCalendar.Undated(60).ToSimTime(new DateTime(2000, 1, 1, 2, 0, 0)) == 120, "dates convert back with the unit");
    }

    static void TestPhysicalUnits()
    {
        // 1 h time unit: an operator walking 1.2 m/s covers 4320 m per unit; a 90 s load is 0.025 units
        var clock = new SimClock(1);
        clock.Context.Calendar = SimCalendar.Undated(3600);
        var space = new SimuLean.Serialization.ModelSpace();
        space.Matrices["m"] = new SimuLean.Resources.DistanceMatrix().Set("a", "b", 2160);
        var tc = new SimuLean.Serialization.ResourceTeamConfig
        {
            Id = "ops", Travel = SimuLean.Serialization.TravelKind.Matrix, Matrix = "m", HomeNode = "a", Speed = 1.2, Units = SimuLean.Serialization.ResourceUnits.Physical,
            AxisSpeed = new[] { 1, 0.5, 1 }, AxisAccel = new[] { 0.2, 0, 0 },
        };
        tc.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "op1", LoadTime = 90, UnloadTime = 180 });
        var executors = new List<SimuLean.Resources.TaskExecutor>();
        SimuLean.Serialization.ResourceBuild.BuildTeam(tc, clock, executors, space);
        var ex = executors[0];
        Check(Math.Abs(ex.LoadTime - 0.025) < 1e-12 && Math.Abs(ex.UnloadTime - 0.05) < 1e-12, $"load / unload in seconds → units ({ex.LoadTime}, {ex.UnloadTime})");
        double walk = ex.TravelModel.Plan(ex, "a", "b").Duration;
        Check(Math.Abs(walk - 0.5) < 1e-12, $"2160 m at 1.2 m/s = 1800 s = 0.5 h (got {walk})");
        Check(tc.Speed == 1.2 && tc.Members[0].LoadTime == 90, "the config itself is not changed");
        var inModel = SimuLean.Serialization.ResourceBuild.InModelUnits(tc, 3600);
        Check(string.Join("|", inModel.AxisSpeed) == "3600|1800|3600" && inModel.AxisAccel[0] == 2592000, $"axis speeds × unit, accelerations × unit² ({string.Join("|", inModel.AxisSpeed)}; {string.Join("|", inModel.AxisAccel)})");
        tc.Units = SimuLean.Serialization.ResourceUnits.Model;
        Check(ReferenceEquals(SimuLean.Serialization.ResourceBuild.InModelUnits(tc, 3600), tc), "model units: nothing converted");
        bool threw = false;
        try { tc.Set("units", "Parsecs"); } catch (FormatException) { threw = true; }
        Check(threw, "unknown units fail when written");
    }

    static void TestFindByName()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 10 };
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("UnityQueue_Buffer_1234", "Queue", "Buffer"));
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("UnitySink_Out_77", "Sink", "Out"));
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("UnitySink_Out_78", "Sink", "Out"));
        var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        model.BuildModel(c);
        Check(model.Find("Buffer") != null && model.Find("Buffer").Name == "Buffer", "found by display name");
        Check(model.Find("UnitySink_Out_78") != null, "an id works too");
        Check(model.Find("Nope") == null && model.Find("") == null, "none: null");
        bool failed = false;
        try { model.Find("Out"); } catch (InvalidOperationException) { failed = true; }
        Check(failed, "two elements named 'Out': fails, asking for the id");
    }

    // ── #57 ──────────────────────────────────────────────────────────────────

    static void TestDecimalCommaInConfigs()
    {
        var item = new Item(0); item.SetLabelValue("w", 2.5);
        var single = new ElementConfig("q", "Queue", "q");
        single.Set("inputStrategy.strategyType", "SingleLabel");
        single.Set("inputStrategy.singleLabelName", "w");
        single.Set("inputStrategy.singleLabelValue", "2,5");
        Check(ElementBuildHelpers.BuildInputStrategy(single).Admits(null, item, null), "single label value '2,5' = 2.5 (was 25: nothing admitted)");

        var multi = new ElementConfig("q", "Queue", "q");
        multi.Set("inputStrategy.strategyType", "MultiLabel");
        multi.Set("inputStrategy.multiLabelPairs", new List<LabelValueSettings> { LabelValueSettings.Of("w", "2,5") });
        Check(ElementBuildHelpers.BuildInputStrategy(multi).Admits(null, item, null), "multi label record w / '2,5' = 2.5");


        // A value that is not a number is a text value (it used to fail, and before that admitted nothing).
        single.Set("inputStrategy.singleLabelValue", "abc");
        var text = new Item(0); text.SetLabelText("w", "abc");
        var textStrategy = ElementBuildHelpers.BuildInputStrategy(single);
        Check(textStrategy.Admits(null, text, null) && !textStrategy.Admits(null, item, null),
              "a label value that is not a number matches the text label spelled the same (and not a numeric one)");
    }

    // ── #58 ──────────────────────────────────────────────────────────────────

    static void TestUnreadableRecordsFail()
    {
        var clock = new SimClock(1);
        var els = new Dictionary<string, Element> { ["m"] = new MultiServer(new[] { Const(1) }, "M", clock) };
        bool Fails(DowntimeConfig dc, string mention)
        {
            try { DowntimeBuilder.Build(dc, els, clock); return false; }
            catch (FormatException ex) { return ex.Message.Contains(mention); }
        }
        var ok = new DowntimeConfig("t", "Timetable", "m");
        ok.Set("rows", new List<DowntimeIntervalSettings> { DowntimeIntervalSettings.Of("m", 100, 50), new DowntimeIntervalSettings { Target = "m", Start = "3600,5", Duration = 60 } });   // a decimal comma is read as one
        Check(DowntimeBuilder.Build(ok, els, clock).Count == 1, "valid intervals build (decimal comma accepted)");
        var bad = new DowntimeConfig("t", "Timetable", "m");
        bad.Set("rows", new List<DowntimeIntervalSettings> { DowntimeIntervalSettings.Of("m", 100, 50), new DowntimeIntervalSettings { Target = "m", Start = "tomorrow", Duration = 60 } });
        Check(Fails(bad, "tomorrow"), "an interval that cannot be read fails, naming it (the stop silently never happened)");
        // a row whose target is not one of the downtime's (a team member's name, another element) was skipped on every
        // target: the stop silently never happened
        var stray = new DowntimeConfig("t", "Timetable", "m");
        stray.Set("rows", new List<DowntimeIntervalSettings> { DowntimeIntervalSettings.Of("Ops_1", 100, 50) });
        Check(Fails(stray, "'Ops_1'"), "a row targeting something that is not a target of the downtime fails, naming it");
        var all = new DowntimeConfig("t", "Timetable", "m");
        all.Set("rows", new List<DowntimeIntervalSettings> { DowntimeIntervalSettings.Of("", 100, 50) });
        Check(DowntimeBuilder.Build(all, els, clock).Count == 1, "a row without a target applies to every target");
        var shift = new DowntimeConfig("s", "Shift", "m");
        shift.Set("shiftPattern", "Mon-Fri 08:00-16:00");
        shift.Set("holidays", new List<string> { "2026-12-25", "Christmas" });
        Check(Fails(shift, "Christmas"), "a holiday that is not a date fails, naming it");

        var lanes = new ElementConfig("l", "LengthLimitedQueue", "l");
        var ls = lanes.SettingsAs<LengthLimitedQueueSettings>();
        lanes.Set("laneLengths", new List<string> { "12", "2,5" });
        var read = LengthLimitedQueueBuilder.LaneLengths("l", ls);
        Check(read.Count == 2 && Math.Abs(read[1] - 2.5) < 1e-12, $"lane lengths '12', '2,5' = two lanes, 12 and 2.5 ({string.Join(" ", read)})");
        lanes.Set("laneLengths", new List<object> { 8.0, 4L });   // a list loaded from JSON
        read = LengthLimitedQueueBuilder.LaneLengths("l", ls);
        Check(read.Count == 2 && read[0] == 8 && read[1] == 4, $"a JSON list of numbers ({string.Join(" ", read)}; it fell back to one lane of 10)");
        bool threw = false;
        try { lanes.Set("laneLengths", new List<string> { "12", "x" }); } catch (FormatException ex) { threw = ex.Message.Contains("x"); }
        Check(threw, "a lane length that is not a number fails, naming it");
        lanes.Set("laneLengths", new[] { 12.0, -1.0 });
        threw = false;
        try { LengthLimitedQueueBuilder.LaneLengths("l", ls); } catch (FormatException ex) { threw = ex.Message.Contains("-1"); }
        Check(threw, "a lane length that is not positive fails");
    }
}
