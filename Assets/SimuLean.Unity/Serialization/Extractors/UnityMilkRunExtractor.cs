using System.Collections.Generic;
using SimuLean.Unity;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction of <c>UnityMilkRun</c> into its typed settings (<see cref="MilkRunSettings"/>; the build side is the
    /// engine's <c>MilkRunHandler</c>). Its settings go in pass 1; the records that name other elements (stops, flows,
    /// pick-ups, the empties destination) go in the reference pass, once every element has its config id. The team is the
    /// element's Transport block, exported by the scene extractor like any element's.</summary>
    internal sealed class UnityMilkRunExtractor : ElementExtractorBase, IElementReferenceExtractor
    {
        public override string TypeName => "MilkRun";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityMilkRun mr)) return;
            var s = config.SettingsAs<MilkRunSettings>();
            s.TourMode = mr.tourMode;
            s.DepotName = string.IsNullOrWhiteSpace(mr.depotName) ? "Depot" : mr.depotName.Trim();
            s.DepotNode = mr.depotNode != null && !string.IsNullOrEmpty(mr.depotNode.NodeId) ? mr.depotNode.NodeId : null;
            s.TourTypes = new List<MilkRunTourTypeSettings>();
            foreach (var t in mr.tourTypes)
                if (t != null && !string.IsNullOrWhiteSpace(t.name)) s.TourTypes.Add(MilkRunTourTypeSettings.Of(t.name.Trim(), Clean(t.flows), Clean(t.stops)));
            s.Pattern = Clean(mr.pattern);
            s.PatternMode = mr.patternMode;
            s.Departure = mr.departure;
            s.ReadyRule = mr.readyRule;
            s.ReadyMaxWait = mr.readyMaxWait;
            s.DepartureInterval = mr.departureInterval;
            s.FirstDeparture = mr.firstDeparture;
            var tt = new List<double>();
            foreach (float t in mr.timetable) tt.Add(t);
            s.Timetable = tt.Count > 0 ? tt.ToArray() : null;
            s.TimetableCycle = mr.timetableCycle;
            s.Policy = mr.policy;
            s.LoadTimeout = mr.loadTimeout;
            s.RequestOrder = !string.IsNullOrWhiteSpace(mr.requestOrderName) ? mr.requestOrderName.Trim()
                           : mr.requestOrder != null ? mr.requestOrder.Encode() : null;
            s.DeliveryOrder = !string.IsNullOrWhiteSpace(mr.deliveryOrderName) ? mr.deliveryOrderName.Trim()
                            : mr.deliveryOrder != null && !IsEmpty(mr.deliveryOrder) ? mr.deliveryOrder.Encode() : null;
            s.NamedOrders = new List<NamedOrderSettings>();
            if (mr.namedOrders != null)
                foreach (var o in mr.namedOrders)
                    if (o != null && !string.IsNullOrWhiteSpace(o.name)) s.NamedOrders.Add(NamedOrderSettings.Of(o.name.Trim(), (o.query ?? "").Trim()));
            s.Assignment = mr.assignment;
            s.OriginAccess = mr.originAccess;
            s.FillTimeout = mr.fillTimeout;
            s.Capacity = mr.capacity;
            s.CapacityByType = new List<MilkRunTypeCapacitySettings>();
            foreach (var c in mr.capacityByType)
                if (c != null && !string.IsNullOrWhiteSpace(c.type)) s.CapacityByType.Add(new MilkRunTypeCapacitySettings { Type = c.type.Trim(), Capacity = c.count });
            s.TypeLabel = mr.typeLabel ?? "";
            s.Trains = mr.trains;
            if (mr.overrideEmptiesTimes && mr.emptiesLoadTime != null) s.EmptiesLoadTime = mr.emptiesLoadTime.ToSettings();
            if (mr.overrideEmptiesTimes && mr.emptiesUnloadTime != null) s.EmptiesUnloadTime = mr.emptiesUnloadTime.ToSettings();
            s.Legs = new List<MilkRunLegSettings>();
            foreach (var l in mr.legs)
                if (l != null && !string.IsNullOrWhiteSpace(l.from) && !string.IsNullOrWhiteSpace(l.to) && l.time != null)
                    s.Legs.Add(MilkRunLegSettings.Of(l.from.Trim(), l.to.Trim(), l.time.ToSettings()));
            s.SymmetricLegs = mr.symmetricLegs;
            // the records naming elements come in the reference pass (ids); empty until then
            s.Stops = new List<MilkRunStopSettings>();
            s.Flows = new List<MilkRunFlowSettings>();
            s.Pickups = new List<MilkRunPickupSettings>();
            s.EmptiesDestination = null;
        }

        public void ExtractReferences(SElement element, ElementConfig config, IReadOnlyDictionary<SElement, string> ids)
        {
            if (!(element is UnityMilkRun mr)) return;
            string Id(SElement e) => e != null && ids.TryGetValue(e, out string id) ? id : null;
            var s = config.SettingsAs<MilkRunSettings>();

            s.Stops = new List<MilkRunStopSettings>();
            s.Pickups = new List<MilkRunPickupSettings>();
            var depotPick = Ids(mr.depotPickups, Id);
            if (depotPick.Count > 0) s.Pickups.Add(MilkRunPickupSettings.Of(s.DepotName, depotPick.ToArray()));
            foreach (var st in mr.stops)
            {
                if (st == null || string.IsNullOrWhiteSpace(st.name)) continue;
                string name = st.name.Trim();
                s.Stops.Add(MilkRunStopSettings.Of(name, st.node != null ? st.node.NodeId : "", Ids(st.emptiesOrigins, Id),
                                                   Id(st.emptiesDestination) ?? "", st.emptiesFilter ?? ""));
                var pick = Ids(st.pickups, Id);
                if (pick.Count > 0) s.Pickups.Add(MilkRunPickupSettings.Of(name, pick.ToArray()));
            }

            s.Flows = new List<MilkRunFlowSettings>();
            s.FlowReadiness = new List<MilkRunReadinessSettings>();
            foreach (var f in mr.flows)
            {
                if (f == null) continue;
                string o = Id(f.origin), d = Id(f.destination);
                if (o == null || d == null)
                {
                    Debug.LogWarning($"[SimuLean] Milk run '{mr.name}': flow '{f.name}' has no origin or destination — left out.");
                    continue;
                }
                s.Flows.Add(MilkRunFlowSettings.Of(f.name.Trim(), o, (f.stop ?? "").Trim(), d, f.maxPerTour,
                                                   f.overrideTimes && f.loadTime != null ? f.loadTime.ToSettings() : null,
                                                   f.overrideTimes && f.unloadTime != null ? f.unloadTime.ToSettings() : null, f.filter ?? ""));
                if (f.minPerTour > 1 || f.optional) s.FlowReadiness.Add(MilkRunReadinessSettings.Of(f.name.Trim(), f.minPerTour, f.optional));
            }
            s.EmptiesDestination = Id(mr.emptiesDestination);
        }

        private static bool IsEmpty(SimuLean.Collections.ListQuerySpec q)
            => string.IsNullOrWhiteSpace(q.orderBy) && (q.conditions == null || q.conditions.Count == 0);

        private static List<string> Ids(List<SElement> elements, System.Func<SElement, string> id)
        {
            var l = new List<string>();
            if (elements != null) foreach (var e in elements) { string x = id(e); if (x != null) l.Add(x); }
            return l;
        }

        private static List<string> Clean(List<string> names)
        {
            var l = new List<string>();
            if (names != null) foreach (var n in names) if (!string.IsNullOrWhiteSpace(n)) l.Add(n.Trim());
            return l;
        }
    }
}
