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

            r.Register(new UnitySourceHandler());

            r.Register(new UnityQueueHandler());
            r.Register(new UnityLengthLimitedQueueHandler());
            r.Register(new UnityStackerHandler());
            r.Register(new UnityGateQueueHandler());
            r.Register(new UnityMultiServerHandler());
            r.Register(new UnityDelayHandler());
            r.Register(new UnityAgendaHandler());
            r.Register(new UnityCombinerHandler());
            r.Register(new UnitySinkHandler());

            // Transport & storage subsystems (P4/S5)
            var storage = new UnityStorageSystemHandler();
            r.Register(storage);
            r.RegisterAlias("UnityStorageRack", storage);   // parametric rack/floor subclass

            // Kitting / assembly stations (Task 0-D)
            r.Register(new UnityPickingStationHandler());
            r.Register(new UnityAssemblyStationHandler());

            // Naval library (N2)
            r.Register(new UnityErectionDockHandler());

            return r;
        }
    }

}
