using System;
using System.Collections.Generic;
using SimuLean.Transport;

namespace SimuLean.Resources
{
    /// <summary>
    /// A travel model that drives the move itself after it starts: the plan is open-ended and the
    /// executor calls <see cref="Begin"/> once the leg exists; the model ends it with <see cref="TaskExecutor.Arrive"/>.
    /// </summary>
    public interface IDrivenTravel
    {
        void Begin(TaskExecutor executor, TravelLeg leg);
    }

    /// <summary>
    /// Traffic on a <see cref="TransportNetwork"/>: every track (either direction — two
    /// vehicles cannot pass on it) holds at most <see cref="SegmentCapacity"/> vehicles. A vehicle reserves the next
    /// segment before entering it, keeps the one it is on while it waits at the node (as a real vehicle does), and
    /// enters a released segment in arrival order. Shared by every team travelling on the network; one per model run
    /// (<see cref="Reset"/> at start).
    /// <para><b>Deadlocks</b> (a cycle of vehicles each waiting for a segment the next one holds — e.g. two vehicles
    /// meeting head-on) are detected when a vehicle starts waiting, by following the wait chain; the vehicle closing
    /// the cycle is let through (the segment goes over its capacity for it) and the event is counted
    /// (<see cref="DeadlocksResolved"/>) — a model with many of them needs passing places or one-way tracks.</para>
    /// </summary>
    public sealed class TrafficControl
    {
        private readonly Dictionary<string, Segment> segments = new Dictionary<string, Segment>();
        // The moves in progress (a finished one leaves: they were all kept until the next run, each holding its leg,
        // route and load).
        private readonly HashSet<TrafficMover> movers = new HashSet<TrafficMover>();

        public TrafficControl(int segmentCapacity = 1) { SegmentCapacity = Math.Max(1, segmentCapacity); }

        public int SegmentCapacity { get; }

        /// <summary>Times a vehicle had to wait for a segment (waits of zero length — a segment freed at that same instant —
        /// not counted), and the total time waited.</summary>
        public int Waits { get; internal set; }
        public double TotalWait { get; internal set; }
        public int DeadlocksResolved { get; private set; }

        private sealed class Segment
        {
            public readonly List<TrafficMover> Holders = new List<TrafficMover>();
            public readonly List<TrafficMover> Waiting = new List<TrafficMover>();
        }

        internal static string Key(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "\u0001" + b : b + "\u0001" + a;

        private Segment Seg(string key)
        {
            if (!segments.TryGetValue(key, out var s)) segments[key] = s = new Segment();
            return s;
        }

        /// <summary>Vehicles on a track now (either direction).</summary>
        public int Occupancy(string a, string b) => segments.TryGetValue(Key(a, b), out var s) ? s.Holders.Count : 0;

        internal void Register(TrafficMover m) => movers.Add(m);
        internal void Unregister(TrafficMover m) => movers.Remove(m);

        /// <summary>Moves in progress under this control.</summary>
        public int ActiveMoves => movers.Count;

        internal bool TryEnter(string key, TrafficMover m)
        {
            var s = Seg(key);
            if (s.Holders.Contains(m)) return true;
            if (s.Holders.Count < SegmentCapacity) { s.Holders.Add(m); return true; }
            // A cycle of waits through this segment back to m: let m through rather than lock everybody.
            if (ClosesCycle(key, m)) { s.Holders.Add(m); DeadlocksResolved++; return true; }
            s.Waiting.Add(m);
            return false;
        }

        internal void Leave(string key, TrafficMover m)
        {
            if (key == null || !segments.TryGetValue(key, out var s)) return;
            s.Holders.Remove(m);
            while (s.Waiting.Count > 0 && s.Holders.Count < SegmentCapacity)
            {
                var next = s.Waiting[0];
                s.Waiting.RemoveAt(0);
                s.Holders.Add(next);
                next.Entered();
            }
        }

        // Does m waiting for `key` close a cycle? (a holder of key waits — transitively — for something m holds)
        private bool ClosesCycle(string key, TrafficMover m)
        {
            var seen = new HashSet<TrafficMover>();
            var stack = new Stack<TrafficMover>(Seg(key).Holders);
            while (stack.Count > 0)
            {
                var h = stack.Pop();
                if (h == m) return true;
                if (!seen.Add(h) || h.WaitingFor == null) continue;
                foreach (var hh in Seg(h.WaitingFor).Holders) stack.Push(hh);
            }
            return false;
        }

        /// <summary>Empties every segment and forgets the moves (new run).</summary>
        public void Reset()
        {
            foreach (var m in new List<TrafficMover>(movers)) m.Cancel();
            movers.Clear();
            segments.Clear();
            Waits = 0; TotalWait = 0; DeadlocksResolved = 0;
        }
    }

