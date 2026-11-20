// No Unity reference here: the standalone suite extractiontests compiles this file with the engine alone, so the rules
// the extraction applies to a wrapper's settings are tested headless. Keep it free of UnityEngine / UnityEditor.
using System;
using System.Collections.Generic;
using SimuLean.Serialization;
using SimuLean.Storage;

namespace SimuLean.Unity
{
    /// <summary>Industry storage element presets built by <c>UnityStorageRack</c>.</summary>
    public enum StorageRackStyle
    {
        /// <summary>Selective pallet racking: bays × slots × levels, depth 1, every position directly accessible.</summary>
        ConventionalRack = 0,

        /// <summary>Ground grid of bays × depth cells; each cell is a LIFO stack of up to stackHeight units.</summary>
        FloorStorage = 1,

        /// <summary>Drive-in (DIN) rack: deep LIFO lanes, one SKU per lane.</summary>
        DriveInRack = 2,

        /// <summary>Push-back rack: shallow LIFO lanes (typically 2–5 deep), one SKU per lane.</summary>
        PushBackRack = 3,

        /// <summary>
        /// Carton flow rack (live storage for picking boxes): inclined FIFO lanes, one SKU per lane (the engine's
        /// FlowRack preset). Boxes are loaded at the rear and picked at the anchor face (z = 0).
        /// </summary>
        CartonFlowRack = 4,
    }

    /// <summary>SKU-mixing override for generated lanes.</summary>
    public enum RackSkuMix { StyleDefault, SingleSku, Mixed }

    /// <summary>Access override for generated lanes, e.g. Direct for cases on a pallet position (any one can be
    /// picked from the top, no relocations).</summary>
    public enum RackAccessOverride { StyleDefault, Direct, Fifo, Lifo }

    /// <summary>How a team's members travel: in straight lines between the elements (the default), no position (travel takes
    /// no time), on the transport network, by a distance matrix, by axes, around obstacles on a walk grid or with Unity
    /// physics. (Straight was added last so the scenes' saved values keep their meaning.)</summary>
    public enum TeamTravel { Abstract, Network, Matrix, Axes, Grid, UnityPhysics, Straight }

    /// <summary>Unity Physics travel: the model that estimates distances (nearest rules, calibration, headless).</summary>
    public enum PhysicsEstimate { Grid, Matrix, Network }

    /// <summary>
    /// The decisions the extraction makes from a wrapper's settings, as pure functions of plain values: a rack style's lane
    /// semantics (<c>UnityStorageRack</c>), a resource team's travel (<c>UnityResourceTeamExtractor</c>) and a
    /// Combiner's input order (<c>UnityCombiner</c>). The wrappers and extractors gather the values from the scene and
    /// call these.
    /// </summary>
    public static class ExtractionRules
    {
        // ── storage rack ────────────────────────────────────────────────

        /// <summary>
        /// The engine lane semantics of a rack style, with its overrides: conventional racking is direct (LIFO when a lane
        /// holds more than one unit), drive-in / push-back LIFO single-SKU, carton flow FIFO single-SKU, floor storage LIFO
        /// mixed; <paramref name="skuMix"/> and <paramref name="accessOverride"/> replace the style's choice.
        /// <paramref name="warning"/> is the text of a multi-deep conventional rack left at the style's defaults (null
        /// otherwise).
        /// </summary>
        public static void RackSemantics(StorageRackStyle style, int laneCapacity, RackAccessOverride accessOverride, RackSkuMix skuMix,
                                         out AccessDiscipline access, out SkuConstraint sku, out string warning)
        {
            warning = null;
            switch (style)
            {
                case StorageRackStyle.ConventionalRack:
                    if (laneCapacity > 1)
                    {
                        if (accessOverride == RackAccessOverride.StyleDefault && skuMix != RackSkuMix.SingleSku)
                            warning = $"Conventional racking is single-deep; {laneCapacity} units per lane " +
                                      "make the lanes LIFO (multi-deep behaviour, relocations apply).";
                        access = AccessDiscipline.Lifo;
                    }
                    else
                    {
                        access = AccessDiscipline.Direct;
                    }
                    sku = SkuConstraint.Mixed;
                    break;

                case StorageRackStyle.DriveInRack:
                case StorageRackStyle.PushBackRack:
                    access = AccessDiscipline.Lifo;
                    sku = SkuConstraint.SingleSku;
                    break;

                case StorageRackStyle.CartonFlowRack:   // StoragePresets.FlowRack
                    access = AccessDiscipline.Fifo;
                    sku = SkuConstraint.SingleSku;
                    break;

                default: // FloorStorage
                    access = AccessDiscipline.Lifo;
                    sku = SkuConstraint.Mixed;
                    break;
            }

            if (skuMix == RackSkuMix.SingleSku) sku = SkuConstraint.SingleSku;
            else if (skuMix == RackSkuMix.Mixed) sku = SkuConstraint.Mixed;
            if (accessOverride == RackAccessOverride.Direct) access = AccessDiscipline.Direct;
            else if (accessOverride == RackAccessOverride.Fifo) access = AccessDiscipline.Fifo;
            else if (accessOverride == RackAccessOverride.Lifo) access = AccessDiscipline.Lifo;
        }

