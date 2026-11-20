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
        /// <summary>The engine type it extracts to (<see cref="ElementConfig.Type"/>, the handler's TypeName: <c>Queue</c>,
        /// <c>MultiServer</c>…; the engine names, not the wrapper's).</summary>
        string TypeName { get; }

        /// <summary>The wrapper component it reads (<c>SElement.GetType().Name</c>: <c>UnityQueue</c>…), its key in the registry.</summary>
        string ComponentName { get; }

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
    /// <summary>
    /// Optional third pass of an extractor: parameters that name other elements by their config id (e.g. a milk
    /// run's flows), written once every element of the scene has its id.
    /// </summary>
    public interface IElementReferenceExtractor
    {
        void ExtractReferences(SElement element, ElementConfig config, IReadOnlyDictionary<SElement, string> ids);
    }

    public abstract class ElementExtractorBase : IElementExtractor
    {
        public abstract string TypeName { get; }
        /// <summary>Default: <c>Unity</c> + the engine type name (the wrapper's class name).</summary>
        public virtual string ComponentName => "Unity" + TypeName;
        public abstract void ExtractParameters(SElement element, ElementConfig config);
        public virtual void ExtractConnections(SElement element, string elementId, List<ConnectionConfig> connections,
                                               Dictionary<SElement, string> ids) { }
    }

    /// <summary>All extractors by component name (the wrapper class); <see cref="CreateDefault"/> holds the built-in ones.</summary>
    public class ElementExtractorRegistry
    {
        private readonly Dictionary<string, IElementExtractor> extractors = new Dictionary<string, IElementExtractor>();

        public void Register(IElementExtractor extractor) => extractors[extractor.ComponentName] = extractor;
        public void RegisterAlias(string alias, IElementExtractor extractor) => extractors[alias] = extractor;
        public bool TryGetExtractor(string componentName, out IElementExtractor extractor) => extractors.TryGetValue(componentName, out extractor);

        /// <summary>The engine type of a wrapper component (its extractor's <see cref="IElementExtractor.TypeName"/>; the
        /// component's class name when no extractor reads it).</summary>
        public string EngineTypeOf(string componentName)
            => extractors.TryGetValue(componentName, out var x) ? x.TypeName : componentName;

        public static ElementExtractorRegistry CreateDefault()
        {
            var r = new ElementExtractorRegistry();
            r.Register(new UnitySourceExtractor());
            r.Register(new UnityQueueExtractor());
            r.Register(new UnityLengthLimitedQueueExtractor());
            r.Register(new UnityStackerExtractor());
            r.Register(new UnityMultiServerExtractor());
            r.Register(new UnityDelayExtractor());
            r.Register(new UnityAgendaExtractor());
            r.Register(new UnityCombinerExtractor());
            r.Register(new UnitySinkExtractor());
            r.Register(new UnitySeparatorExtractor());
            r.Register(new UnityLineSideUnpackerExtractor());
            r.Register(new UnityPickFaceExtractor());   // Warehousing pack
            r.Register(new UnityBaseStockReplenisherExtractor());
            r.Register(new UnityMilkRunExtractor());
            r.Register(new UnityCallOffPointExtractor());   // line supply (Manufacturing pack)
            r.Register(new UnityPacedLineExtractor());
            r.Register(new UnitySequencingStationExtractor());
            var storage = new UnityStorageSystemExtractor();
            r.Register(storage);
            r.RegisterAlias("UnityStorageRack", storage);   // parametric rack/floor subclass of the storage component
            r.Register(new UnityPickingStationExtractor());
            r.Register(new UnityAssemblyStationExtractor());
            r.Register(new UnityErectionDockExtractor());   // naval library
            return r;
        }
    }
}
