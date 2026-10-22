using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Serialization;

namespace SimuLean.Headless
{
    /// <summary>
    /// Run control of a built model, installed by
    /// <see cref="HeadlessModelFactory.StartModel"/> — so scene runs and headless runs honour it alike:
    /// <list type="bullet">
    /// <item><b>end condition</b> — stop when the elements <see cref="SimulationConfig.StopAt"/> (sinks, or the buffers that
    /// hold the finished product) have received <see cref="SimulationConfig.StopAtCount"/> items between them, and / or
    /// when nothing is left to do (<see cref="SimulationConfig.StopWhenEmpty"/>); the clock stops
    /// (<see cref="SimClock.Stop"/>) and the statistics end there;</item>
    /// <item><b>empty / finished / stalled / deadlock</b> — when no event that can move items is left
    /// (<see cref="SimClock.Idle"/>), the items still in the model are sorted out:
    /// <list type="bullet">
    /// <item><i>at rest</i>: in a buffer (queue, lane) without outputs — the finished product, not waiting;</item>
    /// <item><i>surplus</i>: ready to leave, but every element downstream is empty (or holds surplus itself) — no element
    /// will ever take them, e.g. an unlimited source blocked behind a full rack whose consumers are done (#26);</item>
    /// <item><i>starved</i>: components in a Combiner with no main item while its main input's supply is exhausted (every
    /// element upstream of the main input is empty — at Idle no source will create anything more): they wait for
    /// material that will never come, which is not a deadlock (#28);</item>
    /// <item><i>waiting</i>: the others.</item>
    /// </list>
    /// No item at all = <b>empty</b>; none waiting = <b>finished</b> (not a stall); waiting = <b>stalled</b> (logged; an
    /// element under a stop is no exception: at Idle no event is left to end the stop); stalled with an item ready to leave that is not surplus — an element BLOCKED or
    /// WAITING_FOR_RESOURCE, or a buffer with outputs, behind an element that holds items — = <b>deadlock</b> (warned
    /// once). Each waiting element is named with what it waits for, and the outcome is flagged in
    /// <see cref="SimContext.Run"/>.</item>
    /// </list>
    /// <see cref="IsTerminal"/> (no outputs) also gives the results their makespan: the last entry into a terminal element.
    /// Read-only towards the model (it moves nothing, schedules nothing).
    /// </summary>
    public sealed class RunMonitor
    {
        private readonly SimClock clock;
        private readonly IReadOnlyDictionary<string, Element> elements;
        private readonly Dictionary<Element, List<Element>> downstream = new Dictionary<Element, List<Element>>();
        private readonly HashSet<Element> withOutputs = new HashSet<Element>();   // any outgoing connection (elements, lists)
        private readonly Dictionary<Element, List<Element>> upstream = new Dictionary<Element, List<Element>>();
        private readonly HashSet<Element> fedByList = new HashSet<Element>();     // an incoming connection from a list
        // lists: what an element pushes to, who pulls from a list, the list a Combiner port pulls from
        private readonly Dictionary<string, string> listNames = new Dictionary<string, string>();
        private readonly Dictionary<Element, List<string>> pushesTo = new Dictionary<Element, List<string>>();
        private readonly Dictionary<string, List<Element>> pullers = new Dictionary<string, List<Element>>();
        private readonly Dictionary<(Element, int), string> portList = new Dictionary<(Element, int), string>();
        private readonly bool stopWhenEmpty;
        private readonly int stopAtCount;
        private int endCount;

        public RunMonitor(SimClock clock, IReadOnlyDictionary<string, Element> elements, SimulationConfig config)
        {
            this.clock = clock;
            this.elements = elements;
            stopWhenEmpty = config.StopWhenEmpty;
            stopAtCount = config.StopAtCount;

            if (config.Lists != null)
                foreach (var l in config.Lists) if (l?.Id != null) listNames[l.Id] = string.IsNullOrEmpty(l.Name) ? l.Id : l.Name;
            foreach (var c in config.Connections)
            {
                if (c == null) continue;
                bool fromElement = elements.TryGetValue(c.SourceId ?? "", out var from);
                bool toElement = elements.TryGetValue(c.TargetId ?? "", out var to);
                if (c.ConnectionType == "PullFromList" && toElement)
                {
                    if (!pullers.TryGetValue(c.SourceId, out var ps)) pullers[c.SourceId] = ps = new List<Element>();
                    if (!ps.Contains(to)) ps.Add(to);
                    int port = c.GetParameter<int>("port", -1);
                    if (to is Combiner) portList[(to, port < 0 ? 0 : port)] = c.SourceId;
                }
                if (c.ConnectionType == "PushToList" && fromElement)
                {
                    if (!pushesTo.TryGetValue(from, out var ls)) pushesTo[from] = ls = new List<string>();
                    if (!ls.Contains(c.TargetId)) ls.Add(c.TargetId);
                }
                if (!fromElement)
                {
                    if (toElement) fedByList.Add(to);   // pulls from a list: unknown supply
                    continue;
                }
                withOutputs.Add(from);
                if (!toElement) continue;
                if (!downstream.TryGetValue(from, out var list)) downstream[from] = list = new List<Element>();
                if (!list.Contains(to)) list.Add(to);
                if (!upstream.TryGetValue(to, out var ups)) upstream[to] = ups = new List<Element>();
                if (!ups.Contains(from)) ups.Add(from);
            }

            if (config.StopAt != null && config.StopAt.Count > 0 && stopAtCount > 0)
                foreach (string name in config.StopAt)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var e = Find(name) ?? throw new ArgumentException($"End condition: no element '{name}' (stop at).");
                    e.ItemEntered += OnEndEntry;
                }
            clock.Idle += OnIdle;
        }

