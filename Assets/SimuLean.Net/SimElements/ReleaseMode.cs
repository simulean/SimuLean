namespace SimuLean
{
    /// <summary>
    /// How a holding element (Queue, Multi-Server) offers its items downstream: the receiver
    /// decides which item it takes (its input strategy — a Combiner's pull mode, a condition), the holder only offers.
    /// </summary>
    public enum ReleaseMode
    {
        /// <summary>Only the first item (oldest) is offered; a refused first item holds the rest back (default).</summary>
        FIFO,
        /// <summary>Each held item is offered, oldest first (up to the release scan), until a receiver accepts one — e.g. a
        /// yard handing each assembly bay the sub-block of its own block.</summary>
        AnyAccepted,
    }
}
