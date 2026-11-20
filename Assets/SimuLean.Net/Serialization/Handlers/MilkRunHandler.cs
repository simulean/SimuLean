using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Resources;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Build side of the milk run (<see cref="MilkRun"/>, Unity wrapper <c>UnityMilkRun</c>) from its typed settings
    /// (<see cref="MilkRunSettings"/>: stops, flows, tour types, legs, pick-ups, named orders and request fields
    /// are typed records). Element references are config ids (or element names). The team is the element's Transport use
    /// (<c>transportBy.team</c>, <c>transportBy.requirements</c>); its <c>transportBy.loadTime</c> /
    /// <c>transportBy.unloadTime</c> are the default per-carrier times and <c>transportBy.travelTime</c> the default leg time.
    /// </summary>
    internal sealed class MilkRunHandler : ElementHandlerBase
    {
        public override string TypeName => "MilkRun";

        // its trains are members of its transport team; it has no setup or process resources
        internal override bool ConfigureTransport(Element element, ElementConfig config,
            IReadOnlyDictionary<string, ResourceDispatcher> teams, SimClock clock)
        {
            ((MilkRun)element).Transport = ResourceBuild.TransportUse(config, teams, element);
            return false;
        }

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<MilkRunSettings>();
            var mr = new MilkRun(config.Name, clock, vElement);
            string owner = config.Name;
            mr.Depot = new MilkRunStop(string.IsNullOrWhiteSpace(s.DepotName) ? "Depot" : s.DepotName.Trim()) { Location = Null(s.DepotNode) };
            mr.PatternMode = s.PatternMode;
            mr.Departure = s.Departure;
            mr.ReadyRule = s.ReadyRule;
            mr.ReadyMaxWait = s.ReadyMaxWait;
            mr.DepartureInterval = s.DepartureInterval;
            mr.FirstDeparture = s.FirstDeparture;
            mr.TimetableCycle = s.TimetableCycle;
            foreach (double t in s.Timetable ?? new double[0]) mr.Timetable.Add(t);
            mr.Capacity = s.Capacity;
            foreach (var c in s.CapacityByType ?? new List<MilkRunTypeCapacitySettings>())
            {
                if (c == null || string.IsNullOrWhiteSpace(c.Type))
                    throw new FormatException($"Milk run '{config.Name}': a capacity by type has no type.");
                mr.CapacityByType[c.Type.Trim()] = c.Capacity;
            }
            mr.TypeLabel = Null(s.TypeLabel);
            mr.Trains = Math.Max(1, s.Trains);
            mr.SymmetricLegs = s.SymmetricLegs;
            mr.TourMode = s.TourMode;
            mr.Policy = s.Policy;
            mr.LoadTimeout = s.LoadTimeout;
            // calculated fields of the requests: a formula over the request's fields, usable in the orders
            var requestFieldNames = new List<string>();
            var fields = new List<MilkRunRequestFieldSettings>();
            foreach (var f in s.RequestFields ?? new List<MilkRunRequestFieldSettings>())
            {
                if (f == null) continue;
                if (string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.Formula))
                    throw new FormatException($"Milk run '{config.Name}': request field '{f.Name}' needs a name and a formula.");
                requestFieldNames.Add(f.Name.Trim());
                fields.Add(f);
            }
            mr.RequestOrder = Order(config, s, s.RequestOrder, "requestOrder", requestFieldNames);
            mr.DeliveryOrder = Order(config, s, s.DeliveryOrder, "deliveryOrder", requestFieldNames);
            mr.Assignment = s.Assignment;
            mr.OriginAccess = s.OriginAccess;
            mr.FillTimeout = s.FillTimeout;
            mr.Requests.FieldContext = SimuLean.Expressions.ExpressionContext.RequestField;
            foreach (var f in fields)
                mr.Requests.DefineFields(new[] { new SimuLean.Collections.ListFieldSpec(f.Name.Trim(),
                    SimuLean.Collections.ListFieldKind.Expression, f.Formula.Trim(), monitor: false) });

            var tb = s.TransportBy;
            var load = SamplerOf(tb?.LoadTime, clock, owner, "transportLoad");
            var unload = SamplerOf(tb?.UnloadTime, clock, owner, "transportUnload");
            var travel = SamplerOf(tb?.TravelTime, clock, owner, "transportTravel");
            if (load != null) mr.DefaultLoadTime = item => SimuLean.Expressions.Samplers.Sample(load, item, mr);
            if (unload != null) mr.DefaultUnloadTime = item => SimuLean.Expressions.Samplers.Sample(unload, item, mr);
            if (travel != null) mr.DefaultLegTime = () => SimuLean.Expressions.Samplers.Sample(travel, null, mr);
            var eLoad = SamplerOf(s.EmptiesLoadTime, clock, owner, "emptiesLoadTime");
            var eUnload = SamplerOf(s.EmptiesUnloadTime, clock, owner, "emptiesUnloadTime");
            if (eLoad != null) mr.EmptiesLoadTime = item => SimuLean.Expressions.Samplers.Sample(eLoad, item, mr);
            if (eUnload != null) mr.EmptiesUnloadTime = item => SimuLean.Expressions.Samplers.Sample(eUnload, item, mr);

            foreach (var l in s.Legs ?? new List<MilkRunLegSettings>())
            {
                if (l == null) continue;
                string from = (l.From ?? "").Trim(), to = (l.To ?? "").Trim();
                if (from.Length == 0 || to.Length == 0 || l.Time == null)
                    throw new FormatException($"Milk run '{config.Name}': a leg needs from, to and a time ('{from}' → '{to}').");
                var sampler = ElementBuildHelpers.SamplerOf(l.Time, clock, owner, "leg:" + from + ">" + to, "legs[].time");
                // a constant leg is also the "distance" of nearest-stop rules for abstract trains
                double distance = (l.Time.StrategyType ?? "Constant") == "Constant" ? l.Time.ConstantValue ?? 5.0 : double.NaN;
                mr.SetLegTime(from, to, () => SimuLean.Expressions.Samplers.Sample(sampler, null, mr), distance);
            }
            return mr;
        }

        public override void Wire(Element element, ElementConfig config, IReadOnlyDictionary<string, Element> created)
        {
            if (!(element is MilkRun mr)) return;
            var s = config.SettingsAs<MilkRunSettings>();
            string owner = config.Name;
            var clock = mr.GetSimClock();

            if (!string.IsNullOrWhiteSpace(s.EmptiesDestination)) mr.EmptiesDestination = Resolve(created, s.EmptiesDestination.Trim(), config.Name);

            foreach (var r in s.Stops ?? new List<MilkRunStopSettings>())
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Name)) continue;
                var stop = new MilkRunStop(r.Name.Trim()) { Location = Null(r.Location) };
                foreach (string o in r.EmptiesOrigins ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(o)) stop.EmptiesOrigins.Add(Resolve(created, o.Trim(), config.Name));
                if (!string.IsNullOrWhiteSpace(r.EmptiesDestination)) stop.EmptiesDestination = Resolve(created, r.EmptiesDestination.Trim(), config.Name);
                if (!string.IsNullOrWhiteSpace(r.EmptiesFilter)) stop.EmptiesFilter = Condition(r.EmptiesFilter.Trim(), mr, "empties:" + stop.Name);
                if (mr.Stop(stop.Name) != null) throw new FormatException($"Milk run '{config.Name}': stop '{stop.Name}' is defined twice.");
                foreach (var o in stop.EmptiesOrigins) CheckEmptiesOrigin(config.Name, stop.Name, o);
                mr.Stops.Add(stop);
            }

            foreach (var r in s.Pickups ?? new List<MilkRunPickupSettings>())
            {
                if (r == null) continue;
                var stop = mr.Stop((r.Stop ?? "").Trim());
                if (stop == null) throw new FormatException($"Milk run '{config.Name}': pick-ups at an unknown stop '{r.Stop}'.");
                foreach (string o in r.Origins ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(o)) stop.Pickups.Add(Resolve(created, o.Trim(), config.Name));
            }

            foreach (var r in s.Flows ?? new List<MilkRunFlowSettings>())
            {
                if (r == null) continue;
                string name = (r.Name ?? "").Trim();
                if (name.Length == 0 || string.IsNullOrWhiteSpace(r.Origin) || string.IsNullOrWhiteSpace(r.Destination))
                    throw new FormatException($"Milk run '{config.Name}': flow '{name}' needs a name, an origin, a stop and a destination.");
                var stop = mr.Stop((r.Stop ?? "").Trim());
                if (stop == null || (stop == mr.Depot && mr.TourMode == MilkRunTourMode.Fixed))
                    throw new FormatException($"Milk run '{config.Name}': flow '{name}' goes to an unknown stop '{(r.Stop ?? "").Trim()}'.");
                var flow = new MilkRunFlow(name, Resolve(created, r.Origin.Trim(), config.Name), stop, Resolve(created, r.Destination.Trim(), config.Name));
                if (r.MaxPerTour != 0) flow.MaxPerTour = r.MaxPerTour;
                if (r.LoadTime != null)
                {
                    var ls = ElementBuildHelpers.SamplerOf(r.LoadTime, clock, owner, "flow:" + name + ":load", "flows[].loadTime");
                    flow.LoadTime = item => SimuLean.Expressions.Samplers.Sample(ls, item, mr);
                }
                if (r.UnloadTime != null)
                {
                    var us = ElementBuildHelpers.SamplerOf(r.UnloadTime, clock, owner, "flow:" + name + ":unload", "flows[].unloadTime");
                    flow.UnloadTime = item => SimuLean.Expressions.Samplers.Sample(us, item, mr);
                }
                if (!string.IsNullOrWhiteSpace(r.Filter)) flow.Filter = Condition(r.Filter.Trim(), mr, "filter:" + name);
                if (mr.Flows.Exists(x => x.Name == name)) throw new FormatException($"Milk run '{config.Name}': flow '{name}' is defined twice.");
                mr.Flows.Add(flow);
            }

            foreach (var r in s.FlowReadiness ?? new List<MilkRunReadinessSettings>())
            {
                if (r == null) continue;
                var flow = mr.Flows.Find(x => x.Name == (r.Flow ?? "").Trim());
                if (flow == null) throw new FormatException($"Milk run '{config.Name}': the flow readiness names an unknown flow '{r.Flow}'.");
                flow.MinPerTour = r.MinPerTour;
                flow.Optional = r.Optional;
            }

            foreach (var r in s.TourTypes ?? new List<MilkRunTourTypeSettings>())
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Name)) continue;
                var type = new MilkRunTourType(r.Name.Trim());
                var flowNames = Clean(r.Flows);
                if (flowNames.Count == 0) type.Flows.AddRange(mr.Flows);
                else
                    foreach (string n in flowNames)
                        type.Flows.Add(mr.Flows.Find(x => x.Name == n)
                                       ?? throw new FormatException($"Milk run '{config.Name}': tour type '{type.Name}' names an unknown flow '{n}'."));
                var stopNames = Clean(r.Stops);
                if (stopNames.Count == 0)
                {
                    foreach (var st in mr.Stops)
                        if (type.Flows.Exists(x => x.Stop == st)) type.Stops.Add(st);
                }
                else
                    foreach (string n in stopNames)
                    {
                        var st = mr.Stop(n);
                        if (st == null || st == mr.Depot) throw new FormatException($"Milk run '{config.Name}': tour type '{type.Name}' names an unknown stop '{n}'.");
                        type.Stops.Add(st);
                    }
                foreach (var fl in type.Flows)
                    if (!type.Stops.Contains(fl.Stop))
                        throw new FormatException($"Milk run '{config.Name}': tour type '{type.Name}' serves flow '{fl.Name}' but does not visit its stop '{fl.Stop.Name}'.");
                mr.TourTypes.Add(type);
            }
            if (mr.TourTypes.Count == 0)
            {
                // one tour of every flow and stop
                var all = new MilkRunTourType("All");
                all.Flows.AddRange(mr.Flows);
                all.Stops.AddRange(mr.Stops);
                mr.TourTypes.Add(all);
            }

            if (mr.TourMode == MilkRunTourMode.Dynamic)
            {
                foreach (var st in mr.AllStops())
                    if (st.Location == null)
                        st.Location = mr.Flows.Find(x => x.Stop == st)?.Destination.Location ?? (st.Pickups.Count > 0 ? st.Pickups[0].Location : null);
                mr.Connect();
                return;
            }

            var pattern = Clean(s.Pattern);
            if (pattern.Count == 0) mr.Pattern.AddRange(mr.TourTypes);
            else
                foreach (string n in pattern)
                    mr.Pattern.Add(mr.TourType(n) ?? throw new FormatException($"Milk run '{config.Name}': the pattern names an unknown tour type '{n}'."));

            // Locations: a stop without one stands where its destinations / empties origins stand; the depot where the origins do.
            foreach (var st in mr.Stops)
                if (st.Location == null)
                    st.Location = mr.Flows.Find(x => x.Stop == st)?.Destination.Location ?? (st.EmptiesOrigins.Count > 0 ? st.EmptiesOrigins[0].Location : null);
            if (mr.Depot.Location == null && mr.Flows.Count > 0) mr.Depot.Location = mr.Flows[0].Origin.Location;

            foreach (var o in mr.Origins())
                if (o.GetOutput() != null && !(o.GetOutput() is MilkRun))
                    SimLog.Warning($"[MilkRun] '{config.Name}': '{o.Name}' was connected to other elements; the milk run now carries its items.");
            mr.Connect();
        }

        // ── helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// An empties origin must be a place the empty carriers wait in, not the element that empties them:
        /// the milk run takes over an origin's output, so a Line-Side Unpacker (or any separator) listed there
        /// handed its pieces to the train, and an element already sending its items on lost its flow — the build only
        /// warned. An error naming the pattern: the element's <c>emptiesDestination</c> (its empties port) to a Queue at the
        /// stop, and that queue in <c>emptiesOrigins</c>.
        /// </summary>
        private static void CheckEmptiesOrigin(string milkRun, string stop, Element o)
        {
            if (o == null) return;
            bool separator = o is Separator;
            bool feeds = o.GetOutput() != null && !(o.GetOutput() is MilkRun);
            if (!separator && !feeds) return;
            throw new FormatException(
                $"Milk run '{milkRun}': stop '{stop}' lists '{o.Name}' as an empties origin, but '{o.Name}' " +
                (separator ? "empties carriers (its output hands out the pieces)" : "already sends its items on") +
                ": the milk run would take over its output. Put a Queue at the stop, send the empty carriers there " +
                $"(set '{o.Name}'.emptiesDestination to that queue{(separator ? "" : ", or route them to it")}) and list the queue in emptiesOrigins.");
        }

        /// <summary>An order: the name of one of the named orders, else a query text; empty = null.</summary>
        private static SimuLean.Collections.ListQuery Order(ElementConfig config, MilkRunSettings s, string value, string key,
                                                             IEnumerable<string> requestFields)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) return null;
            foreach (var o in s.NamedOrders ?? new List<NamedOrderSettings>())
                if (o != null && (o.Name ?? "").Trim() == text) { text = (o.Query ?? "").Trim(); break; }
            if (text.Length == 0) return null;
            var spec = SimuLean.Collections.ListQuerySpec.Decode(text);
            // the request's fields, its calculated ones and item. / origin. / destination.
            var known = new List<string>(SimuLean.Collections.ListFields.MilkRunRequest);
            known.AddRange(SimuLean.Collections.SimList.BuiltInFields);
            if (requestFields != null) known.AddRange(requestFields);
            SimuLean.Collections.ListFields.Check(spec, text, known, SimuLean.Collections.ListFields.MilkRunRoots, false, $"Milk run '{config.Name}', {key}");
            return spec.ToQuery();
        }

        private static Element Resolve(IReadOnlyDictionary<string, Element> created, string key, string owner)
        {
            if (created.TryGetValue(key, out Element e)) return e;
            foreach (var x in created.Values) if (x.Name == key) return x;
            throw new FormatException($"Milk run '{owner}': '{key}' is not an element of the model.");
        }

        // a flow / empties filter: its random functions draw from the milk run's stream of that filter it drew from the
        // origin's fallback stream <origin>/expr, shared with that origin's other uses)
        private static Func<Item, Element, bool> Condition(string text, MilkRun mr, string purpose)
        {
            var ex = SimuLean.Expressions.Expression.ParseCondition(text, SimuLean.Expressions.ExpressionContext.MilkRunFilter, mr.Name, purpose);
            var scope = new SimuLean.Expressions.EntityScope();
            return (item, origin) =>
            {
                scope.RandomStream = mr.RandomStreamFor(purpose);
                return ex.Condition(scope.Set(item, mr, origin));
            };
        }

        private static IRandomSampler SamplerOf(SamplerSettings s, SimClock clock, string owner, string purpose)
            => ElementBuildHelpers.SamplerOf(s, clock, owner, purpose, purpose);

        private static string Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static List<string> Clean(List<string> names)
        {
            var l = new List<string>();
            foreach (string n in names ?? new List<string>()) if (!string.IsNullOrWhiteSpace(n)) l.Add(n.Trim());
            return l;
        }
    }
}
