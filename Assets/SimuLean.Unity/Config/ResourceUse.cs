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

        public ResourceRequirement ToRequirement()
        {
            bool any = query == null || query.conditions == null || query.conditions.Count == 0;
            return new ResourceRequirement(Math.Max(1, count), any ? null : query.ToQuery(), string.IsNullOrEmpty(label) ? null : label);
        }

        public string Encode() => ResourceBuild.EncodeRequirement(Math.Max(1, count), query != null ? query.Encode() : "", label);
    }

    /// <summary>
    /// How an element uses a resource team: the team (null = not used), the requirements (empty = one member) and
    /// the item label giving the request priority. <see cref="ProcessResourceUse"/> / <see cref="TransportResourceUse"/>
    /// add what each use needs.
    /// </summary>
    [Serializable]
    public class ResourceUse
    {
        [Tooltip("Resource team that does this work (empty = no resources).")]
        public UnityResourceTeam team;

        [Tooltip("Resources needed for each job (empty = one member of the team). Several = e.g. an operator and a tool.")]
        public List<ResourceRequirementSpec> requirements = new List<ResourceRequirementSpec>();

        [Tooltip("Priority of the requests: an expression over the item and the element (e.g. prio, or item.prio * 10 + item.age); empty = 0. The team's request order decides how it is used.")]
        public string priority = "";

        public bool IsUsed => team != null;

        public List<string> EncodeRequirements()
        {
            var list = new List<string>();
            if (requirements != null) foreach (var r in requirements) if (r != null) list.Add(r.Encode());
            return list;
        }

        /// <summary>Writes the team, requirements and priority label as element parameters (headless model).</summary>
        protected void ExportUse(ElementConfig ec, string teamId, string teamKey, string reqKey, string prioKey)
        {
            ec.Parameters[teamKey] = teamId;
            var reqs = EncodeRequirements();
            if (reqs.Count > 0) ec.Parameters[reqKey] = reqs;
            if (!string.IsNullOrWhiteSpace(priority)) ec.Parameters[prioKey] = priority;
        }
    }

    /// <summary>Resources for the station's process, and when they are released.</summary>
    [Serializable]
    public class ProcessResourceUse : ResourceUse
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
        public RandomProcessConfig holdFor = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1 };

        /// <param name="elementId">The model id of an element (a scene run: two elements may share a name, review2 #13);
        /// null = by name (an editor's view of one element).</param>
        public void Export(ElementConfig ec, string teamId, System.Func<SElement, string> elementId = null)
        {
            ExportUse(ec, teamId, ResourceUseKeys.ProcessTeam, ResourceUseKeys.ProcessRequirements, ResourceUseKeys.ProcessPriority);
            ec.Parameters[ResourceUseKeys.ProcessRelease] = release.ToString();
            if (release == ProcessRelease.AtElement && releaseAt != null)
                ec.Parameters[ResourceUseKeys.ProcessReleaseAt] = elementId?.Invoke(releaseAt) ?? releaseAt.name;
            if (holdForOwnTime && holdFor != null) ec.Parameters[ResourceUseKeys.ProcessHoldFor] = holdFor.ToSamplerSpec();
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
            ExportUse(ec, teamId, ResourceUseKeys.TransportTeam, ResourceUseKeys.TransportRequirements, ResourceUseKeys.TransportPriority);
            if (travelTime != null && !OnNetwork) ec.Parameters[ResourceUseKeys.TransportTravel] = travelTime.ToSamplerSpec();
            if (!overrideLoadTimes) return;
            if (loadTime != null) ec.Parameters[ResourceUseKeys.TransportLoad] = loadTime.ToSamplerSpec();
            if (unloadTime != null) ec.Parameters[ResourceUseKeys.TransportUnload] = unloadTime.ToSamplerSpec();
        }

        /// <summary>The team is spatial (network, matrix…): its travel model gives the travel time.</summary>
        public bool OnNetwork => team != null && team.travel != TeamTravel.Abstract;

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

        /// <summary>Writes the walk keys (<c>walkSpeed</c>, <c>walkTravel</c>, <c>walkGrid</c> / <c>walkMatrix</c> by the
        /// ids <paramref name="spaceId"/> gives the grid / matrix).</summary>
        public void Export(ElementConfig ec, Func<Component, string> spaceId)
        {
            if (!IsUsed) return;
            ec.Parameters[ResourceUseKeys.WalkSpeed] = (double)speed;
            ec.Parameters[ResourceUseKeys.WalkTravel] = route.ToString();
            if (waitAtDoor) ec.Parameters[ResourceUseKeys.WalkWaitAtDoor] = true;
            if (route == WalkRoute.Grid && grid != null) ec.Parameters[ResourceUseKeys.WalkGrid] = spaceId(grid);
            if (route == WalkRoute.Matrix && matrix != null) ec.Parameters[ResourceUseKeys.WalkMatrix] = spaceId(matrix);
        }
    }
}