        /// <summary>True when <paramref name="e"/> has no outputs: a sink, or a buffer / station at the end of the line.</summary>
        public bool IsTerminal(Element e) => !withOutputs.Contains(e);

        /// <summary>The element of id or display name <paramref name="idOrName"/> (null if none).</summary>
        private Element Find(string idOrName)
        {
            if (elements.TryGetValue(idOrName, out var e)) return e;
            foreach (var x in elements.Values) if (x.GetName() == idOrName) return x;
            return null;
        }

        private void OnEndEntry(Element element, Item item)
        {
            if (++endCount < stopAtCount || clock.IsStopped) return;
            End(RunEndReason.StopAtCount);
        }

        private void End(RunEndReason reason)
        {
            var run = clock.Context.Run;
            run.EndReason = reason;
            run.EndTime = clock.GetSimulationTime();
            clock.Stop();
        }

        private void OnIdle()
        {
            var run = clock.Context.Run;
            var holding = new List<Element>();
            foreach (var e in elements.Values)
            {
                if (e is Sink) continue;
                if (Held(e) > 0) holding.Add(e);
            }
            var waiting = holding.FindAll(e => !AtRest(e) && !Starved(e) && !Surplus(e, new HashSet<Element>()));
            double now = clock.GetSimulationTime();
            if (waiting.Count == 0)
            {
                if (holding.Count == 0 && double.IsNaN(run.EmptyTime)) run.EmptyTime = now;
                if (double.IsNaN(run.FinishTime))
                {
                    run.FinishTime = now;
                    if (holding.Count > 0)
                        SimLog.Info($"[SimuLean] Finished at t = {now.ToString("G6", CultureInfo.InvariantCulture)}: nothing left to do " +
                                    $"(items at rest or surplus — {string.Join("; ", holding.ConvertAll(Describe))}).");
                }
                if (stopWhenEmpty) End(holding.Count == 0 ? RunEndReason.Empty : RunEndReason.Finished);
                return;
            }
            // A stopped element does not excuse the stall: every stop's end (a generator's repair or next shift, a reaction's
            // "stop for a time", a clock signal) is a pending event, and none is left — a stop still active now never ends
            // (benchmark C4, 2026-10-13: a repair waiting for the operator its paused machine holds was reported neither as
            // a stall nor as a deadlock).
            if (run.Stalled) return;

            run.Stalled = true;
            run.StallTime = now;
            foreach (var e in waiting) run.Waiting.Add(Describe(e));
            bool blocked = waiting.Exists(ReadyToLeave);
            string t = run.StallTime.ToString("G6", CultureInfo.InvariantCulture);
            if (blocked)
            {
                run.Deadlocked = true;
                run.DeadlockTime = run.StallTime;
                SimLog.Warning($"[SimuLean] Deadlock at t = {t}: no event can move the {waiting.Count} element(s) holding items — {string.Join("; ", run.Waiting)}.");
            }
            else
                SimLog.Info($"[SimuLean] Stalled at t = {t}: items wait but nothing can happen (none blocked) — {string.Join("; ", run.Waiting)}.");
            if (stopWhenEmpty) End(blocked ? RunEndReason.Deadlock : RunEndReason.Stalled);
        }

        private static bool IsBuffer(Element e) => e is ItemsQueue || e is LengthLimitedQueue;

        // Items at rest: a buffer without outputs (the end of the line) — they do not wait for anything.
        private bool AtRest(Element e) => IsBuffer(e) && IsTerminal(e);

        // Items wait to go on: an element BLOCKED or waiting for a resource, or a buffer (queue, lane) with outputs — its
        // items are ready to leave (a refused queue stays PROCESSING until it retries).
        private bool ReadyToLeave(Element e)
            => e.CurrentState == ElementState.BLOCKED || e.CurrentState == ElementState.WAITING_FOR_RESOURCE
               || (IsBuffer(e) && !IsTerminal(e));

