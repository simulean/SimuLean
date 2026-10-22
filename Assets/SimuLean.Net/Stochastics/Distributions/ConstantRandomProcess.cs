namespace SimuLean
{
    /// <summary>A sampler that always returns the same value.</summary>
    public class ConstantRandomProcess : IRandomSampler
    {
        private readonly double constant;

        public ConstantRandomProcess(double constant)
        {
            this.constant = constant;
        }

        public double NextValue() => constant;
    }
}
