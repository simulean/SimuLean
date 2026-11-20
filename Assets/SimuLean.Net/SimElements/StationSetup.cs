using System;
using SimuLean.Resources;

namespace SimuLean
{
    /// <summary>When a station sets itself up (a changeover) before processing an item (<see cref="StationSetup"/>).</summary>
    public enum SetupWhen
    {
        /// <summary>Never (the default: the station processes at once, as without a setup).</summary>
        None,
        /// <summary>Before every item's process.</summary>
        EachItem,
        /// <summary>Only when the item's value of <see cref="StationSetup.Label"/> differs from the previous item set up or
        /// processed at the same slot (a Multi-Server's server, a Combiner's assembly); the first item of a slot pays one
        /// when <see cref="StationSetup.First"/>.</summary>
        LabelChange,
    }

    /// <summary>A station that can set itself up before its process (<see cref="MultiServer"/>, <see cref="Combiner"/>,
    /// <see cref="SingleJobStation"/> / <see cref="PickingStation"/>, <see cref="AssemblyStation"/>, <see cref="Stacker"/>,
    /// <c>Naval.ErectionDock</c>).</summary>
    public interface ISetupStation
    {
        /// <summary>Its setup; null = none (the default).</summary>
        StationSetup Setup { get; set; }

        /// <summary>True when the station could take <paramref name="item"/> now without a setup because it is already set
        /// up for it: a <see cref="SetupWhen.LabelChange"/> rule and the slot that takes it next last set up / processed the
        /// item's value (an upstream queue grouping by setup prefers such items, <see cref="ItemsQueue.GroupBySetup"/>).
        /// Read-only: nothing is recorded.</summary>
        bool IsSetUpFor(Item item);
    }

    /// <summary>What a station tells its setup: the slot's state, and what follows the setup.</summary>
    internal interface ISetupHost
    {
        /// <summary>The slot (and the element, derived from its slots) changes state: WAITING_FOR_RESOURCE while the setup
        /// crew is coming, SETUP while it sets up.</summary>
        void SetupState(ServerProcess slot, ElementState state);

        /// <summary>The setup of <paramref name="slot"/> is over. <paramref name="keptResources"/>: its process resources
        /// are already there (they did the setup, <see cref="StationSetup.KeepForProcess"/>) — process at once; else ask the
        /// process resources (if any), then process.</summary>
        void SetupEnded(ServerProcess slot, bool keptResources);
    }

    /// <summary>
    /// A station's setup (changeover) before its process: when (<see cref="When"/>), how long (any sampler, as
    /// the process time, its own random stream: purpose <c>setup</c>) and who does it — the machine alone (no
    /// <see cref="Resources"/>), a crew of its own (<see cref="Resources"/>, requests and tasks of type
    /// <see cref="TaskType.Setup"/>: its members hold in SETUP; released when the setup ends) or the process's own resources
    /// (<see cref="KeepForProcess"/>: one request with the process use, before the setup; its members set up, then process —
    /// no new request). The station keeps the setup time (<see cref="Element.ScheduleWork"/>: its stops pause it); the slot
    /// is SETUP meanwhile. A setup crew can be preempted as a process crew (its setup work is the job's work).
    /// Statistics (two-source rule: its own counters): <see cref="Count"/> setups completed and their sampled
    /// <see cref="TotalTime"/>; the time in SETUP is the state tracker's.
    /// </summary>
    public sealed class StationSetup
    {
        /// <summary>When a setup is needed (None = never).</summary>
        public SetupWhen When { get; set; }
        /// <summary><see cref="SetupWhen.LabelChange"/>: the item label whose change needs a setup (e.g. <c>type</c>, <c>ref</c>).</summary>
        public string Label { get; set; } = "type";
        /// <summary><see cref="SetupWhen.LabelChange"/>: the first item of a slot needs a setup too (default true: the station
        /// does not know how it was left).</summary>
        public bool First { get; set; } = true;
        /// <summary>The setup duration (any sampler; expressions read the item and the station). Null = 0. A pair of the
        /// <see cref="Matrix"/> takes precedence.</summary>
        public IRandomSampler Time { get; set; }
        /// <summary>The setup times per from → to pair of <see cref="Label"/>'s values (null = <see cref="Time"/> for every
        /// setup); a pair it does not list uses <see cref="Time"/>.</summary>
        public SetupMatrix Matrix { get; set; }
        /// <summary>The setup crew (null = the machine sets up alone, even when its process needs an operator).</summary>
        public StationResources Resources { get; set; }
        /// <summary>The process's resources do the setup and stay for the process (one request, the process use's, made
        /// before the setup). Only with a process team that is the setup's (the build checks it).</summary>
        public bool KeepForProcess { get; set; }

        /// <summary>Setups completed (since start or warm-up).</summary>
        public int Count { get; private set; }
        /// <summary>Their sampled durations summed (stop pauses not included; the time in SETUP is the state tracker's).</summary>
        public double TotalTime { get; private set; }
        /// <summary>The mean setup duration (0 without setups).</summary>
        public double MeanTime => Count > 0 ? TotalTime / Count : 0.0;

        /// <summary>True when it can need setups.</summary>
        public bool IsActive => When != SetupWhen.None;

