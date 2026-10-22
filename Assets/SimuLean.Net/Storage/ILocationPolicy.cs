using System;
using System.Collections.Generic;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Storage
{
    /// <summary>
    /// Read-only context handed to location/relocation policies, mirroring the
    /// existing <c>OutputStrategyContext</c> pattern.
    /// </summary>
    public class LocationContext
    {
        /// <summary>The storage system asking for a decision.</summary>
        public StorageSystem System { get; set; }

        /// <summary>The model clock (current time, event scheduling).</summary>
        public SimClock Clock { get; set; }

        /// <summary>
        /// Factory for reproducible random streams
        /// (<c>SimContext.CreateRandomStream(name, "locationPolicy")</c>). Policies that need
        /// randomness must create their stream ONCE through this and reuse it.
        /// </summary>
        public Func<IRandomEngine> RandomStream { get; set; }

        /// <summary>Reference point (e.g. inbound dock) for distance-based policies.</summary>
        public double RefX { get; set; }
        public double RefY { get; set; }
        public double RefZ { get; set; }
    }

    /// <summary>
    /// Slotting decision for put-away: pick the lane a new item goes to.
    /// <c>candidates</c> is pre-filtered to lanes whose <c>CanAccept</c> is
    /// true, in lane-declaration order. Return null to reject (the item waits
    /// upstream).
    /// </summary>
    public interface ILocationPolicy
    {
        StorageLane SelectLane(IReadOnlyList<StorageLane> candidates, Item item, LocationContext ctx);
    }

    /// <summary>
    /// Slotting decision for a blocker being re-homed during a retrieval.
    /// Same shape as <see cref="ILocationPolicy"/>; the caller excludes the
    /// lane currently holding the blocker from <c>candidates</c>. Return null
    /// to declare the blocker unplaceable (the retrieval fails).
    /// </summary>
    public interface IRelocationPolicy
    {
        StorageLane SelectLane(IReadOnlyList<StorageLane> candidates, Item item, LocationContext ctx);
    }

    /// <summary>
    /// Convenience base for custom policies: handles the empty-candidate case
    /// and implements both the location and relocation interfaces so any
    /// policy can serve either role.
    /// </summary>
    public abstract class LocationPolicyBase : ILocationPolicy, IRelocationPolicy
    {
        public StorageLane SelectLane(IReadOnlyList<StorageLane> candidates, Item item, LocationContext ctx)
        {
            if (candidates == null || candidates.Count == 0)
                return null;
            return SelectLaneCore(candidates, item, ctx);
        }

        /// <summary>Core decision; called only with a non-empty candidate list.</summary>
        protected abstract StorageLane SelectLaneCore(IReadOnlyList<StorageLane> candidates, Item item, LocationContext ctx);
    }

    /// <summary>Default policy: the first acceptable lane in declaration order.</summary>
    public class FirstAvailablePolicy : LocationPolicyBase
    {
        protected override StorageLane SelectLaneCore(IReadOnlyList<StorageLane> candidates, Item item, LocationContext ctx)
        {
            return candidates[0];
        }
    }

    /// <summary>
    /// Picks the lane closest (euclidean) to the context reference point
    /// (e.g. the inbound dock). Deterministic: ties broken by LaneId ordinal.
    /// </summary>
    public class ClosestSpotPolicy : LocationPolicyBase
    {
        protected override StorageLane SelectLaneCore(IReadOnlyList<StorageLane> candidates, Item item, LocationContext ctx)
        {
            var reference = new Coordinate(ctx.RefX, ctx.RefY, ctx.RefZ);
            StorageLane best = null;
            double bestDistance = double.PositiveInfinity;

            foreach (StorageLane lane in candidates)
            {
                double distance = lane.Coordinate.DistanceTo(reference);
                if (distance < bestDistance ||
                    (distance == bestDistance && best != null &&
                     string.CompareOrdinal(lane.LaneId, best.LaneId) < 0))
                {
                    bestDistance = distance;
                    best = lane;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Picks a uniformly random acceptable lane. The random stream is created
    /// ONCE from <see cref="LocationContext.RandomStream"/> at first use, so
    /// runs with a seeded <see cref="SimClock"/> are reproducible.
    /// </summary>
    public class RandomPolicy : LocationPolicyBase
    {
        private IRandomEngine engine;

        protected override StorageLane SelectLaneCore(IReadOnlyList<StorageLane> candidates, Item item, LocationContext ctx)
        {
            if (engine == null)
                engine = ctx.RandomStream();
            return candidates[engine.NextInt(0, candidates.Count)];
        }
    }

    /// <summary>
    /// Routes each SKU to its dedicated lane; unmapped SKUs (or a mapped lane
    /// that is not currently acceptable) fall back to another policy
    /// (default <see cref="FirstAvailablePolicy"/>).
    /// </summary>
    public class DedicatedBySkuPolicy : LocationPolicyBase
    {
        private readonly Dictionary<string, string> skuToLaneId;
        private readonly ILocationPolicy fallback;

        public DedicatedBySkuPolicy(Dictionary<string, string> skuToLaneId = null,
                                    ILocationPolicy fallback = null)
        {
            this.skuToLaneId = skuToLaneId ?? new Dictionary<string, string>();
            this.fallback = fallback ?? new FirstAvailablePolicy();
        }

        /// <summary>Maps (or remaps) a SKU to a lane id.</summary>
        public void Map(string sku, string laneId)
        {
            skuToLaneId[sku] = laneId;
        }

        protected override StorageLane SelectLaneCore(IReadOnlyList<StorageLane> candidates, Item item, LocationContext ctx)
        {
            string sku = item.GetTypeName();
            string laneId;
            if (sku != null && skuToLaneId.TryGetValue(sku, out laneId))
            {
                foreach (StorageLane lane in candidates)
                    if (lane.LaneId == laneId)
                        return lane;
            }
            return fallback.SelectLane(candidates, item, ctx);
        }
    }
}
