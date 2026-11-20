using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Data;

namespace SimuLean.Results
{
    /// <summary>
    /// A whole <see cref="ResultSet"/> as JSON: what the out-of-editor runner sends back so the editor gets
    /// the same set as an in-editor study — scenarios, runs with their metrics, every object's statistics, the run info,
    /// the time series, the state timeline, the jobs, the samples, the extra tables and an optimisation's candidates (what
    /// <see cref="ResultStore"/> keeps, plus the time unit and the samples). Envelope
    /// <c>{"format": 1, "kind": "resultset", "resultSet": {…}}</c> (<see cref="Key"/>).
    /// <para><b>Compact runs</b> (a run is also sent alone as soon as it ends, <see cref="RunToJson"/>):
    /// the statistics are columns — each statistic name once per run, objects as parallel lists — and every number list is
    /// the base64 of its IEEE-754 doubles (little-endian): exact to the bit (NaN included), ≈ 4× smaller than decimal text
    /// and fast to read in Mono. Integer lists (indices, counts) the same with 32-bit integers.</para>
    /// </summary>
    public static class ResultSetJson
    {
        /// <summary>The key of the set in the envelope and in the runner's answer (the vocabulary's <c>resultSet</c>).</summary>
        public const string Key = "resultSet";

        public static string Serialize(ResultSet set) => MiniJson.Serialize(Envelope(set));

        /// <summary>The envelope as dictionaries (to embed in another answer).</summary>
        public static Dictionary<string, object> Envelope(ResultSet set) => new Dictionary<string, object>
        {
            ["format"] = Serialization.ConfigJson.FormatVersion, ["kind"] = "resultset", [Key] = ToJson(set),
        };

        public static ResultSet Deserialize(string json)
        {
            if (!(MiniJson.Parse(json ?? throw new ArgumentNullException(nameof(json))) is Dictionary<string, object> root))
                throw new FormatException("A result set file is a JSON object {\"format\": 1, \"kind\": \"resultset\", \"resultSet\": {…}}.");
            return FromEnvelope(root);
        }

        public static ResultSet FromEnvelope(Dictionary<string, object> root)
        {
            Serialization.ConfigJson.CheckEnvelope(root, "resultset");
            if (!(root.TryGetValue(Key, out object s) && s is Dictionary<string, object> set))
                throw new FormatException("The result set file has no \"resultSet\" object.");
            return FromJson(set);
        }

        // ── the set ──────────────────────────────────────────────────────────

        /// <summary>The set as dictionaries; <paramref name="withRuns"/> false leaves the runs out (they were sent one by one).</summary>
        public static Dictionary<string, object> ToJson(ResultSet set, bool withRuns = true)
        {
            var runs = new List<object>();
            if (withRuns) foreach (var r in set.Runs) runs.Add(RunToJson(r));
            var scenarios = new List<object>();
            foreach (var sc in set.Scenarios)
                scenarios.Add(new Dictionary<string, object> { ["name"] = sc.Name, ["parameters"] = Pairs(sc.Parameters) });
            var extra = new List<object>();
            foreach (var t in set.Extra)
            {
                var rows = new List<object>(t.RowCount);
                foreach (var row in t.Rows) rows.Add(new List<object>(row));
                extra.Add(new Dictionary<string, object> { ["name"] = t.Name, ["columns"] = new List<object>(t.Columns), ["rows"] = rows });
            }
            var candidates = new List<object>();
            foreach (var c in set.Candidates)
            {
                var metricNames = new List<object>();
                var metricValues = new List<double>();
                foreach (var kv in c.Metrics) { metricNames.Add(kv.Key); metricValues.Add(kv.Value); }
                candidates.Add(new Dictionary<string, object>
                {
                    ["iteration"] = c.Iteration, ["fitness"] = Doubles(new[] { c.Fitness }), ["error"] = c.Error,
                    ["parameters"] = Pairs(c.Parameters), ["metrics"] = new Dictionary<string, object> { ["names"] = metricNames, ["values"] = Doubles(metricValues) },
                });
            }
            return new Dictionary<string, object>
            {
                ["name"] = set.Name, ["source"] = set.Source, ["model"] = set.Model,
                ["created"] = set.Created.ToString("o", CultureInfo.InvariantCulture), ["confidence"] = Doubles(new[] { set.Confidence }),
                ["metadata"] = Pairs(set.Metadata), ["metricNames"] = new List<object>(set.MetricNames),
                ["scenarios"] = scenarios, ["runs"] = runs, ["extra"] = extra, ["candidates"] = candidates,
            };
        }

