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

        // ── Build hooks: what the model factory (HeadlessModelFactory) asks of a type at its passes, so the factory
        //    branches on no element type. The defaults are those of most types.

        /// <summary>Pass 1a, before the setup and process resources: the element's own use of a transport team (a
        /// storage's moves, a milk run's trains). False = the type has no setup or process resources (not read).</summary>
        internal virtual bool ConfigureTransport(Element element, ElementConfig config,
            IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> teams, SimClock clock) => true;

        /// <summary>Pass 1a, once the process use is built (null = none): what the type adds to it.</summary>
        internal virtual void ConfigureProcessUse(Element element, ElementConfig config,
            IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> teams, SimuLean.Resources.StationResources use) { }

        /// <summary>Its jobs may secure their resources before the part (<c>processBy.acquireBeforeEntry</c>).</summary>
        internal virtual bool JobsAcquireBeforeEntry => false;

        /// <summary>A job that gives up its wait may send its item elsewhere (<c>processBy.onTimeout = Reroute</c>).</summary>
        internal virtual bool JobsReroute => false;

        /// <summary>Pass 1b, before the links are created.</summary>
        internal virtual void PrepareWiring(Element element, ElementConfig config, IReadOnlyDictionary<string, Element> created) { }

        /// <summary>The config's input strategy applies to the element (false: it admits its items its own way).</summary>
        internal virtual bool AppliesInputStrategy => true;

        /// <summary>The element that pulls from a list for its input <paramref name="input"/> (a PullFromList connection's
        /// <c>Input</c>); null = the type has no numbered inputs (the element itself pulls).</summary>
        internal virtual Element InputPort(Element element, int input) => null;

        /// <summary>The port a material feed (a StockInput connection) delivers to; null = the type has none.</summary>
        internal virtual SkuStockPort StockPort(Element element) => null;

        /// <summary>The port of its returns (a ReturnsOutput connection); null = the type has none.</summary>
        internal virtual Element ReturnsPort(Element element) => null;

        /// <summary>Its items pushed to a list are carried to the pullers by its transport team (false: it carries them
        /// itself).</summary>
        internal virtual bool ListPushesByTransport => true;
    }
}
