using System;
using System.Collections.Generic;

namespace SimuLean.Expressions
{
    /// <summary>
    /// Where an expression is written: the roots it may read, what a bare name means and which
    /// function families it may call. <see cref="Expression.Parse(string, ExpressionContext, string, string)"/> refuses a
    /// root or a function the place does not serve, so the error comes when the model is built (or when the text is typed),
    /// never in the middle of a run. The one table of contexts: <see cref="All"/> (shown by <c>simulean_vocabulary</c>).
    /// </summary>
    public sealed class ExpressionContext
    {
        /// <summary>Short name, used in messages ("a process time").</summary>
        public string Name { get; }
        /// <summary>Key in the vocabulary and the Unity attribute (<c>time</c>, <c>routing</c>…).</summary>
        public string Key { get; }
        /// <summary>The roots served (<c>item.x</c>, <c>element.x</c>…), each with what it reads here.</summary>
        public IReadOnlyDictionary<string, string> Roots { get; }
        /// <summary>The root a bare name reads (<c>item</c>, <c>entry</c>, <c>element</c>), <c>metric</c> / <c>factor</c> for
        /// the study formulas (names checked by the study), or null when bare names are not served.</summary>
        public string Bare { get; }
        /// <summary>Random functions (<c>rand uniform exponential normal pick</c>), from the use's own stream.</summary>
        public bool Random { get; }
        /// <summary>Functions that read the model (<see cref="Expression.ModelFunctions"/>).</summary>
        public bool Model { get; }
        /// <summary>Data-table functions (<c>table cell rows sum</c>).</summary>
        public bool Data { get; }
        /// <summary>True when <c>element</c> is the element the expression belongs to (its owner), so its property names are
        /// checked against that element; otherwise against every element of the model.</summary>
        public bool ElementIsOwner { get; }

        private ExpressionContext(string key, string name, string bare, bool random, bool model, bool data, bool elementIsOwner,
                                  params (string root, string meaning)[] roots)
        {
            Key = key; Name = name; Bare = bare; Random = random; Model = model; Data = data; ElementIsOwner = elementIsOwner;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (r, m) in roots) d[r] = m;
            Roots = d;
        }

        private static ExpressionContext Full(string key, string name, string element, string bare = "item", bool owner = true,
                                              params (string, string)[] more)
        {
            var roots = new List<(string, string)> { ("item", "the item"), ("element", element) };
            roots.AddRange(more);
            return new ExpressionContext(key, name, bare, true, true, true, owner, roots.ToArray());
        }

