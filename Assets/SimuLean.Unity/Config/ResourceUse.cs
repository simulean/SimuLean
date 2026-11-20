using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Resources;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>A station wrapper whose engine element can use resources for its process (shows the Process block).</summary>
    public interface IProcessResourceUser { }

    /// <summary>One kind of resource needed: how many, and which (a filter over the team's members, e.g. skill == welding).</summary>
    [Serializable]
    public class ResourceRequirementSpec
    {
        [Min(1)] public int count = 1;
        [Tooltip("Which members qualify (empty = any), e.g. skill == welding.")]
        public ListQuerySpec query = new ListQuerySpec();
        [Tooltip("Optional description shown in lists and reports (e.g. the skill).")]
        public string label = "";
        [Tooltip("The team these members come from (empty = the use's team): e.g. a crane of the cranes' team and a driver " +
                 "of the drivers' team in one job. Not for transport (a move of several carriers gathers one team).")]
        public UnityResourceTeam team;

        /// <summary>The record <c>count#query#label[#team]</c> (a downtime's repair crew: its parameter bag); the team only
        /// when it is not <paramref name="useTeam"/> (its model id, <see cref="SceneExtractor.TeamId"/>).</summary>
        public string Encode(UnityResourceTeam useTeam = null)
            => ResourceBuild.EncodeRequirement(Math.Max(1, count), query != null ? query.Encode() : "", label,
                                               team != null && team != useTeam ? SceneExtractor.TeamId(team) : "");

        /// <summary>The engine's typed requirement (an element's settings); the team only when it is not
        /// <paramref name="useTeam"/>.</summary>
        public RequirementSettings ToSettings(UnityResourceTeam useTeam = null)
            => RequirementSettings.Of(Math.Max(1, count), query != null ? query.Encode() : "", label ?? "",
                                      team != null && team != useTeam ? SceneExtractor.TeamId(team) : null);
    }

    /// <summary>
    /// How an element uses a resource team: the team (null = not used), the requirements (empty = one member), the request
    /// priority (an expression) and preemption. <see cref="WaitingResourceUse"/> (process, setup crew, repair crew) adds the
    /// acquisition order and the max wait; <see cref="TransportResourceUse"/> its move times.
    /// </summary>
    [Serializable]
    public class ResourceUse
    {
        [Tooltip("Resource team that does this work: the switch and the default team of the requirements. Empty = no " +
                 "resources, unless a requirement names its own team (then every requirement must name one).")]
        public UnityResourceTeam team;

        [Tooltip("Resources needed for each job (empty = one member of the team). Several = e.g. an operator and a tool. " +
                 "A requirement without a team takes the use's team.")]
        public List<ResourceRequirementSpec> requirements = new List<ResourceRequirementSpec>();

        [Tooltip("Priority of the requests: an expression over the item and the element (e.g. prio, or item.prio * 10 + item.age); " +
                 "empty = 1. Larger = more important. The team's request order decides how it is used; with Preemption, a request " +
                 "takes members only from work of strictly lower priority.")]
        [SimuLean.Unity.SimuLeanExpression("priority")]
        public string priority = "1";

        [Tooltip("When no member is free. None: wait (default). Resume: take a member from work of strictly lower priority; " +
                 "that work waits for a member and goes on where it was. Restart: the same, the interrupted work starts again. " +
                 "Abort: cancel the interrupted work — only work without an item, never a station's job or a carried item. " +
                 "With a mode, a member's breakdown also interrupts this work (the team's On Stop decides what follows).")]
        public PreemptMode preemption = PreemptMode.None;

        [Tooltip("Requests of higher priority with a preemption mode may take this work's members (default on): while they walk " +
                 "to the element, while they work during its process (only that job, task or cycle waits), during their own time " +
                 "(another member finishes it) or while they hold the item after the process (another member replaces them); " +
                 "carriers only on their way, empty, to the pick-up — a loaded carrier always delivers.")]
        public bool preemptable = true;

        /// <summary>The use is active: its team is set, or a requirement names a team of its own (a requirement
        /// without one then fails the build, saying what to set).</summary>
        public bool IsUsed => team != null || RequirementTeam != null;

        /// <summary>The team that stands for the use: its own, else its first requirement's.</summary>
        public UnityResourceTeam EffectiveTeam => team != null ? team : RequirementTeam;

        private UnityResourceTeam RequirementTeam
        {
            get
            {
                if (requirements != null) foreach (var r in requirements) if (r != null && r.team != null) return r.team;
                return null;
            }
        }

        /// <summary>The requirements as the engine's typed records (an element's settings).</summary>
        public List<RequirementSettings> TypedRequirements()
        {
            var list = new List<RequirementSettings>();
            if (requirements != null) foreach (var r in requirements) if (r != null) list.Add(r.ToSettings(team));
            return list;
        }

        /// <summary>Writes the team, requirements and priority label as element parameters (headless model).</summary>
        /// <summary>The same into any config (an element's settings, a downtime's repair crew).</summary>
        protected void ExportUse(IConfigValues parameters, string teamId, string teamKey, string reqKey, string prioKey)
        {
            parameters.Set(teamKey, teamId);
            var typed = TypedRequirements();
            if (typed.Count > 0) parameters.Set(reqKey, typed);
            if (!string.IsNullOrWhiteSpace(priority)) parameters.Set(prioKey, priority);
        }

        /// <summary>Writes the preemption mode and whether the use's work can be taken.</summary>
        protected void ExportPreemption(IConfigValues parameters, string modeKey, string preemptableKey)
        {
            parameters.Set(modeKey, preemption.ToString());
            parameters.Set(preemptableKey, preemptable);
        }
    }

    /// <summary>A use whose jobs wait for their members at the element (process, setup crew, repair crew;  not transport):
    /// how several requirements are taken and the max wait.</summary>
    [Serializable]
    public class WaitingResourceUse : ResourceUse
    {
        [Tooltip("With several requirements: All At Once (default) — granted only when every one can be met at the same " +
                 "moment, nothing held meanwhile; In Order — one by one in list order, the members already granted wait HELD " +
                 "at the element for the next (a crane, then a driver). In Order can deadlock when two uses take the same teams " +
                 "in opposite orders (reported as a stall). Not for transport.")]
        public AcquireOrder acquire = AcquireOrder.AllAtOnce;

        [Tooltip("The longest a job waits for these resources (a time, or an expression over the item and the element, " +
                 "evaluated when the wait starts); empty or 0 = no limit. Not for transport.")]
        [SimuLean.Unity.SimuLeanExpression("maxWait")]
        public string maxWait = "";

        [Tooltip("When the max wait is over (always counted as a timeout and signalled: WaitTimedOut). Keep Waiting: only " +
                 "record it. Give Up: release everything held for the job (e.g. a crane held while the driver is waited for, an " +
                 "operator waiting for a part that does not come) and ask again behind the others. Reroute (a Multi-Server's " +
                 "process): the item leaves unprocessed by the Timeout Target.")]
        public SimuLean.TimeoutAction onTimeout = SimuLean.TimeoutAction.KeepWaiting;

        /// <summary>Writes the max wait (0 = no limit) and its action — also at their defaults, so a study can vary them from
        /// there ("every property is parameterisable").</summary>
        protected void ExportMaxWait(IConfigValues parameters, string maxWaitKey, string onTimeoutKey)
        {
            parameters.Set(maxWaitKey, string.IsNullOrWhiteSpace(maxWait) ? "0" : maxWait.Trim());
            parameters.Set(onTimeoutKey, onTimeout.ToString());
        }

        /// <summary>Writes the acquisition order, also at its default.</summary>
        protected void ExportAcquire(IConfigValues parameters, string key) => parameters.Set(key, acquire.ToString());
    }

    /// <summary>
    /// The crew that repairs a downtime's stops (<see cref="UnityDowntime.repairBy"/>): on a stop the element waits
    /// (WAITING_FOR_REPAIR) for members of the team, who travel to it; the repair time starts when they are there. Same
    /// fields as an element's use; a repair's work is <b>not</b> preemptable by default.
    /// </summary>
    [Serializable]
    public class RepairResourceUse : WaitingResourceUse
    {
        public RepairResourceUse() { preemptable = false; }

        /// <summary>Writes the repair keys (<c>repairBy.team</c>, <c>repairBy.requirements</c>, <c>repairBy.priority</c>,
        /// <c>repairBy.preemption</c>, <c>repairBy.preemptable</c>) into the downtime's parameters.</summary>
        public void Export(DowntimeConfig dc, string teamId)
        {
            ExportUse(dc, teamId, "repairBy.team", "repairBy.requirements", "repairBy.priority");
            ExportPreemption(dc, "repairBy.preemption", "repairBy.preemptable");
            ExportAcquire(dc, "repairBy.acquire");
            ExportMaxWait(dc, "repairBy.maxWait", "repairBy.onTimeout");
        }
    }

    /// <summary>Resources for the station's process, and when they are released.</summary>
    [Serializable]
    public class ProcessResourceUse : WaitingResourceUse
    {
        [Tooltip("End Of Process: free as soon as the process ends. Item Leaves: kept (HELD) while the station is blocked, until the item leaves. " +
                 "At Element: the resource goes with the item (a device lent to a patient) until it enters Release At.")]
        public ProcessRelease release = ProcessRelease.EndOfProcess;
        [Tooltip("At Element: where the resource comes back (when the item enters it), e.g. the device return desk.")]
        public SElement releaseAt;

        [Tooltip("The resources work for their own time (below) and are then free, while the item stays for the rest of the " +
                 "process — painting then curing, an erection lift then the joint welding. Off: the whole process.")]
        public bool holdForOwnTime;
        [Tooltip("The resources' own time at the station (per job; an expression may read the item's labels).")]
        [SimuLean.Unity.SimuLeanHelp("Used only with holdForOwnTime = true: how long the resources stay. The station's process time is " +
                                     "its delayStrategy.")]
        public RandomProcessConfig holdFor = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1 };

        [Tooltip("Secure the resources before taking the part: when an item it would take cannot be sent, the station first " +
                 "asks for its resources and takes an item only once they are there (walked in); the part waits upstream " +
                 "meanwhile. The grant is for the next item that comes in. The Multi-Server only.")]
        public bool acquireBeforeEntry;

        [Tooltip("Reroute: where the item goes when its job gives up waiting for the resources (e.g. a Sink, or another " +
                 "station); while it cannot take the item, the item waits in its slot.")]
        public SElement timeoutTarget;

        /// <param name="elementId">The model id of an element (a scene run: two elements may share a name);
        /// null = by name (an editor's view of one element).</param>
        public void Export(ElementConfig ec, string teamId, System.Func<SElement, string> elementId = null)
        {
            ExportUse(ec, teamId, "processBy.team", "processBy.requirements", "processBy.priority");
            ExportPreemption(ec, "processBy.preemption", "processBy.preemptable");
            ExportAcquire(ec, "processBy.acquire");
            // the Multi-Server only: there also at its default (a study may switch it); elsewhere only a stray true, which
            // the build refuses with a clear message
            if (acquireBeforeEntry || ec.Type == "MultiServer") ec.Set("processBy.acquireBeforeEntry", acquireBeforeEntry);
            ec.Set("processBy.release", release.ToString());
            if (release == ProcessRelease.AtElement && releaseAt != null)
                ec.Set("processBy.releaseAt", elementId?.Invoke(releaseAt) ?? releaseAt.name);
            if (holdForOwnTime && holdFor != null) ec.Set("processBy.holdFor", holdFor.ToSettings());
            ExportMaxWait(ec, "processBy.maxWait", "processBy.onTimeout");
            if (!string.IsNullOrWhiteSpace(maxWait) && onTimeout == SimuLean.TimeoutAction.Reroute && timeoutTarget != null)
                ec.Set("processBy.timeoutTarget", elementId?.Invoke(timeoutTarget) ?? timeoutTarget.name);
        }

        /// <summary>An Assembly Station without a process team whose tasks name their own teams — writes the
        /// settings those tasks' uses take from this block (priority, preemption, preemptable, acquisition order, max wait and
        /// its action; <c>ResourceBuild.AssemblyTaskUses</c>), with no team key (the station itself asks for no resources).</summary>
        public void ExportTaskSettings(ElementConfig ec)
        {
            if (!string.IsNullOrWhiteSpace(priority)) ec.Set("processBy.priority", priority);
            ExportPreemption(ec, "processBy.preemption", "processBy.preemptable");
            ExportAcquire(ec, "processBy.acquire");
            ExportMaxWait(ec, "processBy.maxWait", "processBy.onTimeout");
        }
    }

    /// <summary>
    /// Resources that carry the element's items to its next elements. A move = load + travel + unload: load / unload
    /// from the member (its own times) unless overridden here; travel = the time given here (abstract transport —
    /// the transport network will compute it for spatial resources).
    /// </summary>
    [Serializable]
    public class TransportResourceUse : ResourceUse
    {
        [Tooltip("Travel time of each move (abstract teams; a team on the network takes it from the route).")]
        public RandomProcessConfig travelTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0 };

        [Tooltip("Use these load / unload times instead of each member's own.")]
        public bool overrideLoadTimes;
        public RandomProcessConfig loadTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0 };
        public RandomProcessConfig unloadTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0 };

        public void Export(ElementConfig ec, string teamId)
        {
            ExportUse(ec, teamId, "transportBy.team", "transportBy.requirements", "transportBy.priority");
            ExportPreemption(ec, "transportBy.preemption", "transportBy.preemptable");
            if (travelTime != null && !OnNetwork) ec.Set("transportBy.travelTime", travelTime.ToSettings());
            if (!overrideLoadTimes) return;
            if (loadTime != null) ec.Set("transportBy.loadTime", loadTime.ToSettings());
            if (unloadTime != null) ec.Set("transportBy.unloadTime", unloadTime.ToSettings());
        }

        /// <summary>The team is spatial (network, matrix…): its travel model gives the travel time.</summary>
        public bool OnNetwork => EffectiveTeam != null && EffectiveTeam.travel != TeamTravel.Abstract;

    }

    /// <summary>Where walking items find their way (the resources' travel models).</summary>
    public enum WalkRoute { Network, Grid, Matrix }

    /// <summary>
    /// The element's items <b>walk by themselves</b> to its next elements (the item is a mobile entity — a
    /// patient), on the model's transport network (from this element's Location to the next one's), a walk grid or a
    /// travel matrix, in model time; engine <c>WalkLink</c>. A transport team, when set, carries them instead.
    /// </summary>
    [Serializable]
    public class ItemWalkUse
    {
        [Tooltip("Walking speed (m/s); 0 = the items do not walk (handed over at once).")]
        [Min(0f)] public float speed;
        [Tooltip("Where they find their way: the transport network (Location to Location), a walk grid (A*) or a travel matrix.")]
        public WalkRoute route = WalkRoute.Network;
        public UnityWalkGrid grid;
        public UnityTravelMatrix matrix;
        [Tooltip("Set off even when the next element has no room now, and wait at its door (patients going to their " +
                 "appointment). Off: walk only to an element with room.")]
        public bool waitAtDoor;

        public bool IsUsed => speed > 0f;

        /// <summary>Writes the walk keys (<c>walkBy.speed</c>, <c>walkBy.route</c>, <c>walkBy.grid</c> / <c>walkBy.matrix</c> by the
        /// ids <paramref name="spaceId"/> gives the grid / matrix).</summary>
        public void Export(ElementConfig ec, Func<Component, string> spaceId)
        {
            if (!IsUsed) return;
            ec.Set("walkBy.speed", (double)speed);
            ec.Set("walkBy.route", route.ToString());
            if (waitAtDoor) ec.Set("walkBy.waitAtDoor", true);
            if (route == WalkRoute.Grid && grid != null) ec.Set("walkBy.grid", spaceId(grid));
            if (route == WalkRoute.Matrix && matrix != null) ec.Set("walkBy.matrix", spaceId(matrix));
        }
    }
}
