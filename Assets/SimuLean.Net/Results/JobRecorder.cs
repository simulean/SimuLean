using System;
using System.Collections.Generic;
using SimuLean.Headless;

namespace SimuLean.Results
{
    /// <summary>One stay of an item in an element (the job Gantt): <c>[Start, End)</c>.</summary>
    public sealed class JobEntry
    {
        /// <summary>The item: its type and its number in arrival order (<c>Block 12</c>).</summary>
        public string Item { get; }
        public string Element { get; }
        public double Start { get; }
        public double End { get; }

        public JobEntry(string item, string element, double start, double end)
        {
            Item = item; Element = element; Start = start; End = end;
        }
    }

    /// <summary>
    /// Records where every item was and when: it listens to the link layer's <c>ItemEntered</c> /
    /// <c>ItemExited</c> of every element (item flow has one source: the links) and keeps each stay. Read-only; attached
    /// before the model starts when a run records its timeline (<see cref="Attach"/>). A stay in a sink (the item is done)
    /// is not a job step and is left out.
    /// </summary>
    public sealed class JobRecorder
    {
        private readonly HeadlessModelFactory model;
        private readonly Dictionary<(Item, Element), double> open = new Dictionary<(Item, Element), double>(new ByReference());
        private readonly List<JobEntry> closed = new List<JobEntry>();
        // items are numbered in the order they first enter an element (their ids are not unique across sources)
        private readonly Dictionary<Item, string> labels = new Dictionary<Item, string>(new ByReference());
        private bool cut;
        // an item that passes through without waiting is reported leaving before entering (the same instant, as the
        // link layer's statistics handle it): that stay lasts 0 and is not recorded
        private readonly HashSet<(Item, Element)> passed = new HashSet<(Item, Element)>(new ByReference());
        private double passedAt = double.NaN;

        private JobRecorder(HeadlessModelFactory model)
        {
            this.model = model;
            foreach (var kv in model.GetAllElements())
            {
                kv.Value.ItemEntered += OnEntered;
                kv.Value.ItemExited += OnExited;
            }
        }

        /// <summary>Starts recording the jobs of <paramref name="model"/> (call after building it, before it starts).</summary>
        public static JobRecorder Attach(HeadlessModelFactory model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            return model.Jobs ?? (model.Jobs = new JobRecorder(model));
        }

        private double Now => model.Clock.GetSimulationTime();

        private void OnEntered(Element e, Item item)
        {
            if (item == null || e is Sink) return;
            if (!labels.ContainsKey(item)) labels[item] = $"{item.GetTypeName()} {labels.Count + 1}";
            if (passedAt == Now && passed.Remove((item, e))) return;
            open[(item, e)] = Now;
        }

        private void OnExited(Element e, Item item)
        {
            if (item == null) return;
            if (!open.TryGetValue((item, e), out double start))
            {
                if (passedAt != Now) { passed.Clear(); passedAt = Now; }
                passed.Add((item, e));
                return;
            }
            open.Remove((item, e));
            if (closed.Count >= RunResults.MaxTimelineRows)
            {
                if (!cut) SimLog.Warning($"[SimuLean] Job timeline cut at {RunResults.MaxTimelineRows} rows.");
                cut = true;
                return;
            }
            if (Now > start) closed.Add(new JobEntry(Label(item), e.GetName(), start, Now));
        }

        /// <summary>Adds the jobs so far to <paramref name="results"/> (stays still going end now). Read-only.</summary>
        public void AddTo(RunResults results)
        {
            results.Jobs.AddRange(closed);
            double now = Now;
            foreach (var kv in open)
                if (now > kv.Value) results.Jobs.Add(new JobEntry(Label(kv.Key.Item1), kv.Key.Item2.GetName(), kv.Value, now));
        }

        private string Label(Item item) => labels.TryGetValue(item, out string l) ? l : item.GetTypeName();

        // items compare by identity here (an item's equality may follow its id, which is not unique)
        private sealed class ByReference : IEqualityComparer<Item>, IEqualityComparer<(Item, Element)>
        {
            public bool Equals(Item a, Item b) => ReferenceEquals(a, b);
            public int GetHashCode(Item i) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(i);
            public bool Equals((Item, Element) a, (Item, Element) b) => ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
            public int GetHashCode((Item, Element) k)
                => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item1) * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item2);
        }
    }
}
