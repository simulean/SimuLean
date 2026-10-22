using System;
using System.Collections.Generic;
using SimuLean.Collections;

namespace SimuLean.Storage
{
    /// <summary>
    /// How units inside a lane can be reached.
    /// </summary>
    public enum AccessDiscipline
    {
        /// <summary>Every position is directly accessible (selective racking). No blockers ever.</summary>
        Direct,

        /// <summary>Single access face; last unit in is first out (multi-deep, drive-in,
        /// push-back, floor block). Retrieving a deeper unit requires relocating the
        /// units in front of it.</summary>
        Lifo,

        /// <summary>Separate input and output faces; first unit in is first out
        /// (gravity flow rack).</summary>
        Fifo
    }

    /// <summary>
    /// Which SKU mixes a lane tolerates.
    /// </summary>
    public enum SkuConstraint
    {
        /// <summary>The lane holds one SKU at a time (empty lanes accept any SKU).</summary>
        SingleSku,

        /// <summary>Any SKU mix is allowed.</summary>
        Mixed
    }

    /// <summary>
    /// An abstract 3D position used for travel-time / closest-spot computations.
    /// Engine-side and Unity-free; the Unity wrapper populates it from Transforms,
    /// headless runs read it from config.
    /// </summary>
    public struct Coordinate
    {
        public double X;
        public double Y;
        public double Z;

