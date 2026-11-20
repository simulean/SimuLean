using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SimuLean.Expressions;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>
    /// The one key vocabulary of the agent interface, as data built from the code — what
    /// <c>simulean_vocabulary</c> answers and the runner / the tests can read: the argument concepts of the commands, the
    /// factor / metric / result scopes, the path grammar with one example per form, the statistics per scope, the state
    /// names, the expression roots / built-ins / functions, the option enums and the catalog keys with their rule.
    /// Nothing here is a second list to keep in step: each part reads the list the code itself uses, except the argument
    /// concepts and the path grammar, which are the vocabulary's definition (an EditMode test fails when a command has an
    /// argument no concept covers).
    /// </summary>
    public static class Vocabulary
    {
        /// <summary>An argument concept: its meaning and the argument names that carry it (every command argument is in
        /// exactly one concept).</summary>
        public sealed class Concept
        {
            public string Name { get; }
            public string Meaning { get; }
            public IReadOnlyList<string> Arguments { get; }
            public Concept(string name, string meaning, params string[] arguments) { Name = name; Meaning = meaning; Arguments = arguments; }
        }

        /// <summary>The argument concepts of the agent commands. One name per concept, no aliases: <see cref="Retired"/> lists
        /// the names that were replaced.</summary>
        public static readonly IReadOnlyList<Concept> Concepts = new[]
        {
            new Concept("object", "A SimuLean object by GameObject name or hierarchy path (objects: a list 'A;B'); from / to: the two ends of a connection; group: the objects a repeat copies; parent: where a new object goes; root: the model root; optimizer / experimenter / recorder: a scene component by name.",
                        "object", "objects", "from", "to", "group", "parent", "root", "optimizer", "experimenter", "recorder"),
            new Concept("naming", "name: a new object's name (or a stored result set's); newName: a rename's new name; names: a pattern for copies.",
                        "name", "newName", "names"),
            new Concept("project", "model: a project model; asset: a library 3D asset; example: an example scene; scene: a scene to record; file / path / sheet / table / column: files and tables.",
                        "model", "asset", "example", "scene", "file", "path", "sheet", "table", "column"),
            new Concept("study", "factors: catalog overrides / decision variables 'path=v1|v2;…' or JSON; metrics: named metric paths; compareTo: the scenario the others are compared with (paired differences on common seeds); the run settings — horizon (run length), timeUnit (a new model's time unit: s, min, h, d or seconds per unit), until (a diagnosis' stop time), warmup, noProgress (stall window), replications, seed, threads; omitted or -1 = the model's (or the component's) value.",
                        "factors", "metrics", "scenarios", "where", "derived", "confidence", "detail", "compareTo", "horizon", "timeUnit", "until", "warmup", "noProgress",
                        "replications", "seed", "threads", "all"),
            new Concept("optimisation", "The search: its fitness expression, the algorithm, its settings and the budget.",
                        "fitness", "algorithm", "algorithmSettings", "maxEvaluations", "maxSeconds"),
            new Concept("editing", "What is placed or changed: an element type, a field (key / value), a model card field, a bulk edit list, copies, a team; next: an element's Next Elements in a new order; parameters / section: what simulean_model answers (with or without the parameters; section model or logic, the decision sites) — section is also the part of simulean_vocabulary answered (its index without); offset: the step between a repeat's copies (and a study answer's first scenario on a page: see results); fields: the field paths simulean_types / simulean_params list (detail: compact or full); pack: the Library entries simulean_types lists (a pack, a group or a module; all = every one); validateOnly: a bulk edit list checked, not applied.",
                        "type", "key", "value", "field", "fields", "operations", "validateOnly", "next", "count", "offset", "connectFrom", "connectTo", "members", "travel",
                        "action", "readAtRun", "open", "parameters", "section", "pack"),
            new Concept("placement", "World position, rotation about the vertical axis and scale.", "x", "y", "z", "rotY", "scale"),
            new Concept("results", "resultSet: a stored result set ('last', an id or a name); kind: a chart's kind (simulean_add_team: the members' kind — operator, agv, forklift… —, their 3D model and speed); a chart's axes (xAxis / yAxis), grouping; title: a chart's title or a shot's caption; export formats; a large study answer's page — top: how many scenarios, sort: the metric they are sorted by (-metric or 'metric desc' descending), offset: the first one (from 0).",
                        "resultSet", "formats", "kind", "xAxis", "yAxis", "groupBy", "title", "sort"),
            new Concept("data", "The project database and distribution fitting (top: how many ranked fits; also the scenarios of a study answer's page: see results).", "sql", "import", "append", "maxRows", "values", "criterion", "top", "into", "pick"),
            new Concept("camera", "Pictures and videos: framing, camera angles, sizes, shots and takes; connections: the connection groups a picture draws.",
                        "elevation", "azimuth", "distance", "fov", "width", "height", "shot", "framing", "orbit", "lookHeight", "position", "rotation",
                        "pose", "fromView", "seconds", "glide", "glideSeconds", "subtitle", "index", "remove", "startAt", "fps", "exitPlay",
                        "quality", "bitrateMbps", "connections"),
            new Concept("jobs", "Background jobs and tests: wait (seconds, at most 55), cancel, a test name filter.", "wait", "cancel", "filter"),
            new Concept("rules", "Choice rules (simulean_rule_*): why = the declarative form tried first (a BestScore expression, an offerSort, a request order, a reaction) and why it is not enough; a rule without it is refused. The site is the kind (routing, release, dispatch) and the class the name.",
                        "why"),
        };

        /// <summary>Names replaced by the one vocabulary → their name now: arguments, answer keys, file keys and
        /// table columns. They are not accepted.</summary>
        public static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>
        {
            ["element"] = "object", ["source"] = "object", ["targets"] = "objects", ["target"] = "object",
            ["rename.name"] = "newName", ["models.name"] = "model", ["place_model.model"] = "asset",
            ["set"] = "factors", ["optimize_start.parameters"] = "factors", ["results.set"] = "resultSet",
            ["maxTime"] = "horizon", ["runTime"] = "horizon", ["diagnose.time"] = "until",
            ["x / y (results)"] = "xAxis / yAxis", ["Owner.fileName (factor)"] = "Owner.table",
            ["element (answers)"] = "object", ["set (result-set files, runner answer)"] = "resultSet",
            ["set / set_id (data-table columns)"] = "result_set / result_set_id",
            ["metric:NAME / stat:SCOPE:OBJECTS:STATISTIC / param:PATH (chart axes)"] = "NAME / [scope:]OBJECTS.statistic / PATH (as in a study)",
        };

        /// <summary>The concept of an argument name (null = none: a new argument must be given one).</summary>
        public static Concept ConceptOf(string argument) => Concepts.FirstOrDefault(c => c.Arguments.Contains(argument));

        /// <summary>The forms of a factor / metric path, each with an example.</summary>
        public static readonly IReadOnlyList<(string form, string meaning, string example)> PathGrammar = new[]
        {
            ("[scope:]Owner.key", "A catalog key of an element (no scope), a team, a downtime, a reaction or a data table (factors); a statistic of an element, team, list, reaction or downtime (metrics). The key is the field path simulean_set writes (it may hold dots); the owner is the longest name of the model followed by a dot.", "Buffer.capacity"),
            ("Owner.field.distributionField", "A number of a distribution: its field (a flat distribution, or a distribution spec's number).", "Srv.delayStrategy.constantValue"),
            ("model.statistic", "A statistic of the whole run.", "model.makespan"),
            ("Owner.tally:NAME:statistic", "A tally's statistic (the values recorded per item).", "Exit.tally:time_in_system:p90"),
            ("Owner.tally:NAME[label]:statistic", "A tally split by an item label (one value per label value).", "Exit.tally:time_in_system[priority]:mean"),
            ("…[*]…", "Expanded after the runs into one metric per label value (name[value]).", "Exit.tally:tis[*]:mean"),
            ("team:T.members[label=value]", "The number of a team's members carrying a label (extra ones copy the last of them).", "team:Welders.members[skill=weld]"),
            ("downtime:Id@Target.statistic", "A downtime with several targets: one target's statistic (the bare id: all of them together).", "downtime:machineFail@M1.repairs"),
            ("Owner.state:STATE", "The share of time in a state.", "Srv.state:BLOCKED"),
            ("list:L.field:NAME:statistic", "A list field's statistic.", "list:orders.field:weight:push_avg"),
            ("data:Table.file / data:Table.sql", "A data table's source per level.", "data:arrivals.file"),
            ("Owner.table", "A schedule source's arrivals file per level.", "Orders.table"),
        };

        /// <summary>The whole vocabulary as plain data (dictionaries and lists). With <paramref name="model"/>, the state
        /// names its built model registers are added (a model that does not build is reported, not thrown).</summary>
        public static Dictionary<string, object> Describe(SimulationConfig model = null)
        {
            var d = new Dictionary<string, object>
            {
                ["arguments"] = Concepts.Select(c => (object)new Dictionary<string, object>
                    { ["concept"] = c.Name, ["meaning"] = c.Meaning, ["arguments"] = c.Arguments.ToList() }).ToList(),
                ["retired"] = Retired.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                ["scopes"] = new Dictionary<string, object>
                {
                    ["factor"] = Names<ParameterScope>(),
                    ["metric"] = Names<MetricScope>(),
                    ["result"] = Names<ResultScope>(),
                },
                ["paths"] = PathGrammar.Select(p => (object)new Dictionary<string, object>
                    { ["form"] = p.form, ["meaning"] = p.meaning, ["example"] = p.example }).ToList(),
                ["statistics"] = Statistics(),
                ["states"] = States(model),
                ["expressions"] = new Dictionary<string, object>
                {
                    ["roots"] = Expression.Roots.ToList(),
                    ["item"] = EntityScope.ItemBuiltIns.ToList(),
                    ["element"] = EntityScope.ElementBuiltIns.ToList(),
                    // where an expression is written: the roots it reads there, a bare name, the function families
                    ["contexts"] = ExpressionContext.All.Select(c => (object)new Dictionary<string, object>
                    {
                        ["key"] = c.Key, ["place"] = c.Name, ["bare"] = c.Bare,
                        ["roots"] = c.Roots.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                        ["random"] = c.Random, ["model"] = c.Model, ["data"] = c.Data,
                    }).ToList(),
                    ["stat"] = SimuLean.Results.ElementMetrics.Names.Concat(new[] { "state:X", "tally:NAME:STAT" }).ToList(),
                    ["team"] = SimuLean.Results.TeamMetrics.Names.ToList(),
                    ["list"] = SimuLean.Results.ListMetrics.Names.ToList(),
                    ["missing"] = "a value not set is missing (0 / ''): has(x) tests it (any expression), x ?? v gives v instead; an " +
                                  "item label read but not set warns once at run time. Names of elements, tables, columns and element " +
                                  "properties are checked when the model is built; a name computed from a label (content(item.dest)) too " +
                                  "when every write of the label is known then, else when it is read.",
                    ["rules"] = "names are case-sensitive (functions and roots lower-case); a condition is a number or true / false, " +
                                "never a text (compare it, or has()); a value that is NaN or infinite (a division by zero, sqrt(-1)) " +
                                "stops the run, naming the time, the element, the field and the expression.",
                    ["functions"] = Expression.FunctionArities.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => (object)new Dictionary<string, object> { ["name"] = kv.Key, ["min"] = kv.Value.min, ["max"] = kv.Value.max }).ToList(),
                },
                ["options"] = new Dictionary<string, object>
                {
                    ["samplers"] = ParameterSchema.SamplerTypes.ToList(),
                    ["routing"] = Enum.GetNames(typeof(HeadlessOutputStrategyType)).ToList(),
                    ["input"] = Enum.GetNames(typeof(HeadlessInputStrategyType)).ToList(),
                    ["preemptMode"] = Enum.GetNames(typeof(SimuLean.Resources.PreemptMode)).ToList(),
                    ["processRelease"] = Enum.GetNames(typeof(SimuLean.Resources.ProcessRelease)).ToList(),
                    ["runEndReason"] = Enum.GetNames(typeof(RunEndReason)).ToList(),
                },
                ["catalog"] = new Dictionary<string, object>
                {
                    ["rule"] = "Numbers, bools and number arrays are offered by their type; a text or list key is declared — " +
                               "structural (not a parameter), choice (one of its options), sampler (a distribution spec), " +
                               "numberList / numbers, properties (the user properties), file (a file per level).",
                    ["common"] = Catalog(ParameterSchema.Common),
                    ["team"] = Catalog(ParameterSchema.Team),
                    ["downtime"] = Catalog(ParameterSchema.Downtime),
                    ["reaction"] = Catalog(ParameterSchema.Reaction),
                },
            };
            return d;
        }

        /// <summary>The sections of <see cref="Describe"/>, in its order, with what each holds: <c>simulean_vocabulary</c>
        /// answers an index of them by default and one or several by <c>section=</c> (the whole is over the size an agent's
        /// tool reads).</summary>
        public static readonly IReadOnlyList<(string name, string holds)> Sections = new[]
        {
            ("arguments", "every command argument under its concept (with its meaning) and the commands that take it"),
            ("retired", "the names that were replaced and their name now (not accepted)"),
            ("scopes", "the factor / metric / result scopes"),
            ("paths", "the factor and metric path grammar, an example per form"),
            ("statistics", "the statistic names per scope (element, team, list, downtime, tally, state…)"),
            ("states", "the state names: declared, and the scene model's"),
            ("expressions", "the expression roots, item / element built-ins, contexts, statistics, functions and rules"),
            ("options", "the option enums: samplers, routing, input, preemption, process release, run end reasons"),
            ("catalog", "the catalog keys a study varies (factors), with their rule"),
        };

        /// <summary>
        /// The index of a vocabulary (<paramref name="whole"/>, as <see cref="Describe"/> gives it): each section's name,
        /// what it holds and its size in characters (indented, as an agent's tool shows it); a section over
        /// <paramref name="maxChars"/> also lists its parts (<c>section=name.part</c>), and <c>ask</c> says how to read them.
        /// </summary>
        public static Dictionary<string, object> Index(Dictionary<string, object> whole, int maxChars = AnswerPages.MaxChars)
        {
            var sections = new List<object>();
            foreach (string name in whole.Keys)
            {
                var e = new Dictionary<string, object>
                {
                    ["section"] = name,
                    ["holds"] = Sections.FirstOrDefault(s => s.name == name).holds ?? "",
                    ["size"] = AnswerPages.SizeOf(new Dictionary<string, object> { [name] = whole[name] }),
                };
                if ((int)e["size"] > maxChars) e["parts"] = PartsOf(whole[name]).Select(p => (object)p.name).ToList();
                sections.Add(e);
            }
            int size = AnswerPages.SizeOf(whole);
            return new Dictionary<string, object>
            {
                ["sections"] = sections,
                ["ask"] = "simulean_vocabulary section=<name> answers a section (several: section=paths,statistics; a part of a large " +
                          "one: section=arguments.study); section=all answers the whole vocabulary " +
                          $"(~{size / 1000} KB{(size > maxChars ? $", over the ~{maxChars / 1000} KB a tool answer holds" : "")}).",
            };
        }

        /// <summary>
        /// Sections of a vocabulary (<paramref name="whole"/>): <paramref name="section"/> = a section name, several
        /// comma-separated, a part of one (<c>arguments.study</c>: a concept; <c>expressions.functions</c>: a key) or
        /// <c>all</c> (the whole). The answer keeps the vocabulary's shape (<c>{"paths": […]}</c>, a part inside its section),
        /// so the sections together are the whole. An unknown name fails, naming the sections.
        /// </summary>
        public static Dictionary<string, object> Section(Dictionary<string, object> whole, string section)
        {
            var asked = (section ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (asked.Count == 0 || asked.Any(a => string.Equals(a, "all", StringComparison.OrdinalIgnoreCase))) return whole;
            var answer = new Dictionary<string, object>();
            foreach (string a in asked)
            {
                int dot = a.IndexOf('.');
                string name = dot < 0 ? a : a.Substring(0, dot), part = dot < 0 ? null : a.Substring(dot + 1).Trim();
                string key = whole.Keys.FirstOrDefault(k => k == name) ?? whole.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                if (key == null)
                    throw new ArgumentException($"section: no section '{name}' — one of {string.Join(", ", whole.Keys)}, or all." +
                                                NameSuggestions.DidYouMean(name, whole.Keys.ToList()));
                if (part == null) { answer[key] = whole[key]; continue; }
                var parts = PartsOf(whole[key]);
                var hit = parts.FirstOrDefault(p => p.name == part);
                if (hit.name == null) hit = parts.FirstOrDefault(p => string.Equals(p.name, part, StringComparison.OrdinalIgnoreCase));
                if (hit.name == null)
                    throw new ArgumentException($"section: '{key}' has no part '{part}' — " +
                                                (parts.Count == 0 ? "it has no parts: ask it whole." : $"one of {string.Join(", ", parts.Select(p => p.name))}.") +
                                                NameSuggestions.DidYouMean(part, parts.Select(p => p.name).ToList()));
                if (whole[key] is Dictionary<string, object>)
                {
                    var into = answer.TryGetValue(key, out object had) && had is Dictionary<string, object> h ? h : new Dictionary<string, object>();
                    into[hit.name] = hit.value;
                    answer[key] = into;
                }
                else
                {
                    var into = answer.TryGetValue(key, out object had) && had is List<object> l ? l : new List<object>();
                    into.Add(hit.value);
                    answer[key] = into;
                }
            }
            return answer;
        }

        // the parts of a section: a dictionary's keys, or a list's records by their first text (a concept, a form)
        private static List<(string name, object value)> PartsOf(object section)
        {
            var parts = new List<(string, object)>();
            if (section is Dictionary<string, object> d)
                foreach (var kv in d) parts.Add((kv.Key, kv.Value));
            else if (section is IEnumerable<object> list)
                foreach (var item in list)
                    if (item is Dictionary<string, object> r && r.Values.OfType<string>().FirstOrDefault() is string n)
                        parts.Add((n, item));
            return parts;
        }

        private static List<object> Names<T>() where T : struct, Enum
            => Enum.GetNames(typeof(T)).Select(n => (object)n.ToLowerInvariant()).ToList();

        private static Dictionary<string, object> Statistics()
        {
            var s = new Dictionary<string, object>();
            foreach (ResultScope scope in Enum.GetValues(typeof(ResultScope)))
            {
                var names = ResultMetrics.Of(scope);
                if (names.Count > 0) s[scope.ToString().ToLowerInvariant()] = names.ToList();
            }
            s["repair"] = ResultMetrics.Repair.ToList();
            s["preemption"] = ResultMetrics.Preemption.ToList();
            s["setup"] = ResultMetrics.Setup.ToList();
            s["timeout"] = ResultMetrics.Timeout.ToList();   // an element with a max wait
            s["source"] = new List<string> { ResultMetrics.LostArrivals };   // a source that loses its arrivals while stopped
            s["queue"] = ResultMetrics.QueueStay.ToList();       // a Queue's stay percentile
            s["sink"] = ResultMetrics.TimeInSystem.ToList();     // a Sink's time in system
            s["tally"] = new Dictionary<string, object> { ["prefix"] = ResultMetrics.TallyPrefix, ["statistics"] = ResultMetrics.TallyStatistics.ToList() };
            s["state"] = new Dictionary<string, object> { ["prefix"] = ResultMetrics.StatePrefix };
            s["field"] = new Dictionary<string, object> { ["prefix"] = ResultMetrics.FieldPrefix };
            return s;
        }

        /// <summary>The state names declared in the engine (every public static <see cref="ElementState"/> field).</summary>
        public static List<string> DeclaredStates()
            => typeof(ElementState).Assembly.GetTypes()
                .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(ElementState)))
                .Select(f => ((ElementState)f.GetValue(null)).Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

        private static Dictionary<string, object> States(SimulationConfig model)
        {
            var declared = DeclaredStates();
            var d = new Dictionary<string, object> { ["declared"] = declared };
            if (model == null) return d;
            try
            {
                new SimuLean.Headless.HeadlessModelFactory(new SimClock(1), enableLogging: false).BuildModel(model);
                d["model"] = ElementState.KnownNames.Except(declared).OrderBy(n => n, StringComparer.Ordinal).ToList();
            }
            catch (Exception e) { d["modelError"] = e.Message; }
            return d;
        }

        private static List<object> Catalog(ParameterSchema schema)
        {
            var list = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, rule, options, label) in schema.Declarations)
            {
                string k = key;   // a list's records once: actions[].kind
                if (!seen.Add(k)) continue;
                var e = new Dictionary<string, object> { ["key"] = k, ["rule"] = rule };
                if (options != null) e["options"] = options.ToList();
                if (label != null) e["label"] = label;
                list.Add(e);
            }
            return list;
        }

    }
}
