using System.Collections.Generic;
using System.Linq;
using SimuLean.Logic;
using SimuLean.Results.Charts;

namespace SimuLean.Unity
{
    /// <summary>
    /// The Play inspector's <i>Decisions</i> rows: for the element shown, each of its decisions that is not the
    /// default (<see cref="LogicOwner.Decisions"/>: <c>Route</c>, <c>Admit</c>, <c>Release order</c>, <c>Rule</c>…) with its
    /// run-time counters — items per output and the last choice with its scores, admission yes / no and wake-ups, releases,
    /// a rule's calls (<see cref="ModelLogic.Live"/>) — and its sentence as the note. Built into
    /// <see cref="ElementInspectorPanel"/> (<see cref="Shared"/>), a code contributor, not a component. Read-only: the report is
    /// made once per run from the model the run built (<see cref="SimuLean.Headless.HeadlessModelFactory.Config"/>) and the
    /// counters are only read, at the panel's refresh rate.
    /// </summary>
    public sealed class DecisionInspectorContributor : IElementInspectorContributor
    {
        /// <summary>The instance every panel uses.</summary>
        public static readonly DecisionInspectorContributor Shared = new DecisionInspectorContributor();

        private SimuLean.Headless.HeadlessModelFactory model;
        private LogicReport report;
        private readonly Dictionary<Element, LogicOwner> owners = new Dictionary<Element, LogicOwner>();

        // the report of the running model whose clock is the inspector's
        private LogicOwner OwnerOf(Element element, SimClock clock)
        {
            var sim = UnityCompat.FindAny<UnitySimClock>();
            var m = sim != null ? sim.Model : null;
            if (m == null || m.Config == null || (clock != null && !ReferenceEquals(m.Clock, clock))) return null;
            if (!ReferenceEquals(m, model))
            {
                model = m;
                owners.Clear();
                try { report = ModelLogic.Of(m.Config); }
                catch (System.Exception) { report = null; }   // a report it cannot make: no rows, never a failing panel
            }
            if (report == null || element == null) return null;
            if (!owners.TryGetValue(element, out var owner))
            {
                string id = m.GetAllElements().FirstOrDefault(kv => ReferenceEquals(kv.Value, element)).Key;
                owner = report.Owners.FirstOrDefault(o => o.Kind == "element" && id != null && o.Id == id);
                owners[element] = owner;
            }
            return owner;
        }

        public bool Supports(Element element)
        {
            var o = OwnerOf(element, element?.GetSimClock());
            return o != null && o.Decisions.Any(d => !d.IsDefault);
        }

        public void AddKpis(Element element, ElementInspectorContext context, List<InspectorRow> rows)
        {
            var o = OwnerOf(element, context?.Clock);
            if (o == null) return;
            ModelLogic.Live(report, model, o.Name);
            rows.Add(InspectorRow.H("Decisions"));
            foreach (var d in o.Decisions.Where(x => !x.IsDefault))
                rows.Add(new InspectorRow(d.Verb, d.LiveText ?? "–", d.Text));
        }

        public void AddDetail(Element element, ElementInspectorContext context, List<InspectorRow> rows) { }

        public void AddCharts(Element element, ElementInspectorContext context, List<ChartData> charts) { }
    }
}
