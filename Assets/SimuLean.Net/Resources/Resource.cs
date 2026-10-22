using System;
using System.Collections.Generic;
using SimuLean.Collections;

namespace SimuLean.Resources
{
    /// <summary>What a task is for; also decides when its resources are released.</summary>
    public enum TaskType { Process, Setup, Transport, Other }

    /// <summary>Abstract resources are capacity only (no position, travel takes no time); spatial ones live on
    /// the transport network and travel.</summary>
    public enum ResourceMode { Abstract, Spatial }

    /// <summary>States of resources (on top of the common ones: IDLE, PROCESSING, SETUP, the stop states).</summary>
    public static class ResourceStates
    {
        public static readonly ElementState TRAVELLING_EMPTY = ElementState.Of("TRAVELLING_EMPTY");
        public static readonly ElementState TRAVELLING_LOADED = ElementState.Of("TRAVELLING_LOADED");
        public static readonly ElementState LOADING = ElementState.Of("LOADING");
        public static readonly ElementState UNLOADING = ElementState.Of("UNLOADING");
        /// <summary>Assigned but waiting for its task to let it go (e.g. an operator kept by a blocked station).</summary>
        public static readonly ElementState HELD = ElementState.Of("HELD");
        /// <summary>Assigned and waiting (a <c>Delay</c> step).</summary>
        public static readonly ElementState WAITING = ElementState.Of("WAITING");
        /// <summary>Travelling, but stopped at a node until the next segment has room (traffic).</summary>
        public static readonly ElementState TRAFFIC_WAIT = ElementState.Of("TRAFFIC_WAIT");

        /// <summary>States in which the resource is working for a task (they count as utilisation).</summary>
        public static readonly ElementState[] Busy =
        {
            ElementState.PROCESSING, ElementState.SETUP, TRAVELLING_EMPTY, TRAVELLING_LOADED, LOADING, UNLOADING, HELD, WAITING, TRAFFIC_WAIT,
        };
    }

    /// <summary>
    /// Anything that does work in the model and is assigned to tasks — operators, vehicles, cranes, AS/RS,
    /// robots. An <see cref="Element"/> for its identity, states, statistics and
    /// downtime (<see cref="Element.Stop"/>: <c>AfterCurrent</c> = finish the task, then the break), but not
    /// part of the item flow: it is not linked and never receives items through links. Its statistics come
    /// from its state tracker (utilisation = time in <see cref="ResourceStates.Busy"/>), its task counters and
    /// its usage per requester — not from links.
    /// </summary>
    public abstract class Resource : Element, IListFieldSource
    {
        private readonly Dictionary<string, double> usage = new Dictionary<string, double>();

        protected Resource(string name, SimClock clock, ResourceMode mode = ResourceMode.Abstract, IVElement vElement = null)
            : base(name, clock, vElement)
        {
            Mode = mode;
        }

        /// <summary>Abstract (capacity only) or spatial (on the network).</summary>
        public ResourceMode Mode { get; }

        /// <summary>Items it can carry at once (vehicles, cranes); 1 for operators.</summary>
        public int Capacity { get; set; } = 1;

        /// <summary>Node it starts from and returns to (spatial resources).</summary>
        public string HomeNode { get; set; }

        /// <summary>The task it is doing (null when free).</summary>
        public TaskSequence CurrentTask { get; protected set; }

        /// <summary>Free for a new task: no task, not stopped and not finishing before a stop.</summary>
        public bool IsAvailable => CurrentTask == null && !IsDown && !IsInputBlocked;

        /// <summary>Tasks completed (since start or warm-up).</summary>
        public int TasksDone { get; protected set; }

        /// <summary>Time since it last became free (for dispatching rules such as "longest idle first").</summary>
        public double IdleSince { get; protected set; }

        /// <summary>Fraction of time spent working for tasks (<see cref="ResourceStates.Busy"/>).</summary>
        public double Utilization
        {
            get
            {
                // a dispatching rule reads it for every free member at every request: computed once per time and state
                // (the same sum, so the same value; review2 #15)
                double now = simClock.GetSimulationTime();
                if (now == utilizationAt && StateVersion == utilizationVersion) return utilization;
                double u = 0;
                foreach (var s in ResourceStates.Busy) u += GetStateRatio(s);
                utilization = u; utilizationAt = now; utilizationVersion = StateVersion;
                return u;
            }
        }
        private double utilization, utilizationAt = double.NaN;
        private int utilizationVersion = -1;

