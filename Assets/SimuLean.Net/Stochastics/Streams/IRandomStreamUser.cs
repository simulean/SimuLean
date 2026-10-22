namespace SimuLean.Stochastics.Streams
{
    /// <summary>
    /// A sampler that draws random numbers and accepts its own random stream
    /// (<see cref="SimuLean.SimContext.CreateRandomStream(string, string)"/>). Implemented by the
    /// distribution bases and the discrete process strategies; deterministic samplers
    /// (constants, label expressions) do not implement it.
    /// </summary>
    public interface IRandomStreamUser
    {
        /// <summary>Replaces the random engine the sampler draws from.</summary>
        void SetRandomEngine(IRandomEngine engine);
    }
}