        public static ResultSet FromJson(Dictionary<string, object> o)
        {
            var set = new ResultSet
            {
                Name = Str(o, "name"), Source = Str(o, "source"), Model = Str(o, "model"),
                Created = DateTime.TryParse(Str(o, "created"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d) ? d : DateTime.MinValue,
                Confidence = FirstDouble(o, "confidence"),
            };
            foreach (var (k, v) in PairsOf(o, "metadata")) set.Metadata.Add(new KeyValuePair<string, string>(k, Text(v)));
            foreach (var n in List(o, "metricNames")) set.MetricNames.Add(Text(n));
            foreach (Dictionary<string, object> sc in List(o, "scenarios"))
            {
                var scenario = new ResultScenario { Name = Str(sc, "name") };
                foreach (var (k, v) in PairsOf(sc, "parameters")) scenario.Parameters.Add(new KeyValuePair<string, string>(k, Text(v)));
                set.Scenarios.Add(scenario);
            }
            foreach (Dictionary<string, object> r in List(o, "runs")) set.Runs.Add(RunFromJson(r));
            foreach (Dictionary<string, object> t in List(o, "extra"))
            {
                var columns = new List<string>();
                foreach (var c in List(t, "columns")) columns.Add(Text(c));
                var table = new ResultTable(Str(t, "name"), columns.ToArray());
                foreach (var row in List(t, "rows")) if (row is List<object> cells) table.Add(cells.ToArray());
                set.Extra.Add(table);
            }
            foreach (Dictionary<string, object> c in List(o, "candidates"))
            {
                var cand = new ResultCandidate
                {
                    Iteration = Int(c, "iteration"), Fitness = FirstDouble(c, "fitness"), Error = c.TryGetValue("error", out object ce) ? ce as string : null,
                };
                foreach (var (k, v) in PairsOf(c, "parameters")) cand.Parameters.Add(new KeyValuePair<string, string>(k, Text(v)));
                ReadNamedValues(Obj(c, "metrics"), (k, v) => cand.Metrics[k] = v);
                set.Candidates.Add(cand);
            }
            return set;
        }

        // ── one run ──────────────────────────────────────────────────────────

        /// <summary>One run, compact (sent alone by the runner as soon as it ends).</summary>
        public static Dictionary<string, object> RunToJson(ResultRun r)
        {
            var names = new List<object>();
            var values = new List<double>();
            foreach (var kv in r.Metrics) { names.Add(kv.Key); values.Add(kv.Value); }
            var run = new Dictionary<string, object>
            {
                ["scenario"] = r.Scenario, ["replication"] = r.Replication, ["seed"] = r.Seed, ["elapsed"] = Doubles(new[] { r.ElapsedSeconds }),
                ["metrics"] = new Dictionary<string, object> { ["names"] = names, ["values"] = Doubles(values) }, ["error"] = r.Error,
            };
            if (r.Results != null) run["results"] = Results(r.Results);
            return run;
        }

        public static ResultRun RunFromJson(Dictionary<string, object> r)
        {
            var run = new ResultRun
            {
                Scenario = Str(r, "scenario"), Replication = Int(r, "replication"), Seed = Int(r, "seed"),
                ElapsedSeconds = FirstDouble(r, "elapsed"), Error = r.TryGetValue("error", out object e) ? e as string : null,
            };
            ReadNamedValues(Obj(r, "metrics"), (k, v) => run.Metrics[k] = v);
            if (r.TryGetValue("results", out object res) && res is Dictionary<string, object> rr) run.Results = ResultsOf(rr);
            return run;
        }

        private static Dictionary<string, object> Results(RunResults r)
        {
            var info = r.Info;
            // statistics as columns: each name once (in first-seen order), per object its keys and values
            var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new List<object>();
            var scope = new List<object>(); var id = new List<object>(); var name = new List<object>(); var type = new List<object>(); var group = new List<object>();
            var counts = new List<int>(); var keys = new List<int>(); var values = new List<double>();
            foreach (var o in r.Objects)
            {
                scope.Add(o.Scope.ToString()); id.Add(o.Id); name.Add(o.Name); type.Add(o.Type); group.Add(o.Group);
                counts.Add(o.Metrics.Count);
                foreach (var st in o.Metrics)
                {
                    if (!nameIndex.TryGetValue(st.Key, out int k)) { k = names.Count; nameIndex[st.Key] = k; names.Add(st.Key); }
                    keys.Add(k);
                    values.Add(st.Value);
                }
            }
            var sScope = new List<object>(); var sName = new List<object>(); var sMetric = new List<object>();
            var sCount = new List<int>(); var sTimes = new List<double>(); var sValues = new List<double>();
            foreach (var ts in r.Series)
            {
                sScope.Add(ts.Scope.ToString()); sName.Add(ts.Name); sMetric.Add(ts.Metric); sCount.Add(ts.Times.Count);
                sTimes.AddRange(ts.Times); sValues.AddRange(ts.Values);
            }
            var tScope = new List<object>(); var tName = new List<object>(); var tState = new List<object>(); var tStart = new List<double>(); var tEnd = new List<double>();
            foreach (var e in r.Timeline) { tScope.Add(e.Scope.ToString()); tName.Add(e.Name); tState.Add(e.State); tStart.Add(e.Start); tEnd.Add(e.End); }
            var jItem = new List<object>(); var jElement = new List<object>(); var jStart = new List<double>(); var jEnd = new List<double>();
            foreach (var j in r.Jobs) { jItem.Add(j.Item); jElement.Add(j.Element); jStart.Add(j.Start); jEnd.Add(j.End); }
            var samples = new Dictionary<string, object>();
            foreach (var kv in r.Samples)
                samples[kv.Key] = new Dictionary<string, object>
                {
                    ["stay"] = Doubles(kv.Value.StayTimes), ["entries"] = Doubles(kv.Value.EntryTimes), ["exits"] = Doubles(kv.Value.ExitTimes),
                };
            var jInfo = new Dictionary<string, object>
            {
                ["model"] = info.Model, ["seed"] = info.Seed, ["times"] = Doubles(new[] { info.EndTime, info.WarmupTime, info.TimeUnitSeconds }),
                ["endReason"] = info.EndReason, ["deadlocked"] = info.Deadlocked, ["waiting"] = new List<object>(info.Waiting),
            };
            if (info.Warnings.Count > 0) jInfo["warnings"] = new List<object>(info.Warnings);
            return new Dictionary<string, object>
            {
                ["info"] = jInfo,
                ["names"] = names,
                ["objects"] = new Dictionary<string, object>
                {
                    ["scope"] = scope, ["id"] = id, ["name"] = name, ["type"] = type, ["group"] = group,
                    ["count"] = Ints(counts), ["keys"] = Ints(keys), ["values"] = Doubles(values),
                },
                ["series"] = new Dictionary<string, object>
                {
                    ["scope"] = sScope, ["name"] = sName, ["metric"] = sMetric, ["count"] = Ints(sCount), ["t"] = Doubles(sTimes), ["v"] = Doubles(sValues),
                },
                ["timeline"] = new Dictionary<string, object>
                {
                    ["scope"] = tScope, ["name"] = tName, ["state"] = tState, ["start"] = Doubles(tStart), ["end"] = Doubles(tEnd),
                },
                ["jobs"] = new Dictionary<string, object> { ["item"] = jItem, ["element"] = jElement, ["start"] = Doubles(jStart), ["end"] = Doubles(jEnd) },
                ["samples"] = samples,
            };
        }

        private static RunResults ResultsOf(Dictionary<string, object> o)
        {
            var r = new RunResults();
            var info = Obj(o, "info");
            if (info != null)
            {
                var times = DoublesOf(info, "times");
                r.Info.Model = Str(info, "model"); r.Info.Seed = Int(info, "seed");
                if (times.Length == 3) { r.Info.EndTime = times[0]; r.Info.WarmupTime = times[1]; r.Info.TimeUnitSeconds = times[2]; }
                r.Info.EndReason = Str(info, "endReason"); r.Info.Deadlocked = info.TryGetValue("deadlocked", out object dl) && dl is bool b && b;
                foreach (var w in List(info, "waiting")) r.Info.Waiting.Add(Text(w));
                foreach (var w in List(info, "warnings")) r.Info.Warnings.Add(Text(w));
            }
            var names = List(o, "names");
            var obs = Obj(o, "objects");
            if (obs != null)
            {
                var scope = List(obs, "scope"); var id = List(obs, "id"); var name = List(obs, "name"); var type = List(obs, "type"); var group = List(obs, "group");
                var counts = IntsOf(obs, "count"); var keys = IntsOf(obs, "keys"); var values = DoublesOf(obs, "values");
                int at = 0;
                for (int i = 0; i < scope.Count; i++)
                {
                    var obj = r.Add(new ResultObject(Scope(Text(scope[i])), Text(id[i]), Text(name[i]), Text(type[i]), Text(group[i])));
                    for (int k = 0; k < counts[i]; k++, at++) obj.Set(Text(names[keys[at]]), values[at]);
                }
            }
            var s = Obj(o, "series");
            if (s != null)
            {
                var scope = List(s, "scope"); var name = List(s, "name"); var metric = List(s, "metric");
                var counts = IntsOf(s, "count"); var times = DoublesOf(s, "t"); var values = DoublesOf(s, "v");
                int at = 0;
                for (int i = 0; i < scope.Count; i++)
                {
                    var ts = new TimeSeries(Scope(Text(scope[i])), Text(name[i]), Text(metric[i]));
                    for (int k = 0; k < counts[i]; k++, at++) { ts.Times.Add(times[at]); ts.Values.Add(values[at]); }
                    r.Series.Add(ts);
                }
            }
            var tl = Obj(o, "timeline");
            if (tl != null)
            {
                var scope = List(tl, "scope"); var name = List(tl, "name"); var state = List(tl, "state");
                var start = DoublesOf(tl, "start"); var end = DoublesOf(tl, "end");
                for (int i = 0; i < scope.Count; i++) r.Timeline.Add(new TimelineEntry(Scope(Text(scope[i])), Text(name[i]), Text(state[i]), start[i], end[i]));
            }
            var jobs = Obj(o, "jobs");
            if (jobs != null)
            {
                var item = List(jobs, "item"); var element = List(jobs, "element");
                var start = DoublesOf(jobs, "start"); var end = DoublesOf(jobs, "end");
                for (int i = 0; i < item.Count; i++) r.Jobs.Add(new JobEntry(Text(item[i]), Text(element[i]), start[i], end[i]));
            }
            var samples = Obj(o, "samples");
            if (samples != null)
                foreach (var kv in samples)
                {
                    var sm = (Dictionary<string, object>)kv.Value;
                    r.Samples[kv.Key] = new ElementSamples(DoublesOf(sm, "stay"), DoublesOf(sm, "entries"), DoublesOf(sm, "exits"));
                }
            return r;
        }

        // ── numbers as base64 ────────────────────────────────────────────────

        /// <summary>A list of doubles as the base64 of their little-endian IEEE-754 bytes (exact, NaN included).</summary>
        public static string Doubles(IEnumerable<double> values)
        {
            var list = values as IList<double> ?? new List<double>(values);
            var bytes = new byte[list.Count * 8];
            for (int i = 0; i < list.Count; i++) Write(bytes, i * 8, BitConverter.DoubleToInt64Bits(list[i]), 8);
            return Convert.ToBase64String(bytes);
        }

        public static double[] DoublesFrom(string base64)
        {
            var bytes = Convert.FromBase64String(base64 ?? "");
            var values = new double[bytes.Length / 8];
            for (int i = 0; i < values.Length; i++) values[i] = BitConverter.Int64BitsToDouble(Read(bytes, i * 8, 8));
            return values;
        }

        private static string Ints(IList<int> values)
        {
            var bytes = new byte[values.Count * 4];
            for (int i = 0; i < values.Count; i++) Write(bytes, i * 4, values[i], 4);
            return Convert.ToBase64String(bytes);
        }

        private static int[] IntsFrom(string base64)
        {
            var bytes = Convert.FromBase64String(base64 ?? "");
            var values = new int[bytes.Length / 4];
            for (int i = 0; i < values.Length; i++) values[i] = (int)Read(bytes, i * 4, 4);
            return values;
        }

        // little-endian whatever the machine
        private static void Write(byte[] b, int at, long v, int size) { for (int k = 0; k < size; k++) b[at + k] = (byte)(v >> (8 * k)); }
        private static long Read(byte[] b, int at, int size)
        {
            long v = 0;
            for (int k = 0; k < size; k++) v |= (long)b[at + k] << (8 * k);
            return size == 4 ? (int)v : v;
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static List<object> Pairs<TV>(IEnumerable<KeyValuePair<string, TV>> pairs)
        {
            var list = new List<object>();
            foreach (var kv in pairs) list.Add(new List<object> { kv.Key, kv.Value });
            return list;
        }

        private static IEnumerable<(string, object)> PairsOf(Dictionary<string, object> o, string key)
        {
            foreach (var p in List(o, key))
                if (p is List<object> kv && kv.Count == 2) yield return (Text(kv[0]), kv[1]);
        }

        private static void ReadNamedValues(Dictionary<string, object> o, Action<string, double> set)
        {
            if (o == null) return;
            var names = List(o, "names");
            var values = DoublesOf(o, "values");
            for (int i = 0; i < names.Count && i < values.Length; i++) set(Text(names[i]), values[i]);
        }

        private static double[] DoublesOf(Dictionary<string, object> o, string key) => DoublesFrom(Str(o, key));
        private static int[] IntsOf(Dictionary<string, object> o, string key) => IntsFrom(Str(o, key));
        private static double FirstDouble(Dictionary<string, object> o, string key) { var d = DoublesOf(o, key); return d.Length > 0 ? d[0] : double.NaN; }

        private static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
            => o.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;

        private static List<object> List(Dictionary<string, object> o, string key)
            => o.TryGetValue(key, out object v) && v is List<object> l ? l : new List<object>();

        private static string Str(Dictionary<string, object> o, string key) => o.TryGetValue(key, out object v) ? Text(v) : "";
        private static string Text(object v) => v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        private static int Int(Dictionary<string, object> o, string key) => o.TryGetValue(key, out object v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;
        private static ResultScope Scope(string s) => Enum.TryParse(s, true, out ResultScope scope) ? scope : ResultScope.Element;
    }
}
