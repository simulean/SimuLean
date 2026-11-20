using System.Collections.Generic;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Holds all registered element handlers and dispatches extraction/build operations.
    /// Call <see cref="CreateDefault"/> to obtain a registry pre-loaded with all built-in handlers.
    /// </summary>
    public class ElementHandlerRegistry
    {
        private readonly Dictionary<string, IElementHandler> handlers =
            new Dictionary<string, IElementHandler>();

        /// <summary>Registers a handler; overwrites any existing handler for the same TypeName.</summary>
        public void Register(IElementHandler handler) => handlers[handler.TypeName] = handler;

        /// <summary>Registers a handler under an additional alias (e.g. a wrapper subclass sharing a handler).</summary>
        public void RegisterAlias(string alias, IElementHandler handler) => handlers[alias] = handler;

        /// <summary>Returns true and sets handler if a handler is registered for typeName.</summary>
        public bool TryGetHandler(string typeName, out IElementHandler handler) =>
            handlers.TryGetValue(typeName, out handler);

        /// <summary>Returns a registry pre-loaded with all built-in element handlers.</summary>
        public static ElementHandlerRegistry CreateDefault()
        {
            var r = new ElementHandlerRegistry();

            r.Register(new SourceHandler());

            r.Register(new QueueHandler());
            r.Register(new LengthLimitedQueueHandler());
            r.Register(new StackerHandler());
            r.Register(new MultiServerHandler());
            r.Register(new DelayHandler());
            r.Register(new AgendaHandler());
            r.Register(new CombinerHandler());
            r.Register(new SinkHandler());
            r.Register(new SeparatorHandler());
            r.Register(new LineSideUnpackerHandler());
            r.Register(new PickFaceHandler());   // Warehousing pack
            r.Register(new BaseStockReplenisherHandler());
            r.Register(new MilkRunHandler());
            r.Register(new CallOffPointHandler());   // line supply (Manufacturing pack)
            r.Register(new PacedLineHandler());
            r.Register(new SequencingStationHandler());

            // Transport & storage subsystems
            var storage = new StorageSystemHandler();
            r.Register(storage);

            // Kitting / assembly stations
            r.Register(new PickingStationHandler());
            r.Register(new AssemblyStationHandler());

            // Naval library
            r.Register(new ErectionDockHandler());

            return r;
        }
    }

}