        /// <summary>A process / cycle / transport / setup / hold / delay / split time (sampler type Expression).</summary>
        public static readonly ExpressionContext Time = Full("time", "a time", "the element whose time it is");
        /// <summary>A request priority (Resources ▸ Process / Transport, an assembly task).</summary>
        public static readonly ExpressionContext Priority = Full("priority", "a request priority", "the requesting element");
        /// <summary>A routing expression (the output index).</summary>
        public static readonly ExpressionContext Routing = Full("routing", "a routing expression", "the routing element");
        /// <summary>A best-score routing's score: evaluated once per output, <c>out.x</c> the output scored.
        /// No random functions: a score compares the outputs, and it is evaluated again each time the item is offered.</summary>
        public static readonly ExpressionContext Score = new ExpressionContext("score", "a routing score", "item", false, true, true, true,
            ("item", "the item"), ("element", "the routing element"), ("out", "the output scored"));
        /// <summary>A key of a Queue's offer order (<c>offerSort</c>): read for every held item at every offer,
        /// so no random functions.</summary>
        public static readonly ExpressionContext OfferSort = new ExpressionContext("offerSort", "a queue's offer order", "item", false, true, true, true,
            ("item", "the held item"), ("element", "the queue"));
        /// <summary>An admission condition (input strategy Condition).</summary>
        public static readonly ExpressionContext Admission = Full("admission", "an admission condition", "the receiving element", more: ("origin", "the sending element"));
        /// <summary>A label record (creation, entry, exit labels; reaction values set as labels use the reaction's).</summary>
        public static readonly ExpressionContext Label = Full("label", "a label record", "the element setting it");
        /// <summary>An item statistic (tallies).</summary>
        public static readonly ExpressionContext Tally = Full("tally", "an item statistic", "the element recording it");
        /// <summary>A max wait (queue reneging, resources, list pulls).</summary>
        public static readonly ExpressionContext MaxWait = Full("maxWait", "a max wait", "the waiting element");
        /// <summary>An agenda's no-show / cancellation probability.</summary>
        public static readonly ExpressionContext Agenda = Full("agenda", "an agenda's probability", "the agenda");
        /// <summary>A call-off family condition.</summary>
        public static readonly ExpressionContext CallOff = Full("callOff", "a call-off condition", "the paced line");
        /// <summary>A sequencing station's applies condition.</summary>
        public static readonly ExpressionContext Sequencing = Full("sequencing", "a sequencing condition", "the station");
        /// <summary>A milk run's flow / empties filter.</summary>
        public static readonly ExpressionContext MilkRunFilter = Full("milkRunFilter", "a milk run's filter", "the milk run", more: ("origin", "the carrier's origin"));
        /// <summary>A resource member's property record (evaluated once when the member is built).</summary>
        public static readonly ExpressionContext MemberProperty = new ExpressionContext("memberProperty", "a resource member's property",
            "element", true, true, true, true, ("element", "the member"));
        /// <summary>A list's calculated field.</summary>
        public static readonly ExpressionContext ListField = new ExpressionContext("listField", "a list's calculated field",
            "entry", true, true, true, false, ("item", "the entry's item"), ("origin", "the element it comes from"), ("entry", "the entry (its fields)"));
        /// <summary>A milk run's calculated request field.</summary>
        public static readonly ExpressionContext RequestField = new ExpressionContext("requestField", "a milk run's request field",
            "entry", true, true, true, false, ("item", "the carrier"), ("origin", "the element it comes from"),
            ("destination", "where it goes"), ("entry", "the request (its fields)"));
        /// <summary>A reaction's condition / value on an item signal.</summary>
        public static readonly ExpressionContext ReactionItem = new ExpressionContext("reaction", "a reaction (item signal)",
            "item", true, true, true, false, ("item", "the signal's item"), ("element", "the signalling element"), ("signal", "what the signal carries"));
        /// <summary>A reaction's condition / value on an element signal (no item).</summary>
        public static readonly ExpressionContext ReactionElement = new ExpressionContext("reactionElement", "a reaction (element signal)",
            null, true, true, true, false, ("element", "the signalling element"), ("signal", "what the signal carries"));
        /// <summary>A reaction's condition / value on a time signal (no item, no element).</summary>
        public static readonly ExpressionContext ReactionTime = new ExpressionContext("reactionTime", "a reaction (time signal)",
            null, true, true, true, false, ("signal", "what the signal carries"));
        /// <summary>An optimiser's fitness: the metrics and numeric parameters by name.</summary>
        public static readonly ExpressionContext Fitness = new ExpressionContext("fitness", "a fitness", "metric", false, false, false, false);
        /// <summary>A study's <c>where</c> (the factors by name) and <c>derived</c> metrics (the metrics by name).</summary>
        public static readonly ExpressionContext Study = new ExpressionContext("study", "a study formula", "metric", false, false, false, false);
        /// <summary>Code that evaluates an expression over its own scope (tests, custom elements): every root and family.</summary>
        public static readonly ExpressionContext Any = new ExpressionContext("any", "an expression", "item", true, true, true, true,
            ("item", "the item"), ("element", "the element"), ("origin", "where the item comes from"), ("destination", "where it goes"),
            ("entry", "a list entry"), ("signal", "what a signal carries"), ("out", "an output scored"));

        /// <summary>Every context, in the order of the docs' table.</summary>
        public static readonly IReadOnlyList<ExpressionContext> All = new[]
        {
            Time, Priority, Routing, Score, OfferSort, Admission, Label, Tally, MaxWait, Agenda, CallOff, Sequencing, MilkRunFilter, MemberProperty,
            ListField, RequestField, ReactionItem, ReactionElement, ReactionTime, Fitness, Study,
        };

        /// <summary>The context of <paramref name="key"/> (<see cref="Key"/>), or null.</summary>
        public static ExpressionContext Of(string key)
        {
            foreach (var c in All) if (c.Key == key) return c;
            return key == Any.Key ? Any : null;
        }

        /// <summary>True when a bare name reads an item label (or a list entry's field): read at run time, never checked
        /// statically; a missing one is warned once.</summary>
        internal bool BareIsDynamic => Bare == "item" || Bare == "entry";

        public override string ToString() => Name;
    }
}
