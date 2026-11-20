using System;
using System.Collections.Generic;
using UnityEngine;
using SimuLean.Resources;

namespace SimuLean.Unity
{
    /// <summary>How a milk run's carriers on board are shown: coupled as trailers, or riding on the vehicle.</summary>
    public enum MilkRunCarrierView { Trailers, OnBoard }

    /// <summary>
    /// A milk run in the scene: a tugger train (or any multi-load vehicle) of a resource team — the team is the
    /// <b>Transport</b> block of this element (<see cref="SElement.transportBy"/>; its load / unload overrides are the default
    /// per-carrier times, its travel time the default leg time) — that runs tours from a depot along stops, delivering the
    /// carriers of its flows and collecting empties, or (dynamic tours) goes from stop to stop on demand. The engine element is
    /// <see cref="MilkRun"/>, built by <c>MilkRunHandler</c> from what <c>UnityMilkRunExtractor</c> exports; this
    /// component is its description and, in Play mode, its view (<see cref="MilkRunTrainView"/>: the tractor and its trailers).
    /// It is not part of the item flow: origins and destinations are linked through it (leave their Next Elements empty).
    /// </summary>
    [SimuLeanElement(SimuLeanModules.Transport, "Milk Run",
        Description = "Tugger train / multi-load vehicle: tours from a depot along stops, deliveries and empties, or dynamic stop-to-stop service", Order = 30, Icon = "Assets/SimuLean.Unity/Art/Models/TuggerTrain/TowTractor.fbx")]
    public class UnityMilkRun : SElement, IVElement
    {
        public override bool HasInput => false;
        public override bool HasOutput => false;

        [Serializable]
        public class StopSpec
        {
            public string name = "Stop";
            [Tooltip("Where the stop is (spatial trains; optional — default: where its destinations stand).")]
            public UnityTransportNode node;
            [Tooltip("Elements whose items (empty carriers) the train collects here (fixed tours): the Queues the empty carriers " +
                     "wait in — never the element that empties them (the milk run takes over an origin's output).")]
            [SimuLeanHelp("The Queues where empty carriers wait at this stop. Never a Line-Side Unpacker / Separator or an element " +
                          "with its own Next Elements (a build error): set that element's emptiesDestination to a Queue at the stop " +
                          "and list the Queue here.")]
            public List<SElement> emptiesOrigins = new List<SElement>();
            [Tooltip("Where this stop's empties go at the depot (empty = the milk run's).")]
            public SElement emptiesDestination;
            [Tooltip("Which empties to collect (an expression over the item and its origin; empty = all).")]
            [SimuLean.Unity.SimuLeanExpression("milkRunFilter")]
            public string emptiesFilter = "";
            [Tooltip("Dynamic tours: origins at this stop whose carriers the train picks up.")]
            public List<SElement> pickups = new List<SElement>();
        }

