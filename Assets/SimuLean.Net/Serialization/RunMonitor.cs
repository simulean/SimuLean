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
    /// will ever take them, e.g. an unlimited source blocked behind a full rack whose consumers are done;</item>
    /// <item><i>starved</i>: components in a Combiner with no main item while its main input's supply is exhausted (every
    /// element upstream of the main input is empty — at Idle no source will create anything more): they wait for
    /// material that will never come, which is not a deadlock;</item>
    /// <item><i>waiting</i>: the others.</item>
    /// </list>
    /// No item at all = <b>empty</b>; none waiting = <b>finished</b> (not a stall); waiting = <b>stalled</b> (logged; an
    /// element under a stop is no exception: at Idle no event is left to end the stop); stalled with an item ready to leave that is not surplus — an element BLOCKED or
    /// WAITING_FOR_RESOURCE, or a buffer with outputs, behind an element that holds items — = <b>deadlock</b> (warned
    /// once). Each waiting element is named with what it waits for, and the outcome is flagged in
    /// <see cref="SimContext.Run"/>.</item>
    /// <item><b>no progress</b> (<see cref="SimulationConfig.StopWhenNoProgressFor"/>) — events that never run out (a
    /// periodic signal, a reaction) keep the clock from becoming idle; the run then ends (<see cref="RunEndReason.NoProgress"/>)
    /// when no item has moved for that time while items wait.</item>
    /// </list>
    /// <see cref="IsTerminal"/> (no outputs) also gives the results their makespan: the last entry into a terminal element.
    /// Read-only towards the model: it moves nothing and schedules only the no-progress check (a background event).
    /// </summary>
    public sealed class RunMonitor : IWarmupStatistics
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
        private readonly IReadOnlyList<Downtime.DowntimeGenerator> downtimes;
        private readonly IReadOnlyCollection<SimuLean.Resources.ResourceDispatcher> teams;
        private readonly bool stopWhenEmpty;
        private readonly int stopAtCount;
        private int endCount;
        // "no progress" end condition: one background check, due when no item has moved for noProgressFor
        private readonly double noProgressFor;
        private readonly double startTime;
        private readonly IEvent progressCheck;

        public RunMonitor(SimClock clock, IReadOnlyDictionary<string, Element> elements, SimulationConfig config,
                          IReadOnlyList<Downtime.DowntimeGenerator> downtimes = null,
                          IReadOnlyCollection<SimuLean.Resources.ResourceDispatcher> teams = null)
        {
            this.clock = clock;
            this.elements = elements;
            this.downtimes = downtimes ?? Array.Empty<Downtime.DowntimeGenerator>();
            this.teams = teams ?? Array.Empty<SimuLean.Resources.ResourceDispatcher>();
            stopWhenEmpty = config.StopWhenEmpty;
            stopAtCount = config.StopAtCount;
            noProgressFor = config.StopWhenNoProgressFor;
            if (double.IsNaN(noProgressFor) || double.IsInfinity(noProgressFor) || noProgressFor < 0)
                throw new ArgumentException($"End condition: 'stop when no progress for' must be a time >= 0 (0 = off), not {noProgressFor}.");
            startTime = clock.GetSimulationTime();

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
                    // a combiner's input by number; a named port (text) is not a combiner input
                    if (to is Combiner)
                    {
                        int port = c.Input ?? -1;
                        portList[(to, port < 0 ? 0 : port)] = c.SourceId;
                    }
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
            if (noProgressFor > 0)
            {
                // a background event: it moves nothing, so it never keeps the Idle check from running
                progressCheck = new BackgroundActionEvent(OnProgressCheck);
                clock.ScheduleEvent(progressCheck, noProgressFor);
            }
        }

        /// <summary>Warm-up: the end condition's count starts with the observed period, as the statistics do (not from time 0).</summary>
        public void ResetStatistics() => endCount = 0;

        /// <summary>True when <paramref name="e"/> has no outputs: a sink, or a buffer / station at the end of the line.</summary>
        public bool IsTerminal(Element e) => !withOutputs.Contains(e);

        /// <summary>The element of id or display name <paramref name="idOrName"/> (null if none).</summary>
        private Element Find(string idOrName)
        {
            if (elements.TryGetValue(idOrName, out var e)) return e;
            foreach (var x in elements.Values) if (x.Name == idOrName) return x;
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
            var waiting = Waiting(out var holding);
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
            // A stopped element does not excuse the stall: every stop's end (a generator's repair, a reaction's "stop for a
            // time", a clock signal) is a pending event, and none is left — a stop still active now never ends (a repair waiting for the operator its paused machine holds was reported neither as a stall nor as a
            // deadlock). The exception is a stop whose end is a pending background event — a shift's next window: that is a
            // night, not a stall (the run must not end at the first shift end with a "deadlock").
            if (run.Stalled || PauseEndsLater()) return;

            run.Stalled = true;
            run.StallTime = now;
            foreach (var e in waiting) run.Waiting.Add(Describe(e));
            foreach (var e in FullAtRest(holding)) run.Waiting.Add(FullText(e));
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

        /// <summary>The last item entry or exit of any element (NaN = none yet): the progress the "no progress" end condition
        /// and the diagnosis read (<see cref="Statistics.ElementStatsCollector.LastEntryTime"/> / <c>LastExitTime</c>).</summary>
        public static double LastMovement(IEnumerable<Element> elements)
        {
            double last = double.NaN;
            foreach (var e in elements)
            {
                if (e is SimuLean.Resources.Resource) continue;
                Movement(e, ref last);
                if (e is IPortedElement ported)
                    foreach (var p in ported.Ports) if (p.Value != null) Movement(p.Value, ref last);
            }
            return last;
        }

        private static void Movement(Element e, ref double last)
        {
            var sc = e.StatsCollector;
            if (sc == null) return;
            if (!double.IsNaN(sc.LastEntryTime) && (double.IsNaN(last) || sc.LastEntryTime > last)) last = sc.LastEntryTime;
            if (!double.IsNaN(sc.LastExitTime) && (double.IsNaN(last) || sc.LastExitTime > last)) last = sc.LastExitTime;
        }

        // The "no progress" end condition: due T after the last item movement. It ends the run when nothing moved
        // for T while items wait (the Idle check cannot run while events such as a periodic signal stay scheduled);
        // after progress, or with no item waiting, it checks again T after the last movement. An element off shift with
        // its next window pending (the Idle rule's exception) is a night, not a lack of progress: checked again later.
        private void OnProgressCheck()
        {
            if (clock.IsStopped) return;
            var run = clock.Context.Run;
            double now = clock.GetSimulationTime();
            double last = LastMovement(elements.Values);
            if (double.IsNaN(last) || last < startTime) last = startTime;
            double due = last + noProgressFor;
            if (due > now + SimTime.ToleranceAt(now)) { clock.ScheduleEvent(progressCheck, due - now); return; }
            var waiting = Waiting(out var holding);
            if (waiting.Count == 0 || PauseEndsLater()) { clock.ScheduleEvent(progressCheck, noProgressFor); return; }

            run.NoProgress = true;
            run.NoProgressTime = now;
            if (run.Waiting.Count == 0)
            {
                foreach (var e in waiting) run.Waiting.Add(Describe(e));
                foreach (var e in FullAtRest(holding)) run.Waiting.Add(FullText(e));
            }
            string t = now.ToString("G6", CultureInfo.InvariantCulture);
            SimLog.Warning($"[SimuLean] No progress at t = {t}: no item has moved for {noProgressFor.ToString("G6", CultureInfo.InvariantCulture)} " +
                           $"(since t = {last.ToString("G6", CultureInfo.InvariantCulture)}) while {waiting.Count} element(s) hold waiting items — " +
                           $"{string.Join("; ", waiting.ConvertAll(Describe))}. The run ends (end condition 'stop when no progress for').");
            End(RunEndReason.NoProgress);
        }

        // the elements holding items that wait (not at rest, starved or surplus), and those holding any item
        private List<Element> Waiting(out List<Element> holding)
        {
            holding = new List<Element>();
            foreach (var e in elements.Values)
            {
                if (e is Sink) continue;
                if (Held(e) > 0) holding.Add(e);
            }
            return holding.FindAll(e => !AtRest(e) && !Starved(e) && !Surplus(e, new HashSet<Element>()));
        }

        // The buffers at rest (no outputs) that are full: not waiting themselves, but what holds up the element feeding
        // them — a stall report that left them out named the blocked feeder only.
        private List<Element> FullAtRest(List<Element> holding) => holding.FindAll(e => AtRest(e) && e.GetFreeCapacity() <= 0);

        // "TotesBack: full (100/100), no outputs"
        private static string FullText(Element e)
        {
            int held = Held(e);
            return $"{e.Name}: full ({held}/{held + Math.Max(0, e.GetFreeCapacity())}), no outputs";
        }

        // An element is under a stop that a pending background event will end (off shift until the next window).
        private bool PauseEndsLater()
        {
            foreach (var g in downtimes) if (g.StopEndPending && g.Target.IsDown) return true;
            return false;
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

        // Starved: a Combiner holding components only, whose main input's supply is exhausted — the element feeding
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

        /// <summary>The elements downstream of <paramref name="e"/> that hold its items up: <paramref name="e"/> holds items
        /// ready to leave (BLOCKED, or a buffer with outputs) and each of these holds items with no room left. The "waits
        /// for" edges of a circular wait (<see cref="CircularWaits"/>).</summary>
        public List<Element> BlockedBy(Element e)
        {
            var list = new List<Element>();
            if (e == null || Held(e) == 0 || !(e.CurrentState == ElementState.BLOCKED || (IsBuffer(e) && !IsTerminal(e)))) return list;
            if (downstream.TryGetValue(e, out var next))
                foreach (var d in next)
                    if (d != e && Held(d) > 0 && d.GetFreeCapacity() <= 0) list.Add(d);
            return list;
        }

        /// <summary>
        /// The circular waits of the model now: groups of elements each holding items and waiting for room in the next,
        /// which is full and waits in turn (<see cref="BlockedBy"/>) — none can move (a loop with too little room, e.g. a
        /// rework loop or a closed loop of carriers). One cycle per group (a strongly connected set of the "waits for"
        /// graph), as names in order with the first repeated at the end: <c>[Buffer, Lathe, Check Buffer, Check, Buffer]</c>.
        /// Read-only.
        /// </summary>
        public List<List<string>> CircularWaits()
        {
            var order = new List<Element>();
            var edges = new Dictionary<Element, List<Element>>();
            foreach (var e in elements.Values)
            {
                if (e == null || edges.ContainsKey(e)) continue;
                order.Add(e);
                edges[e] = BlockedBy(e);
            }
            // Tarjan's strongly connected components, depth first without recursion (a long loop would overflow it)
            var index = new Dictionary<Element, int>();
            var low = new Dictionary<Element, int>();
            var onStack = new HashSet<Element>();
            var stack = new Stack<Element>();
            var groups = new List<HashSet<Element>>();
            int counter = 0;
            foreach (var root in order)
            {
                if (index.ContainsKey(root) || edges[root].Count == 0) continue;
                var work = new Stack<KeyValuePair<Element, int>>();
                work.Push(new KeyValuePair<Element, int>(root, 0));
                index[root] = low[root] = counter++; stack.Push(root); onStack.Add(root);
                while (work.Count > 0)
                {
                    var top = work.Pop();
                    Element v = top.Key;
                    int i = top.Value;
                    var outs = edges.TryGetValue(v, out var o) ? o : new List<Element>();
                    if (i < outs.Count)
                    {
                        work.Push(new KeyValuePair<Element, int>(v, i + 1));
                        var w = outs[i];
                        if (!index.ContainsKey(w))
                        {
                            index[w] = low[w] = counter++; stack.Push(w); onStack.Add(w);
                            work.Push(new KeyValuePair<Element, int>(w, 0));
                        }
                        else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                        continue;
                    }
                    if (work.Count > 0) { var parent = work.Peek().Key; low[parent] = Math.Min(low[parent], low[v]); }
                    if (low[v] != index[v]) continue;
                    var group = new HashSet<Element>();
                    Element x;
                    do { x = stack.Pop(); onStack.Remove(x); group.Add(x); } while (x != v);
                    if (group.Count > 1) groups.Add(group);
                }
            }
            var cycles = new List<List<string>>();
            foreach (var group in groups)
            {
                // the shortest way back to the group's first element (model order), inside the group
                var start = order.Find(group.Contains);
                var from = new Dictionary<Element, Element> { [start] = null };
                var queue = new Queue<Element>();
                queue.Enqueue(start);
                Element last = null;
                while (queue.Count > 0 && last == null)
                {
                    var v = queue.Dequeue();
                    foreach (var w in edges[v])
                    {
                        if (!group.Contains(w)) continue;
                        if (w == start) { last = v; break; }
                        if (from.ContainsKey(w)) continue;
                        from[w] = v;
                        queue.Enqueue(w);
                    }
                }
                if (last == null) continue;
                var path = new List<string>();
                for (var v = last; v != null; v = from[v]) path.Add(v.Name);
                path.Reverse();
                path.Add(start.Name);
                cycles.Add(path);
            }
            return cycles;
        }

        /// <summary>What <paramref name="e"/> holds in words: "3 item(s)"; an unpacker its carriers apart from their pieces
        /// ("2 carrier(s) (capacity 2), 3 piece(s)": its content counts both).</summary>
        public static string HeldText(Element e)
        {
            if (e is Separator s && s.Mode == SeparatorMode.Unpack)
                return $"{s.PlacesUsed} carrier(s) (capacity {s.Capacity}), {Math.Max(0, Held(e) - s.ItemsHeldCount)} piece(s)";
            return $"{Held(e)} item(s)";
        }

        // "Yard: BLOCKED, 2 item(s) → BA_Bay1, BA_Bay2" (what it waits for, from its state). Upstream for a Combiner gathering:
        // holding components only, its main input; holding its main item, the inputs still missing components — the
        // elements wired to them or the lists they pull from. An element pushing to a list waits for its pullers.
        private string Describe(Element e)
        {
            string state = e.CurrentState.Name ?? "?";
            string text = $"{e.Name}: {state}, {HeldText(e)}";
            if (e is Combiner c)
            {
                if (HoldsComponentsOnly(c)) return text + " → waits for its main input" + Source(c, 0, " ");
                if (e.CurrentState != ElementState.BLOCKED)
                {
                    var missing = MissingSources(c);
                    if (missing.Count > 0) return text + " → waits for components from " + string.Join(", ", missing);
                }
            }
            // a routing expression that names no output holds its items for ever: that, not the outputs, is the cause
            if (e.GetOutputStrategy() is ExpressionRoutingStrategy routing && routing.Unrouted != null)
                return text + " → " + routing.Unrouted;
            // the real wait only (a station PROCESSING, or waiting for an operator, was said to wait for
            // the element after it): its pending resource requests; room downstream when it is BLOCKED or a buffer whose
            // items are ready to leave; nothing otherwise (its items are being worked on)
            var resource = ResourceWaits(e);
            if (resource.Count > 0) text += " → waits for " + string.Join(", ", resource);
            else if (e.CurrentState == ElementState.WAITING_FOR_RESOURCE) text += " → waits for a resource";
            else if (e.CurrentState == ElementState.BLOCKED || IsBuffer(e))
            {
                var waitsFor = new List<string>();
                if (downstream.TryGetValue(e, out var next)) waitsFor.AddRange(next.ConvertAll(x => x.Name));
                if (pushesTo.TryGetValue(e, out var lists))
                    foreach (string l in lists)
                        waitsFor.Add($"list {ListName(l)}" + (pullers.TryGetValue(l, out var ps) && ps.Count > 0
                            ? " (pulled by " + string.Join(", ", ps.ConvertAll(p => p.Name)) + ")" : " (no puller)"));
                // BLOCKED says why (a queue holding items no receiver takes read as an error; it is the queue's
                // documented state): no receiver has room for its items now
                if (waitsFor.Count > 0)
                    text += (e.CurrentState == ElementState.BLOCKED ? " → no receiver has room (full, refusing or stopped): waits for " : " → waits for ")
                            + string.Join(", ", waitsFor);
            }
            // items that timed out wait for the timeout target, which is wired outside the connections
            int timedOut = e is ItemsQueue q ? q.Reneging : e is MultiServer ms ? ms.ReroutingCount : 0;
            if (timedOut > 0 && e.TimeoutTarget != null) text += $" → timeout target {e.TimeoutTarget.Name} ({timedOut} item(s))";
            return text;
        }

        // "team Operators (1 for Process, waiting 12.5)": each pending request of the element, by team
        private List<string> ResourceWaits(Element e)
        {
            var list = new List<string>();
            double now = clock.GetSimulationTime();
            foreach (var team in teams)
                foreach (var entry in team.Requests.Entries)
                {
                    if (!(entry.Value is SimuLean.Resources.ResourceRequest r) || r.Requester != e
                        || r.State != SimuLean.Resources.RequestState.Pending) continue;
                    int count = 0;
                    var labels = new List<string>();
                    foreach (var q in r.Requirements)
                    {
                        count += q.Count;
                        if (!string.IsNullOrEmpty(q.Label) && !labels.Contains(q.Label)) labels.Add(q.Label);
                    }
                    string what = $"{count}{(labels.Count > 0 ? " " + string.Join("/", labels) : "")} for {r.TaskType}";
                    if (!double.IsNaN(r.RequestTime)) what += $", waiting {(now - r.RequestTime).ToString("G6", CultureInfo.InvariantCulture)}";
                    list.Add($"team {team.Name} ({what})");
                }
            return list;
        }

        private string ListName(string id) => listNames.TryGetValue(id, out string n) ? n : id;

        // where connection index k of a Combiner gets its items: the element wired to it, or the list it pulls from
        private string Source(Combiner c, int connectionIndex, string prefix = "")
        {
            var origin = c.InputOrigin(connectionIndex);
            if (origin != null) return prefix + origin.Name;
            return portList.TryGetValue((c, connectionIndex), out string l) ? prefix + "list " + ListName(l) : "";
        }

        // the sources of the component inputs that some gathering assembly still lacks
        private List<string> MissingSources(Combiner c)
        {
            var list = new List<string>();
            if (c.Assemblies == null) return list;
            foreach (var a in c.Assemblies)
            {
                // only an assembly still gathering lacks components: one processing has attached them (its parts are
                // empty), one finished waits for room
                if (a.Main == null || !a.Gathering || a.Requirements == null) continue;
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
