using System.Collections.Generic;
using SimuLean.Unity;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Extraction side of an element type (Unity scene → <see cref="ElementConfig"/>), one per wrapper type.
    /// The build side (config → headless element) is the engine's <see cref="IElementHandler"/> with the
    /// same <see cref="TypeName"/>; the engine assembly has no Unity reference, so the two halves live in
    /// different assemblies.
    /// </summary>
    public interface IElementExtractor
    {
        /// <summary>Matches <c>SElement.GetType().Name</c>, <see cref="ElementConfig.Type"/> and the handler's TypeName.</summary>
        string TypeName { get; }

        /// <summary>Pass 1: reads the component's fields into <c>config.Parameters</c>.</summary>
        void ExtractParameters(SElement element, ElementConfig config);

        /// <summary>
        /// Pass 2: appends non-standard connections (the generic nextElements → "General" connections are
        /// added by the extractor). Override only for additional wiring (e.g. station material sources).
        /// </summary>
        void ExtractConnections(SElement element, string elementId, List<ConnectionConfig> connections,
                                Dictionary<SElement, string> ids);
    }

    /// <summary>Base class with a no-op <see cref="ExtractConnections"/>.</summary>
    public abstract class ElementExtractorBase : IElementExtractor
    {
        public abstract string TypeName { get; }
        public abstract void ExtractParameters(SElement element, ElementConfig config);
        public virtual void ExtractConnections(SElement element, string elementId, List<ConnectionConfig> connections,
                                               Dictionary<SElement, string> ids) { }
    }

    /// <summary>All extractors by type name; <see cref="CreateDefault"/> holds the built-in ones.</summary>
    public class ElementExtractorRegistry
    {
        private readonly Dictionary<string, IElementExtractor> extractors = new Dictionary<string, IElementExtractor>();

        public void Register(IElementExtractor extractor) => extractors[extractor.TypeName] = extractor;
        public void RegisterAlias(string alias, IElementExtractor extractor) => extractors[alias] = extractor;
        public bool TryGetExtractor(string typeName, out IElementExtractor extractor) => extractors.TryGetValue(typeName, out extractor);

        public static ElementExtractorRegistry CreateDefault()
        {
            var r = new ElementExtractorRegistry();
            r.Register(new UnitySourceExtractor());
            r.Register(new UnityQueueExtractor());
            r.Register(new UnityLengthLimitedQueueExtractor());
            r.Register(new UnityStackerExtractor());
            r.Register(new UnityGateQueueExtractor());
            r.Register(new UnityMultiServerExtractor());
            r.Register(new UnityDelayExtractor());
            r.Register(new UnityAgendaExtractor());
            r.Register(new UnityCombinerExtractor());
            r.Register(new UnitySinkExtractor());
            var storage = new UnityStorageSystemExtractor();
            r.Register(storage);
            r.RegisterAlias("UnityStorageRack", storage);   // parametric rack/floor subclass
            r.Register(new UnityPickingStationExtractor());
            r.Register(new UnityAssemblyStationExtractor());
            r.Register(new UnityErectionDockExtractor());   // naval library (N2)
            return r;
        }
    }
}
