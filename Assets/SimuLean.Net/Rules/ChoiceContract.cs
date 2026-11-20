using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Expressions;
using SimuLean.Resources;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Rules
{
    /// <summary>The decision sites a choice rule can decide, in the order they were built.</summary>
    public enum RuleSite
    {
        /// <summary>Where an item goes: the rule scores each output of the routing element (<see cref="IRoutingRule"/>).</summary>
        Routing,
        /// <summary>In which order a Queue offers its held items: the rule scores each held item (<see cref="IReleaseRule"/>).</summary>
        Release,
        /// <summary>Which pending request a resource team serves first: the rule scores each request (<see cref="IDispatchRule"/>).</summary>
        Dispatch,
    }

    /// <summary>
    /// A choice rule: a small C# class, in the assembly <c>SimuLean.UserLogic</c>, that makes one decision by
    /// scoring the candidates of one site. <b>The engine does the rest</b>: the highest score wins, ties keep the candidates'
    /// order (connection order, arrival order, request order), eligibility (an output's room, its admission, downtime) and
    /// "nothing now → wait" are the site's as for the declarative modes. A rule reads through its <see cref="ChoiceContext"/>
    /// and the candidate's read-only view and writes nothing: no item moved, no event scheduled, no label or property set, no
    /// static state, no <c>System.Random</c>, no Unity, no I/O. Its instance fields are its memory within a run (one instance
    /// per element and site, built with the model). Public fields marked <see cref="RuleParameterAttribute"/> are its
    /// parameters (catalog keys <c>&lt;Element&gt;.rule.&lt;Field&gt;</c>). Implement one of <see cref="IRoutingRule"/>,
    /// <see cref="IReleaseRule"/> or <see cref="IDispatchRule"/>; a public parameterless constructor is required.
    /// </summary>
    public interface IChoiceRule
    {
    }

    /// <summary>A routing rule: the score of one output for the item routed (<see cref="ChoiceContext.Item"/>) by the element
    /// (<see cref="ChoiceContext.Element"/>). Mirrors the <c>BestScore</c> routing: the highest score among the outputs that
    /// can take the item wins (<c>whenBestFull = Wait</c>: among those that admit it, and the item waits for the best).</summary>
    public interface IRoutingRule : IChoiceRule
    {
        /// <summary>The score of <paramref name="output"/> (higher = better; NaN stops the run).</summary>
        double Score(in ChoiceContext context, in ElementView output);
    }

    /// <summary>A release rule: the score of one held item of a Queue (<see cref="ChoiceContext.Element"/>). Mirrors a Queue's
    /// <c>offerSort</c>: the items are offered best first (highest score), ties in arrival order.</summary>
    public interface IReleaseRule : IChoiceRule
    {
        /// <summary>The score of <paramref name="item"/> (higher = offered first; NaN stops the run).</summary>
        double Score(in ChoiceContext context, in ItemView item);
    }

    /// <summary>A dispatch rule: the score of one pending request of a resource team (<see cref="ChoiceContext.Owner"/> = the
    /// team). Mirrors a team's request order: requests are tried best first (highest score), ties in request order; a request
    /// that cannot be served yet does not block the ones behind it.</summary>
    public interface IDispatchRule : IChoiceRule
    {
        /// <summary>The score of <paramref name="request"/> (higher = served first; NaN stops the run).</summary>
        double Score(in ChoiceContext context, in RequestView request);
    }

    /// <summary>Describes a choice rule: its label, <see cref="Why"/> (the declarative form tried first and why it was not
    /// enough) and what it reads (<see cref="Reads"/>, shown by the logic viewer). Optional, but the commands and the model card
    /// show it.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ChoiceRuleAttribute : Attribute
    {
        /// <summary>A rule with this label.</summary>
        public ChoiceRuleAttribute(string label = null) { Label = label; }
        /// <summary>Its label (null = the type name).</summary>
        public string Label { get; }
        /// <summary>Why a rule and not a declarative form (a field, an expression, a query / order, a reaction).</summary>
        public string Why { get; set; }
        /// <summary>What it reads, in the logic viewer's words (<c>out.content</c>, <c>item.due</c>, <c>stat('M1', 'utilization')</c>).</summary>
        public string[] Reads { get; set; }
        /// <summary>The declarative form the rule decides exactly as with every <see cref="RuleParameterAttribute"/> field at its
        /// initial value (<c>outputStrategy.strategyType = BestScore, outputStrategy.scoreExpression = -out.content</c>), when there
        /// is one: <c>simulean_validate</c> then warns on a use of the rule so. Null = none (the usual case).</summary>
        public string Equivalent { get; set; }
    }

    /// <summary>A public instance field of a choice rule that is a parameter: a number, a whole number, a yes / no, a text or
    /// an option (an enum). Its value comes from the owner's <c>rule</c> records (<c>&lt;Element&gt;.rule.&lt;Field&gt;</c>;
    /// a team's <c>team:T.rule.&lt;Field&gt;</c>), else the field's own initial value. Numbers and yes / no are catalog
    /// parameters (the optimiser and the Experimenter vary them); texts and options are not.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RuleParameterAttribute : Attribute
    {
        /// <summary>A parameter with this label.</summary>
        public RuleParameterAttribute(string label = null) { Label = label; }
        /// <summary>Its label.</summary>
        public string Label { get; }
        /// <summary>The smallest valid value (NaN = none); checked at build.</summary>
        public double Min { get; set; } = double.NaN;
        /// <summary>The largest valid value (NaN = none); checked at build.</summary>
        public double Max { get; set; } = double.NaN;
    }

    /// <summary>An exception inside a choice rule (or a NaN score): it stops the run naming the time, the owner, the rule and
    /// the message, as an <see cref="ExpressionException"/> does.</summary>
    public sealed class ChoiceRuleException : InvalidOperationException
    {
        /// <summary>The simulation time of the decision.</summary>
        public double Time { get; }
        /// <summary>The element or team that decided.</summary>
        public string Owner { get; }
        /// <summary>The rule's type name.</summary>
        public string Rule { get; }

        public ChoiceRuleException(string message, double time, string owner, string rule, Exception inner) : base(message, inner)
        { Time = time; Owner = owner; Rule = rule; }
    }

    /// <summary>
    /// What a choice rule may read while it decides (a <c>readonly struct</c> made once per decision): the time, the item
    /// decided (routing), the element deciding, the owner's name, the number of candidates, model reads by name (an element's
    /// content / free room / property / statistic, a team's and a list's values — the expression functions <c>content</c>,
    /// <c>free</c>, <c>get</c>, <c>stat</c>, <c>team</c>, <c>list</c>) and the rule's own random stream
    /// (<see cref="Random"/>). Nothing in it writes.
    /// </summary>
    public readonly struct ChoiceContext
    {
        private readonly SimClock clock;

        internal ChoiceContext(RuleSite site, SimClock clock, string owner, Element element, Item item, int candidates, RuleRandom random)
        {
            Site = site;
            this.clock = clock;
            Owner = owner;
            Now = clock?.GetSimulationTime() ?? 0;
            Element = new ElementView(element);
            Item = new ItemView(item, Now);
            Candidates = candidates;
            Random = random;
        }

        /// <summary>The site deciding.</summary>
        public RuleSite Site { get; }
        /// <summary>The simulation time.</summary>
        public double Now { get; }
        /// <summary>The name of the element (routing, release) or team (dispatch) that decides.</summary>
        public string Owner { get; }
        /// <summary>The element that decides (routing: the sender; release: the Queue); none for dispatch.</summary>
        public ElementView Element { get; }
        /// <summary>The item decided (routing); none elsewhere (the release candidate is the item scored).</summary>
        public ItemView Item { get; }
        /// <summary>The number of candidates of this decision.</summary>
        public int Candidates { get; }
        /// <summary>The rule's own random stream (<c>&lt;owner&gt;/rule:&lt;site&gt;</c>): its draws are fixed per decision —
        /// the same item / request / output gets the same numbers each time it is scored again — so a seeded run is
        /// reproducible and re-scoring a waiting item does not redraw.</summary>
        public RuleRandom Random { get; }

        private SimContext Model => clock?.Context ?? throw new InvalidOperationException("the rule has no model to read");

        /// <summary>The element of this name (an error when there is none).</summary>
        public ElementView ElementNamed(string name)
        {
            var e = Model.FindElement(name);
            if (e == null) throw new InvalidOperationException($"no element '{name}' in the model");
            return new ElementView(e);
        }

        /// <summary>The items in the element (<c>content('name')</c>).</summary>
        public double Content(string element) => ElementNamed(element).Content;
        /// <summary>The free room of the element (<c>free('name')</c>).</summary>
        public double Free(string element) => ElementNamed(element).Free;
        /// <summary>A number property of the element (<c>get('name', 'p')</c>; NaN when missing or a text).</summary>
        public double Get(string element, string property) => ElementNamed(element).Get(property);
        /// <summary>A statistic of the element (<c>stat('name', 'utilization')</c>; NaN while it has no value).</summary>
        public double Stat(string element, string metric) => ElementNamed(element).Stat(metric);

        /// <summary>A value of a resource team (<c>team('T', 'free')</c>: the <c>TeamMetrics</c> names).</summary>
        public double Team(string team, string what)
        {
            var d = Model.FindTeam(team) ?? throw new InvalidOperationException($"no resource team '{team}' in the model");
            if (SimuLean.Results.TeamMetrics.TryRead(d, what, Now, out double v)) return v;
            throw new InvalidOperationException($"'{what}' is not a team value ({string.Join(", ", SimuLean.Results.TeamMetrics.Names)})");
        }

        /// <summary>A value of a model list (<c>list('L', 'content')</c>: the <c>ListMetrics</c> names).</summary>
        public double List(string list, string what)
        {
            var l = Model.GetList(list) ?? throw new InvalidOperationException($"no list '{list}' in the model");
            if (SimuLean.Results.ListMetrics.TryRead(l, what, Now, out double v)) return v;
            throw new InvalidOperationException($"'{what}' is not a list value ({string.Join(", ", SimuLean.Results.ListMetrics.Names)})");
        }
    }

    /// <summary>A read-only view of an element: what a rule may read of a candidate output, the deciding element or any
    /// element by name.</summary>
    public readonly struct ElementView
    {
        private readonly Element e;

        internal ElementView(Element element, int index = -1) { e = element; Index = index; }

        /// <summary>There is an element.</summary>
        public bool Exists => e != null;
        /// <summary>Its name.</summary>
        public string Name => e?.Name;
        /// <summary>A routing candidate's position among the outputs (connection order, 0 = first); −1 elsewhere.</summary>
        public int Index { get; }
        /// <summary>The items in it.</summary>
        public double Content => Number(EntityScope.OfElement(e, "content"));
        /// <summary>Its free room.</summary>
        public double Free => Number(EntityScope.OfElement(e, "free"));
        /// <summary>Its state's name (<c>IDLE</c>, <c>PROCESSING</c>, <c>BLOCKED</c>…).</summary>
        public string State => e?.CurrentState.Name;
        /// <summary>A number property (NaN when it is missing or a text).</summary>
        public double Get(string property) => Number(EntityScope.OfElement(e, property));
        /// <summary>A property as text (null when missing).</summary>
        public string Text(string property) { var v = EntityScope.OfElement(e, property); return v.Missing ? null : v.ToString(); }
        /// <summary>True when it has the property.</summary>
        public bool Has(string property) => e != null && e.HasProperties && e.Properties.Has(property);
        /// <summary>One of its statistics (the <c>ResultMetrics</c> element names; NaN while it has no value).</summary>
        public double Stat(string metric)
        {
            if (e == null) return double.NaN;
            if (SimuLean.Results.ElementMetrics.TryRead(e, metric, out double v)) return v;
            if (!SimuLean.Results.ElementMetrics.IsKnown(e, metric))
                throw new InvalidOperationException($"'{metric}' is not a statistic of an element ({string.Join(", ", SimuLean.Results.ElementMetrics.Names)})");
            return double.NaN;
        }

        internal static double Number(ExprValue v) => v.Missing || v.IsText ? double.NaN : v.Number;
    }

    /// <summary>A read-only view of an item: its labels and built-ins.</summary>
    public readonly struct ItemView
    {
        private readonly Item item;
        private readonly double now;

        internal ItemView(Item item, double now) { this.item = item; this.now = now; }

        /// <summary>There is an item.</summary>
        public bool Exists => item != null;
        /// <summary>Its id.</summary>
        public int Id => item?.GetId() ?? -1;
        /// <summary>Its type (the item's name).</summary>
        public string Type => item?.Name;
        /// <summary>Its priority.</summary>
        public double Priority => item?.priority ?? double.NaN;
        /// <summary>When it was created.</summary>
        public double Created => item?.GetCreationTime() ?? double.NaN;
        /// <summary>Its age (now − created).</summary>
        public double Age => item == null ? double.NaN : now - item.GetCreationTime();
        /// <summary>A number label (NaN when missing or a text).</summary>
        public double Get(string label) => ElementView.Number(EntityScope.OfItem(item, label, now));
        /// <summary>A label as text (null when missing).</summary>
        public string Text(string label) { var v = EntityScope.OfItem(item, label, now); return v.Missing ? null : v.ToString(); }
        /// <summary>True when it has the label.</summary>
        public bool Has(string label) => item != null && item.HasLabel(label);
    }

    /// <summary>A read-only view of a pending request of a resource team.</summary>
    public readonly struct RequestView
    {
        private readonly ResourceRequest r;
        private readonly double now;

        internal RequestView(ResourceRequest request, double now) { r = request; this.now = now; }

        /// <summary>There is a request.</summary>
        public bool Exists => r != null;
        /// <summary>Its priority (the use's <c>priority</c>).</summary>
        public double Priority => r?.Priority ?? double.NaN;
        /// <summary>When it was made.</summary>
        public double RequestTime => r?.RequestTime ?? double.NaN;
        /// <summary>How long it has waited.</summary>
        public double Age => r == null ? double.NaN : now - r.RequestTime;
        /// <summary>The members it asks for (every requirement's count).</summary>
        public int Count => r?.TotalCount ?? 0;
        /// <summary>Its task type (<c>Process</c>, <c>Transport</c>, <c>Setup</c>…).</summary>
        public string TaskType => r?.TaskType.ToString();
        /// <summary>Where the work is (a location), when known.</summary>
        public string Location => r?.Location;
        /// <summary>The element that asks (a station, a carrier's origin…).</summary>
        public ElementView Requester => new ElementView(r?.Requester);
        /// <summary>A number field of the request, as its list queries read it (<c>priority</c>, <c>requestTime</c>, <c>count</c>,
        /// <c>oldestItemTime</c>, <c>origin.x</c>; NaN when missing).</summary>
        public double Get(string field) => r?.GetListNumber(field) ?? double.NaN;
        /// <summary>A text field (<c>location</c>, <c>taskType</c>, <c>skill</c>, <c>origin.name</c>; null when missing).</summary>
        public string Text(string field) => r?.GetListText(field);
    }

    /// <summary>
    /// A rule's random numbers (<see cref="ChoiceContext.Random"/>), drawn from the stream the model gives the owner for the
    /// site (<c>clock.Context.CreateRandomStream(owner, "rule:&lt;site&gt;")</c>, created at the first draw: a rule that never
    /// draws takes no stream). <b>Per decision, not per evaluation:</b> the numbers drawn while scoring one candidate of one
    /// decision (an item and an output, a held item, a request) are recorded and given again each time that candidate is
    /// scored again (a waiting item re-offered), until it leaves. A seeded run is reproducible.
    /// </summary>
    public sealed class RuleRandom
    {
        private readonly Func<IRandomEngine> create;
        private IRandomEngine stream;
        private readonly Dictionary<object, List<List<double>>> recorded = new Dictionary<object, List<List<double>>>();
        private List<double> current;
        private int next;
        private object subject;
        private int slot;

        internal RuleRandom(Func<IRandomEngine> create) { this.create = create; }

        // the draws of a candidate: subject (the item / request) and slot (the output's index; 0 elsewhere)
        internal void Begin(object subject, int slot) { this.subject = subject; this.slot = slot; current = null; next = 0; }
        internal void End() { subject = null; current = null; }

        /// <summary>Forgets the draws of a subject that has left.</summary>
        internal void Forget(object s) { if (recorded.Count > 0 && s != null) recorded.Remove(s); }

        /// <summary>Forgets the draws of every subject not in <paramref name="still"/> (the items / requests still waiting).</summary>
        internal void KeepOnly(HashSet<object> still)
        {
            if (recorded.Count == 0) return;
            List<object> gone = null;
            foreach (var k in recorded.Keys) if (!still.Contains(k)) (gone ?? (gone = new List<object>())).Add(k);
            if (gone != null) foreach (var k in gone) recorded.Remove(k);
        }

        internal bool HasRecords => recorded.Count > 0;

        /// <summary>A uniform number in [0, 1).</summary>
        public double NextDouble()
        {
            if (current == null && subject != null)
            {
                if (!recorded.TryGetValue(subject, out var slots)) recorded[subject] = slots = new List<List<double>>();
                while (slots.Count <= slot) slots.Add(null);
                current = slots[slot] ?? (slots[slot] = new List<double>());
            }
            if (current != null && next < current.Count) return current[next++];
            double u = (stream ?? (stream = create())).NextDouble();
            if (current != null) { current.Add(u); next++; }
            return u;
        }

        /// <summary>A uniform number in [a, b).</summary>
        public double Uniform(double a, double b) => a + (b - a) * NextDouble();
        /// <summary>A whole number in [0, n).</summary>
        public int Index(int n) => n <= 0 ? throw new ArgumentOutOfRangeException(nameof(n)) : Math.Min(n - 1, (int)Math.Floor(NextDouble() * n));
        /// <summary>An exponential number of this mean.</summary>
        public double Exponential(double mean) => -mean * Math.Log(1.0 - NextDouble());
        /// <summary>A normal number (Box–Muller, two uniforms).</summary>
        public double Normal(double mean, double sd)
        {
            double u1 = 1.0 - NextDouble(), u2 = NextDouble();
            return SimuLean.Stochastics.Distributions.BoxMuller.Normal(mean, sd, u1, u2);
        }
    }

    /// <summary>One rule at one site of one owner, with its stream and its error handling: what the site wrappers share.</summary>
    public abstract class RuleSiteBase
    {
        /// <summary>The rule's instance (its fields are its memory within the run).</summary>
        public IChoiceRule Rule { get; }
        /// <summary>The element or team deciding.</summary>
        public string Owner { get; }
        /// <summary>The site.</summary>
        public RuleSite Site { get; }
        private RuleRandom random;
        private SimClock clock;

        protected RuleSiteBase(IChoiceRule rule, string owner, RuleSite site)
        {
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
            Owner = owner;
            Site = site;
        }

        /// <summary>The rule's type name.</summary>
        public string RuleName => Rule.GetType().Name;

        /// <summary>Decisions made so far (calls of the rule over a candidate set).</summary>
        public long Decisions { get; private set; }

        /// <summary>Errors of the rule (an exception or a NaN score; the first stops the run).</summary>
        public long Errors { get; private set; }

        /// <summary>The last score the rule gave (NaN: none yet).</summary>
        public double LastScore { get; private set; } = double.NaN;

        /// <summary>The item of the last decision (null: none yet, or a decision without an item) — its labels say why.</summary>
        public Item LastItem { get; private set; }

        internal RuleRandom RandomFor(SimClock c)
        {
            if (random == null || !ReferenceEquals(clock, c))
            {
                clock = c;
                random = new RuleRandom(() => c != null ? c.Context.CreateRandomStream(Owner, "rule:" + SettingsKey(Site))
                                                        : throw new InvalidOperationException("a rule outside a model has no random stream"));
            }
            return random;
        }

        internal RuleRandom Random => random;

        internal ChoiceContext Context(SimClock c, Element element, Item item, int candidates)
        {
            Decisions++;
            LastItem = item;
            return new ChoiceContext(Site, c, Owner, element, item, candidates, RandomFor(c));
        }

        /// <summary>A score checked: an exception or NaN stops the run with the time, the owner and the rule.</summary>
        protected double Checked(double score, SimClock c)
        {
            if (double.IsNaN(score)) throw Fail(new InvalidOperationException("the score is NaN"), c);
            LastScore = score;
            return score;
        }

        protected ChoiceRuleException Fail(Exception e, SimClock c)
        {
            if (e is ChoiceRuleException cre) return cre;
            Errors++;
            double now = c?.GetSimulationTime() ?? 0;
            string kind = Site == RuleSite.Dispatch ? "Resource team" : "Element";
            return new ChoiceRuleException(
                $"t={now.ToString("0.###", CultureInfo.InvariantCulture)} {kind} '{Owner}': {SettingsKey(Site)} rule '{RuleName}': {e.Message}",
                now, Owner, RuleName, e);
        }

        /// <summary>The site's name in settings and stream keys (<c>routing</c>, <c>release</c>, <c>dispatch</c>).</summary>
        public static string SettingsKey(RuleSite site) => site == RuleSite.Routing ? "routing" : site == RuleSite.Release ? "release" : "dispatch";
    }
}