        [Serializable]
        public class FlowSpec
        {
            public string name = "Flow";
            [Tooltip("Where the carriers wait (at the depot; dynamic tours: at any stop with it among its pick-ups).")]
            public SElement origin;
            [Tooltip("The stop of the destination (a stop name; dynamic tours may name the depot).")]
            public string stop = "Stop";
            public SElement destination;
            [Tooltip("Most carriers of this flow per tour (0 = no limit).")]
            [Min(0)] public int maxPerTour;
            [Tooltip("Fixed tours: the flow is ready only with this many carriers at its origin and room for them at the destination " +
                     "(a full load: e.g. 2 = never leave with one wagon). 0 / 1 = one carrier.")]
            [Min(0)] public int minPerTour;
            [Tooltip("Fixed tours: the flow neither makes a tour ready nor holds it back; its carriers go when the tour leaves, if ready.")]
            public bool optional;
            [Tooltip("Which items of the origin belong to this flow (expression over the item and the origin, e.g. item.type == 'Tote'; empty = all).")]
            [SimuLean.Unity.SimuLeanExpression("milkRunFilter")]
            public string filter = "";
            [Tooltip("Own load / unload times for this flow.")]
            public bool overrideTimes;
            public RandomProcessConfig loadTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0 };
            public RandomProcessConfig unloadTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0 };
        }

        [Serializable]
        public class TourTypeSpec
        {
            public string name = "A";
            [Tooltip("Flows served (names; empty = all).")]
            public List<string> flows = new List<string>();
            [Tooltip("Stops visited in order (names; empty = the stops of its flows, in the stops' order).")]
            public List<string> stops = new List<string>();
        }

        [Serializable]
        public class LegSpec
        {
            public string from = "Depot", to = "Stop";
            public RandomProcessConfig time = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 60 };
            [Tooltip("View only (2026-10-08): points the drawn train passes through on this leg, in order (e.g. along the aisles); " +
                     "reversed for the way back with Legs Both Ways. Empty = a straight line between the stops.")]
            public List<Transform> via = new List<Transform>();
        }

        [Serializable]
        public class TypeCapacity
        {
            public string type = "";
            [Min(0)] public int count = 1;
        }

        [Header("Tours")]
        [Tooltip("Fixed: tours of the tour types in the pattern. Dynamic: stop to stop on demand (next stop by the request order).")]
        public MilkRunTourMode tourMode = MilkRunTourMode.Fixed;
        public string depotName = "Depot";
        [Tooltip("Where the depot is (spatial trains; optional — default: where the first flow's origin stands).")]
        public UnityTransportNode depotNode;
        [Tooltip("Dynamic tours: origins at the depot.")]
        public List<SElement> depotPickups = new List<SElement>();
        public List<StopSpec> stops = new List<StopSpec>();
        public List<FlowSpec> flows = new List<FlowSpec>();
        public List<TourTypeSpec> tourTypes = new List<TourTypeSpec>();
        [Tooltip("Order of the tour types (e.g. A, B, B; empty = each type once).")]
        public List<string> pattern = new List<string>();
        public MilkRunPatternMode patternMode = MilkRunPatternMode.Cyclic;

        [Header("Departure")]
        public MilkRunDeparture departure = MilkRunDeparture.Ready;
        public MilkRunReadyRule readyRule = MilkRunReadyRule.Any;
        [Tooltip("Ready rule All: after waiting this long at the depot the train leaves as soon as any flow is ready (0 = waits for all).")]
        [Min(0)] public float readyMaxWait;
        [Min(0)] public float departureInterval;
        [Min(0)] public float firstDeparture;
        [Tooltip("Departure times (overrides the interval); repeated every cycle when the cycle is > 0.")]
        public List<float> timetable = new List<float>();
        [Min(0)] public float timetableCycle;

        [Header("Dynamic tours")]
        public MilkRunDynamicPolicy policy = MilkRunDynamicPolicy.Opportunistic;
        [Tooltip("Wait at a loading stop up to this long for more carriers while not full (0 = leave at once).")]
        [Min(0)] public float loadTimeout;
        [Tooltip("Which open request decides the next stop (order field: age, distance, onBoard, originFill, item.priority, " +
                 "origin.content, a request field; filters on kind, stop, origin.name, destination.name, item.type). Empty = the oldest request.")]
        public SimuLean.Collections.ListQuerySpec requestOrder = new SimuLean.Collections.ListQuerySpec();
        [Tooltip("Order of the requests once the train delivers (fill then deliver) or while it carries something " +
                 "(opportunistic), e.g. distance = the nearest stop, pushTime = loading order. Empty = the request order.")]
        public SimuLean.Collections.ListQuerySpec deliveryOrder = new SimuLean.Collections.ListQuerySpec();
        [Tooltip("Named orders (name = query text, e.g. R2 = |distance). The request / delivery order may name one instead " +
                 "of being a query (Request Order Name), which makes it a choice for the Experimenter / optimiser.")]
        public List<NamedOrder> namedOrders = new List<NamedOrder>();
        [Tooltip("Optional: the named order used as the request order (overrides Request Order).")]
        public string requestOrderName = "";
        [Tooltip("Optional: the named order used as the delivery order (overrides Delivery Order).")]
        public string deliveryOrderName = "";
        [Tooltip("At Stop: the chosen request decides where to go; there the train serves the stop. Before Trip: the request " +
                 "is assigned (reserved) at once and the train serves only that carrier, then decides again.")]
        public MilkRunAssignment assignment = MilkRunAssignment.AtStop;
        [Tooltip("Head: an origin's first carrier is the request. Any: every carrier waiting in it is (free access).")]
        public MilkRunOriginAccess originAccess = MilkRunOriginAccess.Head;
        [Tooltip("Fill then deliver: when nothing more can be picked up, wait for more until full or this long after the " +
                 "first load (0 = deliver at once, < 0 = wait until full).")]
        public float fillTimeout;

        [Header("Train")]
        [Tooltip("Carriers per train (0 = no limit).")]
        [Min(0)] public int capacity = 4;
        [Tooltip("Carriers per train by type (the item type, or the label below).")]
        public List<TypeCapacity> capacityByType = new List<TypeCapacity>();
        [Tooltip("Item label giving the carrier type (empty = the item type).")]
        public string typeLabel = "";
        [Tooltip("Trains running tours at once; each is driven by one member of the transportBy.team (the team needs at least " +
                 "this many members). The Tugger Train preset places no team: add one (kind tugger) and name it in transportBy.team.")]
        [SimuLeanHelp("Trains at once, each driven by one member of transportBy.team (required: the Tugger Train preset places " +
                      "no team — simulean_add_team kind=tugger, then transportBy.team). The team's members limit the trains.")]
        [Min(1)] public int trains = 1;

        [Header("Empties")]
        public SElement emptiesDestination;
        public bool overrideEmptiesTimes;
        public RandomProcessConfig emptiesLoadTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0 };
        public RandomProcessConfig emptiesUnloadTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0 };

        [Header("Legs")]
        [Tooltip("Travel time per leg (from → to, stop names, the depot by its name). Spatial teams use their travel model for legs not listed.")]
        public List<LegSpec> legs = new List<LegSpec>();
        public bool symmetricLegs = true;
        [Tooltip("View: radius of the rounded corners of the legs' via paths.")]
        [Min(0f)] public float viaCornerRadius = 2f;

        [Header("View")]
        [Tooltip("Trailers: the carriers on board are coupled behind the tractor (a tugger train). On Board: they ride on the " +
                 "vehicle (a shuttle's deck): at its nodes CargoSlot_0, CargoSlot_1… in loading order, else in a row on its Deck " +
                 "/ LoadSocket node along its local X.")]
        public MilkRunCarrierView carrierView = MilkRunCarrierView.Trailers;
        [Tooltip("On Board without slot nodes: spacing of the row of carriers (m).")]
        public float deckSpacing = 0.7f;
        [Tooltip("On Board, a vehicle with lanes (a transfer car): each travel leg ends with the lane it serves first facing the " +
                 "stop element's transfer point (its child TransferPoint, else its position); a further lane served at the same stop " +
                 "indexes there (first part of the load / unload time). Carriers transfer straight and keep their heading. " +
                 "Visual only: the engine's leg and step times are unchanged.")]
        public bool alignLanes = true;
        [Tooltip("Share of a load / unload time used for an indexing move at the stop (the rest is the straight transfer).")]
        [Range(0.05f, 0.9f)] public float indexShare = 0.35f;
        [Tooltip("Tractor shown for an abstract team (a spatial team shows its own member prefab).")]
        public GameObject tractorPrefab;
        [Tooltip("Gap between coupled trailers (m) and the length of a trailer without hitch nodes.")]
        public float trailerGap = 0.4f, defaultTrailerLength = 1.6f;

        private MilkRun milkRun;

        /// <summary>The engine element of the current run (null outside a run).</summary>
        public MilkRun MilkRun => milkRun;

        private readonly Dictionary<string, PolylinePath> legPaths = new Dictionary<string, PolylinePath>();

        /// <summary>The drawn path of the leg <paramref name="from"/> → <paramref name="to"/> (stop names): the stops' positions
        /// through the leg's <see cref="LegSpec.via"/> points (reversed for a symmetric leg's way back); null when the leg has
        /// no via points (a straight line). View only; cached during a run.</summary>
        public PolylinePath LegPath(string from, string to)
        {
            string key = from + " -> " + to;
            if (Application.isPlaying && legPaths.TryGetValue(key, out var cached)) return cached;
            PolylinePath path = null;
            List<Transform> via = null;
            bool reversed = false;
            foreach (var l in legs)
            {
                if (l == null || l.via == null || l.via.Count == 0) continue;
                if (l.from == from && l.to == to) { via = l.via; break; }
                if (symmetricLegs && l.from == to && l.to == from) { via = l.via; reversed = true; }
            }
            if (via != null && TryStopPosition(from, out Vector3 a) && TryStopPosition(to, out Vector3 b))
            {
                var pts = new List<Vector3> { a };
                if (reversed) for (int i = via.Count - 1; i >= 0; i--) { if (via[i] != null) pts.Add(via[i].position); }
                else foreach (var t in via) if (t != null) pts.Add(t.position);
                pts.Add(b);
                path = PolylinePath.Build(pts, viaCornerRadius);
            }
            if (Application.isPlaying) legPaths[key] = path;
            return path;
        }

        protected override void OnBound(Element built)
        {
            milkRun = built as MilkRun;
            legPaths.Clear();
        }

        public override void OnRunStarted()
        {
            if (milkRun == null) return;
            var view = GetComponent<MilkRunTrainView>();
            if (view == null) view = gameObject.AddComponent<MilkRunTrainView>();
            view.Bind(milkRun, this);
        }

        public override void ClearViews()
        {
            var view = GetComponent<MilkRunTrainView>();
            if (view != null) view.Clear();
        }

        /// <summary>Where stop <paramref name="name"/> stands in the scene (its node, else its first destination / origin).</summary>
        public bool TryStopPosition(string name, out Vector3 position)
        {
            position = default;
            if (name == null) return false;
            if (name == depotName)
            {
                if (depotNode != null) { position = depotNode.transform.position; return true; }
                foreach (var p in depotPickups) if (p != null) { position = p.transform.position; return true; }
                foreach (var f in flows) if (f.origin != null) { position = f.origin.transform.position; return true; }
                return false;
            }
            var s = stops.Find(x => x != null && x.name == name);
            if (s != null && s.node != null) { position = s.node.transform.position; return true; }
            foreach (var f in flows) if (f.stop == name && f.destination != null) { position = f.destination.transform.position; return true; }
            if (s != null)
            {
                foreach (var o in s.emptiesOrigins) if (o != null) { position = o.transform.position; return true; }
                foreach (var o in s.pickups) if (o != null) { position = o.transform.position; return true; }
            }
            return false;
        }

        // Not part of the item flow: no items are shown here.
        void IVElement.ReportState(string msg) { }
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item item) { }
        void IVElement.UnloadItem(Item item) { }
    }
    /// <summary>A named order (name, query text) of a milk run or a resource team: their request order may name it, which
    /// makes it a choice for the Experimenter / optimiser.</summary>
    [Serializable]
    public class NamedOrder
    {
        public string name = "R1";
        [Tooltip("List query text: conditions|orderBy[ desc], e.g. |distance or |priority desc.")]
        public string query = "|pushTime";
    }
}
