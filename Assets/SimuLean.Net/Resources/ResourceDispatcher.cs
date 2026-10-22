using System;
using System.Collections.Generic;
using SimuLean.Collections;

namespace SimuLean.Resources
{
    /// <summary>
    /// The element that hands resources out ("elemento ordenador"): its members — any
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
        private bool dispatching, again, anyZeroRequest;

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

        /// <summary>Order of the free resources for a request (null = FIFO = longest idle first).
        /// E.g. <c>OrderBy("utilization")</c> for the least used one.</summary>
        public ListQuery ResourceOrder { get; set; }

        public IReadOnlyList<TaskExecutor> Members => members;

        /// <summary>Requests served so far.</summary>
        public int Served { get; private set; }

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
            Served = 0;
            foreach (var m in members) if (m.IsAvailable) List(m);
        }

        /// <summary>Warm-up: forgets the requests served so far (its two lists are reset with the model lists).</summary>
        public void ResetStatistics() => Served = 0;

        /// <summary>Queues <paramref name="request"/> and serves whatever can be served now (possibly this one).</summary>
        public ResourceRequest Request(ResourceRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.State != RequestState.Pending || request.Entry != null)
                throw new InvalidOperationException($"{request} was already made.");
            request.RequestTime = Clock.GetSimulationTime();
            foreach (var rq in request.Requirements) if (rq.Count <= 0) anyZeroRequest = true;
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
                    if (Resources.Count == 0 && !anyZeroRequest) break;   // no free member: nothing can be served (review2 #17)
                    // the first request in order, found without sorting them all: usually it is the one served
                    if (Requests.TryPeek(RequestOrder, out var first) && TryServe(first)) { again = true; continue; }
                    // else the others in order (the first already tried)
                    foreach (var reqEntry in Requests.Ordered(RequestOrder))
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
            var chosen = Choose(request);
            if (chosen == null) return false;
            Serve(request, reqEntry, chosen);
            return true;
        }

        /// <summary>Serves what can be served now — e.g. after a requester's stop ended (a stopped station's process
        /// requests wait).</summary>
        public void Retry() => Dispatch();

        // A process request of a stopped element (any active stop: off shift, failure, an AfterCurrent stop in effect)
        // waits; transport requests do not (the item may still be carried away).
        private static bool WaitsForRequester(ResourceRequest request)
            => request.TaskType == TaskType.Process && request.Requester != null && request.Requester.IsDown;

        // All requirements at once, or null.
        private List<TaskExecutor> Choose(ResourceRequest request)
        {
            var chosen = new List<TaskExecutor>();
            foreach (var m in members) m.MatchLocation = request.Location;   // "distance" = to this request
            foreach (var req in request.Requirements)
            {
                ListQuery q = (req.Query ?? ListQuery.All)
                    .Where(e => e.Value is TaskExecutor r && r.IsAvailable && !chosen.Contains(r))
                    .Then(ResourceOrder);
                // the best free member, then the next best (the chosen ones are filtered out): the first ones of the
                // stable order without sorting every free member for every request (review2 #17)
                for (int need = req.Count; need > 0; need--)
                {
                    if (!Resources.TryPeek(q, out var e)) return null;
                    chosen.Add((TaskExecutor)e.Value);
                }
            }
            return chosen;
        }

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