    /// <summary>
    /// <see cref="NetworkTravel"/> under <see cref="TrafficControl"/>: the vehicle travels the route segment by
    /// segment at the network speed, waiting at a node (state <see cref="ResourceStates.TRAFFIC_WAIT"/>) while the next
    /// segment is full. The leg exposes the current segment and its times for views. Stops pause a segment's travel.
    /// </summary>
    public sealed class TrafficNetworkTravel : ITravelModel, IDrivenTravel
    {
        public TrafficNetworkTravel(NetworkTravel network, TrafficControl traffic)
        {
            Network = network ?? throw new ArgumentNullException(nameof(network));
            Traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        }

        public NetworkTravel Network { get; }
        public TrafficControl Traffic { get; }

        public double Distance(string from, string to) => Network.Distance(from, to);

        public TravelPlan Plan(TaskExecutor executor, string from, string to)
        {
            var p = Network.Plan(executor, from, to);
            return new TravelPlan { Route = p.Route, Distance = p.Distance, Duration = double.NaN };
        }

        public void Begin(TaskExecutor executor, TravelLeg leg)
        {
            var m = new TrafficMover(this, executor, leg);
            Traffic.Register(m);
            m.Next();
        }
    }

    /// <summary>One vehicle's move under traffic control (segment by segment).</summary>
    internal sealed class TrafficMover
    {
        private readonly TrafficNetworkTravel model;
        private readonly TaskExecutor executor;
        private readonly TravelLeg leg;
        private int index;              // segment being travelled / waited for: route[index] → route[index + 1]
        private string holding;         // the segment it is on (or last left, at a node)
        private WorkHandle work;
        private double waitSince = double.NaN;
        private bool cancelled;

        internal string WaitingFor { get; private set; }

        internal TrafficMover(TrafficNetworkTravel model, TaskExecutor executor, TravelLeg leg)
        {
            this.model = model; this.executor = executor; this.leg = leg;
        }

        internal void Next()
        {
            if (cancelled) return;
            var route = leg.Route;
            if (route == null || index >= route.Count - 1)
            {
                model.Traffic.Leave(holding, this);
                holding = null;
                model.Traffic.Unregister(this);
                executor.Arrive();
                return;
            }
            string key = TrafficControl.Key(route[index], route[index + 1]);
            if (model.Traffic.TryEnter(key, this)) { Go(key); return; }
            WaitingFor = key;
            waitSince = executor.GetSimClock().GetSimulationTime();
            executor.TrafficWaiting(true);
        }

        internal void Entered()
        {
            if (cancelled) return;
            string key = WaitingFor;
            WaitingFor = null;
            double waited = executor.GetSimClock().GetSimulationTime() - waitSince;
            if (waited > 0) { model.Traffic.TotalWait += waited; model.Traffic.Waits++; }
            executor.TrafficWaiting(false);
            Go(key);
        }

        private void Go(string key)
        {
            if (holding != null && holding != key) model.Traffic.Leave(holding, this);
            holding = key;
            var route = leg.Route;
            // on the track types the route was planned on (any type: a shorter conveyor / crane track between the same
            // two nodes gave a shorter segment than the planned leg)
            double length = model.Network.Distance(route[index], route[index + 1]);
            double d = length / model.Network.Speed;
            double now = executor.GetSimClock().GetSimulationTime();
            leg.SegmentIndex = index;
            leg.SegmentStartTime = now;
            leg.SegmentEndTime = now + d;
            work = executor.ScheduleWork(new Step(this), d);
        }

        internal void Cancel() { cancelled = true; work?.Cancel(); }

        private sealed class Step : IEvent
        {
            private readonly TrafficMover m;
            public Step(TrafficMover m) { this.m = m; }
            public void Execute() { m.work = null; m.index++; m.Next(); }
        }
    }
}
