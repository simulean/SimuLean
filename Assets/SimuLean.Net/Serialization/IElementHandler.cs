using System.Collections.Generic;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Self-registering element handler — one implementation per Unity element type.
    /// Encapsulates both extraction (Unity scene → ElementConfig) and
    /// construction (ElementConfig → headless Element) for a single type.
    /// Adding a new simulation element type requires only one new class;
    /// no existing files need to change.
    /// </summary>
    public interface IElementHandler
    {
        /// <summary>
        /// Matches SElement.GetType().Name and ElementConfig.Type.
        /// </summary>
        string TypeName { get; }



        /// <summary>
        /// Build: create and return the headless simulation element from the config.
        /// </summary>
        Element Build(ElementConfig config, SimClock clock, IVElement vElement);

        /// <summary>
        /// Post-build hook for element-specific wiring that cannot be expressed as
        /// ConnectionConfig entries (e.g. internal cross-references).
        /// No-op for most elements.
        /// </summary>
        void Wire(Element element, ElementConfig config,
                  IReadOnlyDictionary<string, Element> created);

        /// <summary>
        /// What this type's config keys mean as parameters (<see cref="SimuLean.Optimization.ParameterSchema"/>): numbers,
        /// bools and number arrays are found by their type; texts and lists are declared — an option, a sampler spec, a
        /// list of numbers, or structural. The catalog (<see cref="SimuLean.Optimization.ModelParameterCatalog"/>) offers
        /// every parameter to the optimiser, the Experimenter and the editors; a test fails for an undeclared key.
        /// </summary>
        SimuLean.Optimization.ParameterSchema Schema { get; }
    }

    /// <summary>
    /// Abstract base class providing default no-op implementations for
    /// ExtractConnections and Wire so concrete handlers only override what they need.
    /// </summary>
    public abstract class ElementHandlerBase : IElementHandler
    {
        public abstract string TypeName { get; }



        public abstract Element Build(ElementConfig config, SimClock clock, IVElement vElement);

        public virtual void Wire(Element element, ElementConfig config,
                                 IReadOnlyDictionary<string, Element> created) { }

        public virtual SimuLean.Optimization.ParameterSchema Schema => null;
    }
}