        /// <summary>Warm-up / start: forget the setups counted so far.</summary>
        public void ResetStatistics() { Count = 0; TotalTime = 0; }

        /// <summary>The stations whose process resources can do the setup and stay for the process
        /// (<see cref="KeepForProcess"/>: one request per job). Not the assembly station (one request per task) nor the
        /// stacker (one per cycle): there the setup crew is its own.</summary>
        public static bool CanKeepForProcess(Element station)
            => station is MultiServer || station is SingleJobStation || station is SimuLean.Naval.ErectionDock;

        /// <summary>Does <paramref name="item"/> need a setup at <paramref name="slot"/>? Records the item's label value as the
        /// slot's last one, and the one before as the setup's <i>from</i> value (the <see cref="Matrix"/>'s pair).</summary>
        internal bool Needed(ServerProcess slot, Item item)
        {
            if (When == SetupWhen.None) return false;
            string key = item?.GetLabelText(Label);   // a number as invariant text; null = the item has no such label
            bool had = slot.hasSetupKey;
            bool needed = When == SetupWhen.EachItem || (had ? !string.Equals(key, slot.setupKey, StringComparison.Ordinal) : First);
            slot.setupFrom = had ? slot.setupKey : null;
            slot.hasSetupFrom = had;
            slot.setupKey = key;
            slot.hasSetupKey = true;
            return needed;
        }

        /// <summary>The slot is set up for <paramref name="item"/>: a <see cref="SetupWhen.LabelChange"/> rule and the slot's
        /// last value is the item's (nothing recorded; <see cref="ISetupStation.IsSetUpFor"/>).</summary>
        internal bool Matches(ServerProcess slot, Item item)
            => When == SetupWhen.LabelChange && slot != null && slot.hasSetupKey
               && string.Equals(item?.GetLabelText(Label), slot.setupKey, StringComparison.Ordinal);

        // the sampler of the setup decided last at the slot: its matrix pair, else the setup time
        private IRandomSampler TimeFor(ServerProcess slot)
            => Matrix?.Find(slot.hasSetupFrom ? slot.setupFrom : null, slot.hasSetupFrom, slot.setupKey) ?? Time;

        /// <summary>Starts the setup of <paramref name="slot"/>'s job: the crew first (if any), then the setup time; the host
        /// is told when it is over. <paramref name="process"/> = the station's process resources (null = none).</summary>
        internal void Begin(Element station, ServerProcess slot, StationResources process, ISetupHost host)
        {
            var run = new SetupRun(this, station, slot, host, KeepForProcess && process != null ? process : null);
            slot.setup = run;
            if (run.Kept != null && slot.job != null && slot.job.BeforeEntry) run.Start();   // secured before the part: there
            else if (run.Kept != null)
            {
                host.SetupState(slot, ElementState.WAITING_FOR_RESOURCE);
                slot.job = run.Kept.AcquireWithSetup(station, slot.GetItem(), run.Start);
            }
            else if (Resources != null)
            {
                host.SetupState(slot, ElementState.WAITING_FOR_RESOURCE);
                run.Job = Resources.Acquire(station, slot.GetItem(), run.Start);
            }
            else run.Start();
        }

        private void Completed(double duration) { Count++; TotalTime += duration; }

        /// <summary>One setup in progress at a slot: its timing event (<see cref="Element.ScheduleWork"/>).</summary>
        internal sealed class SetupRun : IEvent
        {
            private readonly StationSetup owner;
            private readonly Element station;
            private readonly ServerProcess slot;
            private readonly ISetupHost host;
            /// <summary>The process resources that set up and stay (<see cref="KeepForProcess"/>), or null.</summary>
            internal readonly StationResources Kept;
            /// <summary>The setup crew's job (its own crew), or null.</summary>
            internal ResourceJob Job;
            private double duration;

            internal SetupRun(StationSetup owner, Element station, ServerProcess slot, ISetupHost host, StationResources kept)
            {
                this.owner = owner; this.station = station; this.slot = slot; this.host = host; Kept = kept;
            }

            // the crew is there (or none is needed): set up for the sampled time
            internal void Start()
            {
                var time = owner.TimeFor(slot);
                duration = time != null ? Math.Max(0, SimuLean.Expressions.Samplers.Sample(time, slot.GetItem(), station)) : 0;
                slot.SetupStart = station.GetSimClock().GetSimulationTime();
                slot.SetupDuration = duration;
                host.SetupState(slot, ElementState.SETUP);
                var work = station.ScheduleWork(this, duration);
                // its crew may be taken while it works (the setup is the work of the crew's own job — never the slot's
                // process job secured before the part)
                (Kept ?? owner.Resources)?.SetupStarted(Kept != null ? slot.job : Job, slot, work);
            }

            void IEvent.Execute()
            {
                slot.setup = null;
                owner.Completed(duration);
                if (Kept != null)
                {
                    Kept.SetupEnded(slot.job);   // they go on to the process
                    host.SetupEnded(slot, true);
                    return;
                }
                var job = Job;
                Job = null;
                owner.Resources?.ProcessEnded(job);   // the crew is free
                host.SetupEnded(slot, false);
            }
        }
    }
}
