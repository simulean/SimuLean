using System;
using System.Collections.Generic;
using SimuLean.Collections;

namespace SimuLean.Resources
{
    /// <summary>
    /// One kind of resource a request needs: <see cref="Count"/> resources matching <see cref="Query"/> (a filter
    /// over the dispatcher's resource list — e.g. <c>skill == welding</c>; null = any). Several requirements in one
    /// request cover "an operator and a tool".
    /// </summary>
    public sealed class ResourceRequirement
    {
        public ResourceRequirement(int count = 1, ListQuery query = null, string label = null)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "A requirement needs at least one resource.");
            Count = count; Query = query; Label = label;
        }

        public int Count { get; }
        public ListQuery Query { get; }

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
    /// label); plus <c>age</c> from the list, and user fields of the list.</para>
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

        public Element Requester { get; }
        public TaskType TaskType { get; }
        public IReadOnlyList<ResourceRequirement> Requirements => requirements;
        public Action<ResourceRequest, IReadOnlyList<TaskExecutor>> OnAssigned { get; }

        /// <summary>Higher first when the request order ranks by priority.</summary>
        public double Priority { get; set; }

        /// <summary>Where the work starts (e.g. the pick-up location); the resources' <c>distance</c> field is
        /// measured to it. Null = nowhere in particular.</summary>
        public string Location { get; set; }

        public RequestState State { get; internal set; } = RequestState.Pending;
        public double RequestTime { get; internal set; } = double.NaN;
        public double AssignTime { get; internal set; } = double.NaN;

        /// <summary>The resources it got (empty until assigned).</summary>
        public IReadOnlyList<TaskExecutor> Assigned { get; internal set; } = Array.Empty<TaskExecutor>();

        internal ListEntry Entry;

        public int TotalCount
        {
            get { int n = 0; foreach (var r in requirements) n += r.Count; return n; }
        }

        public double GetListNumber(string field)
        {
            switch (field)
            {
                case "priority": return Priority;
                case "requestTime": return RequestTime;
                case "count": return TotalCount;
            }
            return double.NaN;
        }

        public string GetListText(string field)
        {
            switch (field)
            {
                case "station": return Requester?.GetName();
                case "location": return Location;
                case "taskType": return TaskType.ToString();
                case "skill": return requirements.Count > 0 ? requirements[0].Label : null;
            }
            return null;
        }

        public override string ToString() => $"{TaskType} request from {Requester?.GetName() ?? "?"} ({TotalCount} resources, {State})";
    }
}
