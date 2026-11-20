using System;
using System.Collections.Generic;
using SimuLean.Collections;

namespace SimuLean.Resources
{
    /// <summary>
    /// The element that hands resources out: its members — any
    /// <see cref="TaskExecutor"/>s, operators, tools, vehicles — wait in a <see cref="Resources"/> list while free,
    /// and <see cref="ResourceRequest"/>s wait in a <see cref="Requests"/> list. Both are ordinary
    /// <see cref="SimList"/>s, so the priority rule is a pair of list orders (and user fields / formulas):
    /// <see cref="RequestOrder"/> decides which request is served first, <see cref="ResourceOrder"/> which free
    /// resource it gets. <b>No rule = FIFO if available</b>: requests in arrival order, resources in the order they
    /// became free (longest idle first).
    /// <para>Dispatching runs whenever a request arrives or a member becomes free: requests are tried in order;
    /// one is served only if <b>all</b> its requirements can be met at once (all-or-nothing: no partial holds, no
    /// deadlock); a request that cannot be served yet does not block the ones behind it. The two lists keep their
    /// statistics (request waiting time = time waiting for resources).</para>
    /// </summary>
    public sealed class ResourceDispatcher : IWarmupStatistics
    {
        private readonly List<TaskExecutor> members = new List<TaskExecutor>();
        private readonly Dictionary<TaskExecutor, ListEntry> listed = new Dictionary<TaskExecutor, ListEntry>();
        private bool dispatching, again, anyZeroRequest, anyPreemptive;

        public ResourceDispatcher(string name, SimClock clock)
        {
            Name = name;
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Resources = new SimList(name + ".resources", clock);
            Requests = new SimList(name + ".requests", clock);
        }

        public string Name { get; }
        public SimClock Clock { get; }

        /// <summary>Free members (entries: the resources; fields <c>idleTime</c>, <c>utilization</c>, labels…).</summary>
        public SimList Resources { get; }

        /// <summary>Pending requests (entries: the requests; fields <c>priority</c>, <c>station</c>, <c>age</c>…).</summary>
        public SimList Requests { get; }

        /// <summary>Order of the requests (null = FIFO). E.g. <c>ListQuery.All.OrderBy("priority", true)</c>.</summary>
        public ListQuery RequestOrder { get; set; }

        /// <summary>A dispatch rule ordering the requests instead of <see cref="RequestOrder"/> (a request order
        /// <c>rule:Type</c>; null = none).</summary>
        public SimuLean.Rules.RuleRequestOrder RequestRule { get; set; }

        /// <summary>Order of the free resources for a request (null = FIFO = longest idle first).
        /// E.g. <c>OrderBy("utilization")</c> for the least used one.</summary>
        public ListQuery ResourceOrder { get; set; }

        public IReadOnlyList<TaskExecutor> Members => members;

        /// <summary>Requests served so far.</summary>
        public int Served { get; private set; }

        /// <summary>Some use of the team has a preemption mode (set when it is built): its members and stations report
        /// their preemption statistics.</summary>
        public bool Preemptive { get; set; }

        /// <summary>Requests served by taking members from tasks of lower priority.</summary>
        public int PreemptiveServes { get; private set; }

        // The stations / elements using it (their preemption counters start and reset with it).
        private readonly List<StationResources> uses = new List<StationResources>();
        internal void AddUse(StationResources use) { if (!uses.Contains(use)) uses.Add(use); }

        public void Add(TaskExecutor resource)
        {
            if (resource == null || members.Contains(resource)) return;
            members.Add(resource);
            resource.BecameAvailable += OnBecameAvailable;
        }

        /// <summary>Empties both lists and lists the free members (call after the members' own Start).</summary>
        public void Start()
        {
            Resources.Reset();
            Requests.Reset();
            listed.Clear();
            Served = PreemptiveServes = 0;
            foreach (var u in uses) u.ResetStatistics(Clock.GetSimulationTime(), true);
            foreach (var m in members) if (m.IsAvailable) List(m);
        }

        /// <summary>Warm-up: forgets the requests served so far (its two lists are reset with the model lists).</summary>
        public void ResetStatistics()
        {
            Served = PreemptiveServes = 0;
            foreach (var u in uses) u.ResetStatistics(Clock.GetSimulationTime(), false);
        }

        /// <summary>Queues <paramref name="request"/> and serves whatever can be served now (possibly this one).</summary>
        public ResourceRequest Request(ResourceRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.State != RequestState.Pending || request.Entry != null)
                throw new InvalidOperationException($"{request} was already made.");
            request.RequestTime = Clock.GetSimulationTime();
            request.Dispatcher = this;
            var requirements = request.Requirements;   // by index: a foreach over the interface boxes its enumerator
            for (int i = 0; i < requirements.Count; i++) if (requirements[i].Count <= 0) anyZeroRequest = true;
            if (request.Preempt != PreemptMode.None) anyPreemptive = Preemptive = true;
            request.Entry = Requests.PushValue(request);
            Dispatch();
            return request;
        }

