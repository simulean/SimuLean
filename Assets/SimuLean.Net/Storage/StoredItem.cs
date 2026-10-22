namespace SimuLean.Storage
{
    /// <summary>
    /// A stored item and where it sits: its lane, its depth in the lane and when it was stored. Mutated in place when
    /// the item is relocated between lanes. A <see cref="StorageSystem"/> keeps one per stored item and lists the
    /// items in its stock list (queries over <c>sku</c>, <c>depth</c>, <c>storedTime</c> and the item labels).
    /// </summary>
    public class StoredItem
    {
        /// <summary>The stored simulation item. Its SKU is <c>Item.GetTypeName()</c>.</summary>
        public Item Item { get; set; }

        /// <summary>Id of the lane currently holding the item (null while in transit).</summary>
        public string LaneId { get; set; }

        /// <summary>Position within the lane; 0 = accessible face.</summary>
        public int DepthIndex { get; set; }

        /// <summary>Simulation time at which the item first entered storage.
        /// Preserved across relocations so FIFO/LIFO age stays truthful.</summary>
        public double StoredTime { get; set; }
    }
}
