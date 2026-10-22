namespace SimuLean.Storage
{
    /// <summary>
    /// Factory for the industry storage types of the proposal (§3): each type
    /// is the single parametric <see cref="StorageLane"/> with a preset
    /// combination of access discipline, SKU constraint and depth.
    ///
    /// | Type            | Access | SKU rule  | Relocation?              |
    /// |-----------------|--------|-----------|--------------------------|
    /// | Selective       | Direct | Mixed     | never                    |
    /// | Multi-deep      | Lifo   | SingleSku | reaching a non-front unit|
    /// | Drive-in        | Lifo   | SingleSku | reaching a specific unit |
    /// | Push-back       | Lifo   | SingleSku | rare (small depth)       |
    /// | Floor block     | Lifo   | Mixed     | the main case            |
    /// | Flow rack       | Fifo   | SingleSku | never                    |
    /// </summary>
    public static class StoragePresets
    {
        /// <summary>
        /// Selective (single-depth) racking: every position directly
        /// accessible, mixed SKUs, no relocation ever. <paramref name="capacity"/>
        /// is the number of independently accessible positions grouped in this lane.
        /// </summary>
        public static StorageLane Selective(string id, Coordinate coordinate, int capacity)
            => new StorageLane(id, coordinate, capacity, AccessDiscipline.Direct, SkuConstraint.Mixed);

        /// <summary>
        /// Multi-depth racking: one access face (LIFO), usually one SKU per
        /// lane; reaching a non-front unit requires relocation.
        /// </summary>
        public static StorageLane MultiDeep(string id, Coordinate coordinate, int depth)
            => new StorageLane(id, coordinate, depth, AccessDiscipline.Lifo, SkuConstraint.SingleSku);

        /// <summary>
        /// Drive-in (DIN) rack: deep LIFO lane, single SKU per lane;
        /// relocation needed to reach a specific unit (e.g. FEFO).
        /// </summary>
        public static StorageLane DriveIn(string id, Coordinate coordinate, int depth)
            => new StorageLane(id, coordinate, depth, AccessDiscipline.Lifo, SkuConstraint.SingleSku);

        /// <summary>
        /// Push-back rack: gravity LIFO lane of small depth, single SKU per
        /// lane; relocation is rare in practice.
        /// </summary>
        public static StorageLane PushBack(string id, Coordinate coordinate, int depth)
            => new StorageLane(id, coordinate, depth, AccessDiscipline.Lifo, SkuConstraint.SingleSku);

        /// <summary>
        /// Floor / block stacking: LIFO pile with mixed SKUs — the headline
        /// relocation case. <paramref name="capacity"/> = stack height × footprint.
        /// </summary>
        public static StorageLane FloorBlock(string id, Coordinate coordinate, int capacity)
            => new StorageLane(id, coordinate, capacity, AccessDiscipline.Lifo, SkuConstraint.Mixed);

        /// <summary>
        /// Gravity flow rack: separate input/output faces (FIFO), single SKU
        /// per lane, no relocation.
        /// </summary>
        public static StorageLane FlowRack(string id, Coordinate coordinate, int depth)
            => new StorageLane(id, coordinate, depth, AccessDiscipline.Fifo, SkuConstraint.SingleSku);
    }
}