        /// <summary>Withdraws a pending request. False if it was already served or cancelled.</summary>
        public bool Cancel(ResourceRequest request)
        {
            if (request == null || request.State != RequestState.Pending || request.Entry == null) return false;
            Requests.Remove(request.Entry);
            request.Entry = null;
            request.State = RequestState.Cancelled;
            return true;
        }

        private void OnBecameAvailable(Resource r)
        {
            if (r is TaskExecutor te && !listed.ContainsKey(te)) List(te);
            Dispatch();
        }

        private void List(TaskExecutor r) => listed[r] = Resources.PushValue(r);

        // ── dispatching ──────────────────────────────────────────────────────

        private void Dispatch()
        {
            if (dispatching) { again = true; return; }   // re-entered from a task callback: loop again below
            dispatching = true;
            try
            {
                do
                {
                    again = false;
                    // no free member: nothing can be served — unless a request may take one from lower work
                    if (Resources.Count == 0 && !anyZeroRequest && !anyPreemptive) break;
                    // the first request in order, found without sorting them all: usually it is the one served
                    List<ListEntry> ruled = RequestRule?.Ordered(Requests, Clock);
                    ListEntry first = null;
                    if (ruled != null ? ruled.Count > 0 && (first = ruled[0]) != null && TryServe(first)
                                      : Requests.TryPeek(RequestOrder, out first) && TryServe(first)) { again = true; continue; }
                    // else the others in order (the first already tried)
                    foreach (var reqEntry in ruled ?? Requests.Ordered(RequestOrder))
                    {
                        if (ReferenceEquals(reqEntry, first) || !TryServe(reqEntry)) continue;   // cannot be served yet
                        again = true;                   // lists changed: start over in order
                        break;
                    }
                } while (again);
            }
            finally { dispatching = false; }
        }

        // Serves one request now if its requester can start and every requirement has free members. A stopped station's
        // job cannot start: it does not take the resources away from the others meanwhile — it is served
        // when the station runs again (Retry on its Resumed).
        private bool TryServe(ListEntry reqEntry)
        {
            var request = (ResourceRequest)reqEntry.Value;
            if (WaitsForRequester(request)) return false;
            if (request.Joint != null) return request.Joint.TryServe();   // a part of a request over several teams
            var chosen = Choose(request);
            if (chosen == null) return request.Preempt != PreemptMode.None && TryPreempt(request, reqEntry);
            Serve(request, reqEntry, chosen);
            return true;
        }

        // ── joint requests over several teams ──────────────────────────

        // What this dispatcher would give the part now — free members, else (with a preemption mode) members taken from
        // lower work — without acting; false when it cannot be served now.
        internal bool Plan(ResourceRequest request, out List<TaskExecutor> chosen, out List<TaskExecutor> victims)
        {
            victims = null;
            chosen = null;
            if (WaitsForRequester(request)) return false;
            chosen = Choose(request);
            if (chosen != null) return true;
            if (request.Preempt == PreemptMode.None) return false;
            return PlanPreemption(request, out chosen, out victims);
        }

        // Serves a planned part: its victims give their work back, then it is served.
        internal void Commit(ResourceRequest request, List<TaskExecutor> chosen, List<TaskExecutor> victims)
        {
            if (victims != null)
            {
                PreemptiveServes++;
                foreach (var v in victims) v.Preempt(request.Preempt);
            }
            Serve(request, request.Entry, chosen);
        }

        // Holds dispatching while a joint request serves its parts (true = it was not dispatching: resume it after).
        internal bool Suspend()
        {
            if (dispatching) return false;
            dispatching = true;
            again = false;
            return true;
        }

        internal void ResumeDispatch()
        {
            dispatching = false;
            Dispatch();   // what was queued meanwhile (a victim's work asking again, a member freed)
        }

        // ── preemption ──────────────────────────────────────────────────

        // Probe entries of the members (not listed while busy), so a requirement's filter reads a busy member's fields.
        private readonly Dictionary<TaskExecutor, ListEntry> probes = new Dictionary<TaskExecutor, ListEntry>();

        // No free member meets the request: free ones first, then members taken from tasks of strictly lower priority —
        // the lowest priority first, ties the one that started its task last. All or nothing: only when the whole request
        // can then be served. Runs only for a request with a preemption mode, over this team's members.
        private bool TryPreempt(ResourceRequest request, ListEntry reqEntry)
        {
            if (!PlanPreemption(request, out var chosen, out var victims)) return false;
            PreemptiveServes++;
            foreach (var v in victims) v.Preempt(request.Preempt);   // their work asks again (dispatching re-entered: queued)
            Serve(request, reqEntry, chosen);
            return true;
        }