        public Coordinate(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>Euclidean distance to another coordinate.</summary>
        public double DistanceTo(Coordinate other)
        {
            double dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public override string ToString() => $"({X}, {Y}, {Z})";
    }

    /// <summary>
    /// The single parametric lane model (proposal §3): every industry storage
    /// type is this class with different parameters — see
    /// <see cref="StoragePresets"/>. A lane holds an ordered list of
    /// <see cref="StoredItem"/> records where index 0 is the accessible face
    /// for <see cref="AccessDiscipline.Lifo"/> lanes (and the retrieval face
    /// for <see cref="AccessDiscipline.Fifo"/> lanes); for
    /// <see cref="AccessDiscipline.Direct"/> lanes every position is accessible.
    /// </summary>
    public class StorageLane
    {
        private readonly List<StoredItem> slots = new List<StoredItem>();

        /// <summary>Unique lane id within its <see cref="StorageSystem"/>.</summary>
        public string LaneId { get; }

        /// <summary>Abstract position of the lane (closest-spot policies, travel estimation).</summary>
        public Coordinate Coordinate { get; set; }

        /// <summary>Maximum number of units the lane can hold.</summary>
        public int Capacity { get; }

        /// <summary>Access discipline of the lane.</summary>
        public AccessDiscipline AccessDiscipline { get; }

        /// <summary>SKU mixing rule of the lane.</summary>
        public SkuConstraint SkuConstraint { get; }

        /// <summary>Optional transport-network node id serving this lane.
        /// Null → moves from/to this lane are instantaneous.</summary>
        public string NodeId { get; set; }

        public StorageLane(string laneId, Coordinate coordinate, int capacity,
                           AccessDiscipline accessDiscipline, SkuConstraint skuConstraint,
                           string nodeId = null)
        {
            if (string.IsNullOrEmpty(laneId))
                throw new ArgumentException("Lane id required.", nameof(laneId));
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be > 0.", nameof(capacity));

            LaneId = laneId;
            Coordinate = coordinate;
            Capacity = capacity;
            AccessDiscipline = accessDiscipline;
            SkuConstraint = skuConstraint;
            NodeId = nodeId;
        }

        /// <summary>Units currently in the lane.</summary>
        public int Count => slots.Count;

        /// <summary>Free positions remaining.</summary>
        public int FreeCapacity => Capacity - slots.Count - Reserved;

        /// <summary>Places promised to blockers on their way here (a retrieval's planned relocations): not free for
        /// anyone else until they arrive.</summary>
        public int Reserved { get; private set; }

        /// <summary>The SKU a reservation pins on an empty single-SKU lane (null = none).</summary>
        internal string ReservedSku { get; private set; }

        /// <summary>Retrievals in progress from this lane: no put-away goes in while it is being emptied (an item placed
        /// at the face would land in front of the target).</summary>
        public int RetrievalsInProgress { get; internal set; }

        internal void Reserve(Item item)
        {
            if (Reserved == 0 && slots.Count == 0 && SkuConstraint == SkuConstraint.SingleSku) ReservedSku = item.GetTypeName();
            Reserved++;
        }

        // A reserved place is taken by the item it was kept for.
        internal void PlaceReserved(StoredItem record)
        {
            if (Reserved > 0 && --Reserved == 0) ReservedSku = null;
            Place(record);
        }

        /// <summary>Ordered contents; index 0 = accessible/retrieval face. Live view — do not mutate.</summary>
        public IReadOnlyList<StoredItem> Contents => slots;

        /// <summary>
        /// True when the lane has a free position and the item satisfies the
        /// SKU constraint (SingleSku: lane empty or same <c>GetTypeName()</c>).
        /// </summary>
        public bool CanAccept(Item item)
        {
            if (item == null || FreeCapacity <= 0 || RetrievalsInProgress > 0)
                return false;

            if (SkuConstraint == SkuConstraint.SingleSku)
            {
                string sku = slots.Count > 0 ? slots[0].Item.GetTypeName() : ReservedSku;
                if (sku != null) return sku == item.GetTypeName();
            }

            return true;
        }

        /// <summary>
        /// Stores an item at the accessible face (Lifo: push front; Fifo/Direct:
        /// append) and returns its new record. Throws if the lane cannot accept it.
        /// </summary>
        public StoredItem Store(Item item, double time)
        {
            if (!CanAccept(item))
                throw new InvalidOperationException($"Lane '{LaneId}' cannot accept item {item?.GetId()}.");

            var record = new StoredItem
            {
                Item = item,
                StoredTime = time
            };
            Place(record);
            return record;
        }

        /// <summary>
        /// Inserts an existing record at the accessible face (used for
        /// relocations, preserving the record's original StoredTime) and
        /// updates its LaneId/DepthIndex.
        /// </summary>
        internal void Place(StoredItem record)
        {
            if (FreeCapacity <= 0)
                throw new InvalidOperationException($"Lane '{LaneId}' is full.");

            if (AccessDiscipline == AccessDiscipline.Lifo)
                slots.Insert(0, record);    // newest at the access face
            else
                slots.Add(record);          // Fifo: input at back; Direct: any order

            record.LaneId = LaneId;
            Reindex();
        }

        /// <summary>
        /// The units that must be relocated before <paramref name="target"/>
        /// can be retrieved, ordered from the face inward (relocate index 0
        /// first). Direct lanes never have blockers; Lifo/Fifo lanes return the
        /// units between the retrieval face and the target.
        /// </summary>
        public List<StoredItem> GetBlockers(StoredItem target)
        {
            int index = slots.IndexOf(target);
            if (index < 0)
                throw new ArgumentException($"Item is not in lane '{LaneId}'.", nameof(target));

            var blockers = new List<StoredItem>();
            if (AccessDiscipline == AccessDiscipline.Direct)
                return blockers;

            for (int i = 0; i < index; i++)
                blockers.Add(slots[i]);
            return blockers;
        }

        /// <summary>Removes a record from the lane (reference match). Returns true if found.</summary>
        public bool Remove(StoredItem record)
        {
            int index = slots.IndexOf(record);
            if (index < 0)
                return false;
            slots.RemoveAt(index);
            record.LaneId = null;      // on its way: in no lane
            Reindex();
            return true;
        }

        /// <summary>Removes and returns the record at a depth position.</summary>
        public StoredItem RemoveAt(int index)
        {
            StoredItem record = slots[index];
            slots.RemoveAt(index);
            record.LaneId = null;
            Reindex();
            return record;
        }

        /// <summary>Empties the lane (model reset).</summary>
        public void Clear()
        {
            slots.Clear();
            Reserved = 0;
            ReservedSku = null;
            RetrievalsInProgress = 0;
        }

        private void Reindex()
        {
            for (int i = 0; i < slots.Count; i++)
                slots[i].DepthIndex = i;
        }
    }
}
