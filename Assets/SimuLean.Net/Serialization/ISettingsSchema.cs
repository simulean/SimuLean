using System;

namespace SimuLean.Serialization
{
    /// <summary>
    /// What a settings class may say about its own parameters beyond its attributes (<see cref="ElementSettings.Refine"/>):
    /// the generated parameter schema (<c>SimuLean.Optimization.ParameterSchema</c>) implements it, so the settings do
    /// not depend on the optimisation layer that reads them.
    /// </summary>
    public interface ISettingsSchema
    {
        /// <summary>The key is a choice among the names of <typeparamref name="T"/>.</summary>
        void Choice<T>(string key) where T : struct, Enum;
    }
}
