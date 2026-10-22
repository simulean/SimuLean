namespace SimuLean
{
    /// <summary>
    /// Default interface for a distribution class that returns double values.
    /// </summary>
    public interface IRandomSampler
    {

        /// <summary>
        /// Returns a new double value.
        /// </summary>
        /// <returns></returns>
        double NextValue();
    }
}
