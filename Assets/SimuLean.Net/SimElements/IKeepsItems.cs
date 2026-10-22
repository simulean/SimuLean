namespace SimuLean
{
    /// <summary>
    /// An element that keeps items on purpose: a sink with <see cref="Sink.KeepItems"/>, an erection
    /// dock's hull. Its kept items are at rest — the run monitor does not count them as waiting — and the time the last
    /// one came to rest ends the work for the model's makespan (an erection ends after the block's arrival).
    /// </summary>
    public interface IKeepsItems
    {
        /// <summary>Items kept now (at rest).</summary>
        int KeptCount { get; }

        /// <summary>When the last item came to rest (NaN: none yet).</summary>
        double LastKeptTime { get; }
    }
}
