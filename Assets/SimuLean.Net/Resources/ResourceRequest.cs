using System;
using System.Collections.Generic;
using SimuLean.Collections;

namespace SimuLean.Resources
{
    /// <summary>
    /// One kind of resource a request needs: <see cref="Count"/> resources matching <see cref="Query"/> (a filter
    /// over the dispatcher's resource list — e.g. <c>skill == welding</c>; null = any). Several requirements in one
    /// request cover "an operator and a tool".
    /// <para><see cref="Team"/>: the team this requirement takes its members from —
    /// null = the use's team (<see cref="StationResources.Dispatcher"/>). A station's use may so need a crane of one team
    /// and a driver of another; <see cref="StationResources.Order"/> says whether they are taken all at once or in order.</para>
    /// </summary>
    public sealed class ResourceRequirement
    {
        public ResourceRequirement(int count = 1, ListQuery query = null, string label = null, ResourceDispatcher team = null)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "A requirement needs at least one resource.");
            Count = count; Query = query; Label = label; Team = team;
        }

        public int Count { get; }
        public ListQuery Query { get; }

        /// <summary>The team its members come from; null = the use's team.</summary>
        public ResourceDispatcher Team { get; }

        /// <summary>Optional description (e.g. the required skill) shown in lists and reports.</summary>
        public string Label { get; }
    }

    public enum RequestState { Pending, Assigned, Cancelled }

    /// <summary>
    /// A request for resources made to a <see cref="ResourceDispatcher"/> by an element (a station, a transport
    /// request). It waits in the dispatcher's request list until <b>all</b> its requirements can be met at once;
    /// then <see cref="OnAssigned"/> receives the resources and must give each a task
    /// (<see cref="TaskExecutor.Execute"/>) — resources left without a task go back to the dispatcher. The
    /// resources are released when their tasks end (the task type decides where: transport at the unload,
    /// setup / process at their end, or after a <c>Hold</c>).
    /// <para>List fields (for request-order rules): <c>priority</c>, <c>requestTime</c>, <c>count</c> (resources
    /// needed), text <c>station</c> (requester name), <c>taskType</c>, <c>skill</c> (the first requirement's
    /// label), <c>oldestItemTime</c> (the time of the oldest item the request works for, <see cref="OldestItemTime"/>);
    /// plus <c>age</c> from the list, and user fields of the list.</para>
    /// </summary>
    public sealed class ResourceRequest : IListFieldSource
    {
        private readonly List<ResourceRequirement> requirements = new List<ResourceRequirement>();

        public ResourceRequest(Element requester, TaskType taskType, Action<ResourceRequest, IReadOnlyList<TaskExecutor>> onAssigned,
                               params ResourceRequirement[] requirements)
        {
            Requester = requester;
            TaskType = taskType;
            OnAssigned = onAssigned ?? throw new ArgumentNullException(nameof(onAssigned));
            if (requirements == null || requirements.Length == 0) this.requirements.Add(new ResourceRequirement());
            else this.requirements.AddRange(requirements);
        }

        // The requirements copied once from a station's list (not copied to an array first).
        internal ResourceRequest(Element requester, TaskType taskType, Action<ResourceRequest, IReadOnlyList<TaskExecutor>> onAssigned,
                                 IReadOnlyList<ResourceRequirement> requirements)
        {
            Requester = requester;
            TaskType = taskType;
            OnAssigned = onAssigned ?? throw new ArgumentNullException(nameof(onAssigned));
            if (requirements == null || requirements.Count == 0) this.requirements.Add(new ResourceRequirement());
            else this.requirements.AddRange(requirements);
        }

        public Element Requester { get; }
        public TaskType TaskType { get; }
        public IReadOnlyList<ResourceRequirement> Requirements => requirements;
        public Action<ResourceRequest, IReadOnlyList<TaskExecutor>> OnAssigned { get; }

        /// <summary>Higher first when the request order ranks by priority (default 1). With a preemption mode it also
        /// decides which tasks it may take a member from: those of strictly lower priority.</summary>
        public double Priority { get; set; } = 1;

        /// <summary>What it does when no member that meets its requirements is free (default
        /// <see cref="PreemptMode.None"/>: wait).</summary>
        public PreemptMode Preempt { get; set; }

        /// <summary>Where the work starts (e.g. the pick-up location); the resources' <c>distance</c> field is
        /// measured to it. Null = nowhere in particular.</summary>
        public string Location { get; set; }

        /// <summary>The time of the oldest item this request works for, set by the requester before it asks (e.g. a
        /// sequencing station: the call-off time of its lot's oldest order). NaN (default) = the <see cref="RequestTime"/>.
        /// List field <c>oldestItemTime</c>: a request order <c>|oldestItemTime</c> serves the work whose oldest item has
        /// waited longest first.</summary>
        public double OldestItemTime { get; set; } = double.NaN;

        /// <summary>Served while its requester is stopped (a repair crew comes because the element is down). Other
        /// process requests of a stopped element wait for its resume.</summary>
        public bool ServeWhileDown { get; set; }

        public RequestState State { get; internal set; } = RequestState.Pending;
        public double RequestTime { get; internal set; } = double.NaN;
        public double AssignTime { get; internal set; } = double.NaN;

        /// <summary>The resources it got (empty until assigned).</summary>
        public IReadOnlyList<TaskExecutor> Assigned { get; internal set; } = Array.Empty<TaskExecutor>();

        internal ListEntry Entry;

        /// <summary>The dispatcher it was made to (set by <see cref="ResourceDispatcher.Request"/>).</summary>
        public ResourceDispatcher Dispatcher { get; internal set; }

        /// <summary>One part of a request over several teams served all at once: the dispatcher serves it only when
        /// every part can be served now.</summary>
        internal JointRequest Joint;

        public int TotalCount
        {
            get { int n = 0; foreach (var r in requirements) n += r.Count; return n; }
        }

        public double GetListNumber(string field)
        {
            if (field != null && field.StartsWith("origin.", StringComparison.Ordinal))
            {
                var v = SimuLean.Expressions.EntityScope.OfElement(Requester, field.Substring(7));
                return v.Missing || v.IsText ? double.NaN : v.Number;
            }
            switch (field)
            {
                case "priority": return Priority;
                case "requestTime": return RequestTime;
                case "count": return TotalCount;
                case "oldestItemTime": return double.IsNaN(OldestItemTime) ? RequestTime : OldestItemTime;
            }
            return double.NaN;
        }

        public string GetListText(string field)
        {
            // origin.x: the requesting element (origin.name)
            if (field != null && field.StartsWith("origin.", StringComparison.Ordinal))
            {
                var v = SimuLean.Expressions.EntityScope.OfElement(Requester, field.Substring(7));
                return v.Missing ? null : v.ToString();
            }
            switch (field)
            {
                case "location": return Location;
                case "taskType": return TaskType.ToString();
                case "skill": return requirements.Count > 0 ? requirements[0].Label : null;
            }
            return null;
        }

        public override string ToString() => $"{TaskType} request from {Requester?.Name ?? "?"} ({TotalCount} resources, {State})";
    }

    /// <summary>
    /// A request whose requirements come from several teams, served <b>all at once</b>: one part per team,
    /// each pending in its own dispatcher's list (so each team's request waits count it). A dispatcher that could serve its
    /// part asks the joint request, which plans every part (free members, or members taken from lower work when the parts
    /// have a preemption mode) and serves them all only when every part can be served now — all or nothing across teams.
    /// </summary>
    internal sealed class JointRequest
    {
        internal readonly List<ResourceRequest> Parts = new List<ResourceRequest>();
        /// <summary>True while the parts are being posted: none is served before all are pending.</summary>
        internal bool Posting;

        private readonly List<List<TaskExecutor>> chosen = new List<List<TaskExecutor>>();
        private readonly List<List<TaskExecutor>> victims = new List<List<TaskExecutor>>();

        internal bool TryServe()
        {
            if (Posting) return false;
            foreach (var p in Parts) if (p.State != RequestState.Pending || p.Entry == null) return false;
            chosen.Clear(); victims.Clear();
            foreach (var p in Parts)
            {
                if (!p.Dispatcher.Plan(p, out var c, out var v)) return false;
                chosen.Add(c); victims.Add(v);
            }
            // serve every part; the dispatchers do not dispatch meanwhile (a victim asking again is queued)
            var held = new List<ResourceDispatcher>();
            foreach (var p in Parts) if (p.Dispatcher.Suspend()) held.Add(p.Dispatcher);
            try
            {
                for (int i = 0; i < Parts.Count; i++) Parts[i].Dispatcher.Commit(Parts[i], chosen[i], victims[i]);
            }
            finally { foreach (var d in held) d.ResumeDispatch(); }
            return true;
        }
    }
}