        // ── resource team ───────────────────────────────────────────────

        /// <summary>The engine's travel of a scene travel mode (Unity physics: timed outside the engine).</summary>
        public static TravelKind EngineTravel(TeamTravel t)
        {
            switch (t)
            {
                case TeamTravel.Network: return TravelKind.Network;
                case TeamTravel.Matrix: return TravelKind.Matrix;
                case TeamTravel.Axes: return TravelKind.Axes;
                case TeamTravel.Grid: return TravelKind.Grid;
                case TeamTravel.Straight: return TravelKind.Straight;
                case TeamTravel.UnityPhysics: return TravelKind.External;
                default: return TravelKind.Abstract;
            }
        }

        /// <summary>The engine's travel of a Unity-physics team's estimate model.</summary>
        public static TravelKind EstimateTravel(PhysicsEstimate e)
            => e == PhysicsEstimate.Grid ? TravelKind.Grid
             : e == PhysicsEstimate.Matrix ? TravelKind.Matrix : TravelKind.Network;

        /// <summary>True when the team's travel reads its distance matrix (Matrix, or Unity physics estimated by one).</summary>
        public static bool UsesMatrix(TeamTravel travel, PhysicsEstimate estimate)
            => travel == TeamTravel.Matrix || (travel == TeamTravel.UnityPhysics && estimate == PhysicsEstimate.Matrix);

        /// <summary>True when the team's travel reads its walk grid (Grid, or Unity physics estimated by one).</summary>
        public static bool UsesWalkGrid(TeamTravel travel, PhysicsEstimate estimate)
            => travel == TeamTravel.Grid || (travel == TeamTravel.UnityPhysics && estimate == PhysicsEstimate.Grid);

        /// <summary>The point a Straight team without a home node starts at (the team's position; the extractor adds it).</summary>
        public static string AutoHomeId(string teamId) => "home:" + teamId;

        /// <summary>The home node of a team: none for an abstract team, else its node (<paramref name="homeNodeId"/>, null when
        /// it has none) or, for a Straight team without one, its own position (<see cref="AutoHomeId"/>).</summary>
        public static string HomeNode(TeamTravel travel, string homeNodeId, string teamId)
            => travel == TeamTravel.Abstract ? null : homeNodeId != null ? homeNodeId
             : travel == TeamTravel.Straight ? AutoHomeId(teamId) : null;

        /// <summary>A member's name: its own, else <c>Team_k</c> (k = its 1-based place).</summary>
        public static string MemberName(string name, string teamName, int index)
            => string.IsNullOrEmpty(name) ? $"{teamName}_{index + 1}" : name;

        // ── combiner ────────────────────────────────────────────────────

        /// <summary>
        /// A Combiner's inputs in order ([0] = main, [k] = component input k − 1): the <paramref name="listed"/> entries that
        /// really feed it (in <paramref name="upstream"/>), each once, then the other upstream elements by
        /// <paramref name="hierarchyKey"/> (ordinal).
        /// </summary>
        public static List<T> InputOrder<T>(List<T> upstream, IEnumerable<T> listed, Func<T, string> hierarchyKey) where T : class
        {
            var ordered = new List<T>();
            if (listed != null)
                foreach (T e in listed)
                    if (e != null && upstream.Contains(e) && !ordered.Contains(e)) ordered.Add(e);

            var rest = upstream.FindAll(e => !ordered.Contains(e));
            rest.Sort((a, b) => string.CompareOrdinal(hierarchyKey(a), hierarchyKey(b)));
            ordered.AddRange(rest);
            return ordered;
        }

        /// <summary>
        /// Every input of a Combiner that needs <paramref name="needed"/> of them ([0] = main): the connected element feeding
        /// it, or null when a list feeds it (<paramref name="listFeeds"/>) or nothing does. Connections fill the inputs no
        /// list takes, in order; extra connections are appended so the count check can report them.
        /// </summary>
        public static List<T> InputSlots<T>(List<T> connected, int needed, Func<int, bool> listFeeds) where T : class
        {
            var slots = new List<T>();
            int next = 0;
            for (int i = 0; i < needed || next < connected.Count; i++)
            {
                if (listFeeds(i)) slots.Add(null);
                else if (next < connected.Count) slots.Add(connected[next++]);
                else if (i < needed) slots.Add(null);   // missing input (reported by the count check)
                else break;
            }
            return slots;
        }
    }
}