        // The members a preempting request would get (free ones first, then victims), without acting.
        private bool PlanPreemption(ResourceRequest request, out List<TaskExecutor> chosen, out List<TaskExecutor> victims)
        {
            chosen = new List<TaskExecutor>();
            choosing = chosen;
            victims = null;
            foreach (var m in members) m.MatchLocation = request.Location;
            var requirements = request.Requirements;
            for (int i = 0; i < requirements.Count; i++)
            {
                var req = requirements[i];
                ListQuery q = ChooseQuery(req.Query);
                var filter = req.Query?.Filter(Resources);
                for (int need = req.Count; need > 0; need--)
                {
                    if (Resources.TryPeek(q, out var e)) { chosen.Add((TaskExecutor)e.Value); continue; }
                    var victim = Victim(request, filter, chosen);
                    if (victim == null) return false;
                    chosen.Add(victim);
                    (victims ?? (victims = new List<TaskExecutor>())).Add(victim);
                }
            }
            return victims != null;   // (none: Choose would have served it)
        }

        private TaskExecutor Victim(ResourceRequest request, Func<ListEntry, bool> filter, List<TaskExecutor> chosen)
        {
            TaskExecutor best = null;
            foreach (var m in members)
            {
                if (chosen.Contains(m) || !m.CanBePreemptedFor(request.Priority, request.Preempt)) continue;
                if (filter != null && !filter(ProbeOf(m))) continue;
                if (best != null)
                {
                    double p = m.CurrentTask.Priority, bp = best.CurrentTask.Priority;
                    if (p > bp || (p == bp && !(m.CurrentTask.StartTime > best.CurrentTask.StartTime))) continue;
                }
                best = m;
            }
            return best;
        }

        private ListEntry ProbeOf(TaskExecutor m)
        {
            if (!probes.TryGetValue(m, out var e)) probes[m] = e = new ListEntry { Value = m };
            e.PushTime = m.IdleSince;
            return e;
        }

        /// <summary>Serves what can be served now — e.g. after a requester's stop ended (a stopped station's process
        /// requests wait).</summary>
        public void Retry() => Dispatch();

        // A process (or setup) request of a stopped element (any active stop: off shift, failure, an AfterCurrent stop in
        // effect) waits; transport requests do not (the item may still be carried away), nor a repair crew's.
        private static bool WaitsForRequester(ResourceRequest request)
            => (request.TaskType == TaskType.Process || request.TaskType == TaskType.Setup)
               && !request.ServeWhileDown && request.Requester != null && request.Requester.IsDown;

        // All requirements at once, or null.
        private List<TaskExecutor> Choose(ResourceRequest request)
        {
            var chosen = new List<TaskExecutor>();
            choosing = chosen;
            foreach (var m in members) m.MatchLocation = request.Location;   // "distance" = to this request
            var requirements = request.Requirements;   // by index: a foreach over the interface boxes its enumerator
            for (int i = 0; i < requirements.Count; i++)
            {
                var req = requirements[i];
                ListQuery q = ChooseQuery(req.Query);
                // the best free member, then the next best (the chosen ones are filtered out): the first ones of the
                // stable order without sorting every free member for every request
                for (int need = req.Count; need > 0; need--)
                {
                    if (!Resources.TryPeek(q, out var e)) return null;
                    chosen.Add((TaskExecutor)e.Value);
                }
            }
            return chosen;
        }

        // The query of a requirement for Choose — its filter, then a free member not chosen yet for this request (read
        // from `choosing`, the request being chosen), then the resource order: built once per requirement query, not at
        // every dispatch; built again if ResourceOrder changes.
        private readonly Dictionary<ListQuery, ListQuery> chooseQueries = new Dictionary<ListQuery, ListQuery>();
        private ListQuery chooseAny, chooseOrder;
        private List<TaskExecutor> choosing;

        private ListQuery ChooseQuery(ListQuery query)
        {
            if (!ReferenceEquals(chooseOrder, ResourceOrder)) { chooseQueries.Clear(); chooseAny = null; chooseOrder = ResourceOrder; }
            if (query == null) return chooseAny ?? (chooseAny = ChooseQueryOf(ListQuery.All));
            if (!chooseQueries.TryGetValue(query, out var q)) chooseQueries[query] = q = ChooseQueryOf(query);
            return q;
        }

        private ListQuery ChooseQueryOf(ListQuery query)
            => query.Where(e => e.Value is TaskExecutor r && r.IsAvailable && !choosing.Contains(r)).Then(ResourceOrder);

        private void Serve(ResourceRequest request, ListEntry reqEntry, List<TaskExecutor> chosen)
        {
            Requests.Take(reqEntry);
            request.Entry = null;
            foreach (var r in chosen)
                if (listed.TryGetValue(r, out var e)) { Resources.Take(e); listed.Remove(r); }
            request.State = RequestState.Assigned;
            request.AssignTime = Clock.GetSimulationTime();
            request.Assigned = chosen;
            Served++;
            request.OnAssigned(request, chosen);
            // A resource the requester gave no task goes back to the free list.
            foreach (var r in chosen)
                if (r.IsAvailable && !listed.ContainsKey(r)) List(r);
        }
    }
}