        // Surplus: ready to leave, and every element downstream is empty (and not stopped) or holds surplus itself —
        // nothing will ever ask for these items. A downstream element holding items that are not surplus
        // (a full buffer at the end of the line, a station waiting for parts), a sink (it always takes: refusing is a real
        // problem) or outputs to a list only (unknown demand) are obstacles: not surplus.
        private bool Surplus(Element e, HashSet<Element> visiting)
        {
            if (!ReadyToLeave(e) || !downstream.TryGetValue(e, out var next) || next.Count == 0) return false;
            if (!visiting.Add(e)) return false;   // a cycle of full elements: not surplus
            foreach (var d in next)
            {
                // a sink or an element keeping items (an erection dock waiting for a block's predecessor) always means to
                // take them: its refusal is a real problem, not surplus
                if (d is Sink || d is IKeepsItems || d.IsDown) return false;
                if (Held(d) == 0 || Starved(d)) continue;
                if (!Surplus(d, visiting)) return false;
            }
            return true;
        }

        // Starved (#28): a Combiner holding components only, whose main input's supply is exhausted — the element feeding
        // its main input and everything upstream of it hold nothing (at Idle no source will create more). A main input
        // fed by a list, or not wired, is an unknown supply: not starved.
        private bool Starved(Element e)
        {
            if (!(e is Combiner c) || !HoldsComponentsOnly(c)) return false;
            var main = c.MainOrigin;
            return main != null && SupplyExhausted(main, new HashSet<Element>());
        }

        private static bool HoldsComponentsOnly(Combiner c)
        {
            if (c.Assemblies == null) return false;
            foreach (var a in c.Assemblies) if (a.Main != null) return false;
            return true;
        }

        private bool SupplyExhausted(Element e, HashSet<Element> visited)
        {
            if (!visited.Add(e)) return true;
            if (e.IsDown || fedByList.Contains(e) || Held(e) > 0) return false;
            if (upstream.TryGetValue(e, out var ups))
                foreach (var u in ups) if (!SupplyExhausted(u, visited)) return false;
            return true;
        }

        // Items an element holds: the link layer's content (a Combiner's main item and its components included).
        // items an element holds that are not at rest (an IKeepsItems element's kept items are: the hull)
        private static int Held(Element e)
            => Math.Max(e.GetQueueLength(), (int)Math.Round(e.GetStatsContentCurrent())) - (e is IKeepsItems k ? k.KeptCount : 0);

        /// <summary>What <paramref name="e"/> holds and waits for, as the stall report names it
        /// ("Yard: BLOCKED, 2 item(s) → waits for BA_Bay1, BA_Bay2"); the diagnosis reads it at any time.</summary>
        public string WaitsFor(Element e) => Describe(e);

        /// <summary>Items <paramref name="e"/> holds that are not at rest (the link layer's content).</summary>
        public static int ItemsHeld(Element e) => Held(e);

        // "Yard: BLOCKED, 2 item(s) → BA_Bay1, BA_Bay2" (what it waits for, from its state). Upstream for a Combiner gathering:
        // holding components only, its main input (#28); holding its main item, the inputs still missing components — the
        // elements wired to them or the lists they pull from (#31). An element pushing to a list waits for its pullers (#31).
        private string Describe(Element e)
        {
            string state = e.CurrentState.Name ?? "?";
            string text = $"{e.GetName()}: {state}, {Held(e)} item(s)";
            if (e is Combiner c)
            {
                if (HoldsComponentsOnly(c)) return text + " → waits for its main input" + Source(c, 0, " ");
                if (e.CurrentState != ElementState.BLOCKED)
                {
                    var missing = MissingSources(c);
                    if (missing.Count > 0) return text + " → waits for components from " + string.Join(", ", missing);
                }
            }
            var waitsFor = new List<string>();
            if (downstream.TryGetValue(e, out var next)) waitsFor.AddRange(next.ConvertAll(x => x.GetName()));
            if (pushesTo.TryGetValue(e, out var lists))
                foreach (string l in lists)
                    waitsFor.Add($"list {ListName(l)}" + (pullers.TryGetValue(l, out var ps) && ps.Count > 0
                        ? " (pulled by " + string.Join(", ", ps.ConvertAll(p => p.GetName())) + ")" : " (no puller)"));
            if (waitsFor.Count > 0) text += " → waits for " + string.Join(", ", waitsFor);
            return text;
        }

        private string ListName(string id) => listNames.TryGetValue(id, out string n) ? n : id;

        // where connection index k of a Combiner gets its items: the element wired to it, or the list it pulls from
        private string Source(Combiner c, int connectionIndex, string prefix = "")
        {
            var origin = c.InputOrigin(connectionIndex);
            if (origin != null) return prefix + origin.GetName();
            return portList.TryGetValue((c, connectionIndex), out string l) ? prefix + "list " + ListName(l) : "";
        }

        // the sources of the component inputs that some gathering assembly still lacks
        private List<string> MissingSources(Combiner c)
        {
            var list = new List<string>();
            if (c.Assemblies == null) return list;
            foreach (var a in c.Assemblies)
            {
                if (a.Main == null || a.State == ElementState.BLOCKED || a.Requirements == null) continue;
                for (int i = 0; i < a.Requirements.Length; i++)
                {
                    if (a.Parts[i].Count >= a.Requirements[i]) continue;
                    string s = Source(c, i + 1);
                    if (s.Length == 0) s = $"component input {i + 1}";
                    if (!list.Contains(s)) list.Add(s);
                }
            }
            return list;
        }
    }
}