        /// <summary>Time spent on tasks per requester name (stations / elements that asked for it).</summary>
        public IReadOnlyDictionary<string, double> UsageByRequester => usage;

        /// <summary>Raised when it becomes free for a new task (task finished, stop ended).</summary>
        public event Action<Resource> BecameAvailable;

        // ── labels (skills, attributes) ──────────────────────────────────────

        // Labels are the resource's Properties (SimEntity), like items' and elements'.

        public void SetLabelValue(string label, double value) => Properties.SetDouble(label, value);

        /// <summary>Numeric text is stored as a number, like item labels.</summary>
        public void SetLabelValue(string label, string value) => Properties.SetParsed(label, value);

        public double? GetLabelValue(string label) => Properties.TryGetDouble(label, out double v) ? v : (double?)null;

        public string GetLabelText(string label) => Properties.GetText(label);

        public IEnumerable<string> LabelNames => Properties.Names;

        // ── fields for lists (dispatching rules) ─────────────────────────────

        /// <summary>Fields a dispatcher's resource list reads live: <c>idleTime</c> (time free so far, 0 when
        /// busy), <c>utilization</c>, <c>tasksDone</c>, <c>capacity</c>, <c>distance</c> (to the location of the
        /// request being matched — "nearest resource" rules), text <c>name</c> / <c>location</c>, and every label
        /// (skills, attributes).</summary>
        public double GetListNumber(string field)
        {
            switch (field)
            {
                case "idleTime": return IsAvailable ? simClock.GetSimulationTime() - IdleSince : 0;
                case "utilization": return Utilization;
                case "tasksDone": return TasksDone;
                case "capacity": return Capacity;
                case "distance": return MatchLocation == null ? 0 : DistanceTo(MatchLocation);
            }
            return GetLabelValue(field) ?? double.NaN;
        }

        /// <summary>Location of the request a dispatcher is matching (its <c>distance</c> field is measured to it).</summary>
        internal string MatchLocation;

        /// <summary>Distance from where it is to <paramref name="location"/> (0 for abstract resources).</summary>
        public virtual double DistanceTo(string location) => 0;

        public string GetListText(string field)
        {
            switch (field)
            {
                case "name": return GetName();
                case "location": return Location;
            }
            return GetLabelText(field);
        }

        // ── bookkeeping for subclasses ───────────────────────────────────────

        /// <summary>Statistics count from here (run start or warm-up): a task running then is counted from it on.</summary>
        protected double StatsFrom { get; private set; }

        protected void RecordUsage(string requester, double time)
        {
            string key = requester ?? "";
            usage[key] = (usage.TryGetValue(key, out double t) ? t : 0) + time;
        }

        /// <summary>Marks it free and tells whoever dispatches it.</summary>
        protected void BecomeAvailable()
        {
            IdleSince = simClock.GetSimulationTime();
            if (IsAvailable) BecameAvailable?.Invoke(this);
        }

        // ── element lifecycle ────────────────────────────────────────────────

        public override void Start()
        {
            base.Start();
            CurrentTask = null;
            TasksDone = 0;
            usage.Clear();
            StatsFrom = simClock.GetSimulationTime();
            Location = Mode == ResourceMode.Spatial ? HomeNode : null;
            IdleSince = simClock.GetSimulationTime();
            SetState(ElementState.IDLE);
        }

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            TasksDone = 0;
            usage.Clear();
            StatsFrom = simClock.GetSimulationTime();
        }

        protected override void OnResumed(StopToken token)
        {
            base.OnResumed(token);
            if (IsAvailable) BecomeAvailable();
        }

        // Not part of the item flow.
        public override int GetQueueLength() => CurrentTask != null ? 1 : 0;
        public override int GetFreeCapacity() => IsAvailable ? 1 : 0;
        public override bool CheckAvailability(Item theItem) => false;
        public override bool Receive(Item theItem) => false;
        public override bool Unblock() => false;
    }
}
