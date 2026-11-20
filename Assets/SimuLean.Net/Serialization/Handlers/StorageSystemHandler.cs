using System.Collections.Generic;
using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Handler for <see cref="StorageSystem"/> → <see cref="SimuLean.Storage.StorageSystem"/>.
    ///
    /// Lanes are extracted through the wrapper's <c>ResolveLanes()</c> (the
    /// same resolution InitializeSim uses), so scene and headless models see
    /// identical lanes. The optional transport binding travels as a
    /// "TransportSystem" connection carrying the home node id.
    /// </summary>
    internal sealed class StorageSystemHandler : ElementHandlerBase
    {
        public override string TypeName => "StorageSystem";

        // its internal moves (relocations, retrievals to its location) use its transport team; its setup and process
        // resources are read as any element's
        internal override bool ConfigureTransport(Element element, ElementConfig config,
            IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> teams, SimClock clock)
        {
            var storage = (SimuLean.Storage.StorageSystem)element;
            storage.Transport = ResourceBuild.TransportUse(config, teams, element);
            if (storage.Transport != null) ResourceBuild.ApplyStorageMoveTimes(storage, config, clock);
            // a lane without a node uses the storage's location: its moves take the travel to the rack and the
            // load / unload times — none at all without a location, or with no load / unload time either
            if (storage.Transport != null) StorageMoveWarning(storage);
            return true;
        }

        // its own retrieval carries an item pushed to a list to the puller
        internal override bool ListPushesByTransport => false;

        // a storage whose moves by its team would take no time: lanes without a node and no location;
        // or lanes at the storage's location (their own node, or none) with no load / unload time — a move then takes only
        // the team's travel to the rack, none while a member waits there
        private static void StorageMoveWarning(SimuLean.Storage.StorageSystem storage)
        {
            int noNode = 0, atLocation = 0;
            foreach (var l in storage.Lanes)
            {
                if (l.NodeId == null) noNode++;
                if (storage.Location != null && storage.NodeOf(l) == storage.Location) atLocation++;
            }
            if (storage.Location == null)
            {
                if (noNode > 0)
                    SimLog.Warning($"[SimuLean] Storage '{storage.Name}': {noNode} of its {storage.Lanes.Count} lane(s) have no node and the storage " +
                                   "has no location node: their moves by the transport team are instantaneous. Give the storage a location " +
                                   "(a Transport Node) or each lane a node (lanes[i].node).");
                return;
            }
            if (atLocation == 0) return;
            bool handling = storage.MoveLoadTime != null || storage.MoveUnloadTime != null;
            foreach (var m in storage.Transport.Dispatcher.Members) handling |= m.LoadTime > 0 || m.UnloadTime > 0;
            if (!handling)
                SimLog.Warning($"[SimuLean] Storage '{storage.Name}': {atLocation} of its {storage.Lanes.Count} lane(s) are at the storage's " +
                               $"location '{storage.Location}' (their node, or none) and no load / unload time is set: a move takes only the " +
                               "team's travel to the rack, none while a member waits there. Give the lanes nodes of their own " +
                               "(lanes[i].node) or the moves a load / unload time (transportBy.loadTime, the members' load time). " +
                               "If instantaneous handling is intended, set transportBy.loadTime = 0 explicitly (with transportBy.overrideLoadTimes; " +
                               "simulean_set turns it on): an explicit time, even 0, silences this warning.");
        }



        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => TransportStorageBuilders.BuildStorageSystem(config, clock, vElement);
    }
}
