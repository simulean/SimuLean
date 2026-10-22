using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Collections;
using SimuLean.Resources;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Builds the resource side of a <see cref="SimulationConfig"/>: teams (a <see cref="ResourceDispatcher"/> and its
    /// <see cref="TaskExecutor"/> members) and the process / transport resource use of elements
    /// (<see cref="ResourceUseKeys"/>). Used by the headless factory; the Unity wrappers build the same objects.
    /// </summary>
    /// <summary>Where things can be in a model: the network's nodes and the travel matrices' locations.</summary>
    public sealed class ModelSpace
    {
        public SimuLean.Transport.TransportNetwork Network { get; set; }
        public Dictionary<string, DistanceMatrix> Matrices { get; } = new Dictionary<string, DistanceMatrix>();
        public Dictionary<string, WalkGrid> Grids { get; } = new Dictionary<string, WalkGrid>();
        /// <summary>Traffic on the network (shared by every team with <c>Traffic</c>).</summary>
        public TrafficControl Traffic { get; set; }

        public bool IsLocation(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (Network != null && Network.GetNode(id) != null) return true;
            foreach (var m in Matrices.Values) if (m.Locations.Contains(id)) return true;
            return false;
        }
    }

    /// <summary>Units of a team's speeds, accelerations and load / unload times (<see cref="ResourceTeamConfig.Units"/>).</summary>
    public enum ResourceUnits
    {
        /// <summary>Model units: distance per model time unit, model time units (the clock's unit).</summary>
        Model,
        /// <summary>Physical units: distance per second (m/s), distance per second² and seconds, whatever the clock's unit.</summary>
        Physical,
    }

    public static class ResourceBuild
    {
        /// <summary>The team's config in model units: with <see cref="ResourceUnits.Physical"/>, speeds × seconds per unit,
        /// accelerations × its square, load / unload times ÷ it. A copy; the config is not changed. A travel
        /// calibration is measured in model time already and is left as it is.</summary>
        public static ResourceTeamConfig InModelUnits(ResourceTeamConfig tc, double secondsPerUnit)
        {
            if (!IsPhysical(tc) || secondsPerUnit == 1.0) return tc;
            if (!(secondsPerUnit > 0)) throw new ArgumentOutOfRangeException(nameof(secondsPerUnit));
            var c = ConfigCloner.DeepClone(tc);
            double u = secondsPerUnit;
            c.Speed = tc.Speed * u;
            var vs = Triple(tc.AxisSpeed, tc.Id); var acc = Triple(tc.AxisAccel, tc.Id);
            c.AxisSpeed = TripleSpec(vs[0] * u, vs[1] * u, vs[2] * u);
            c.AxisAccel = TripleSpec(acc[0] * u * u, acc[1] * u * u, acc[2] * u * u);
            foreach (var m in c.Members) { m.LoadTime /= u; m.UnloadTime /= u; }
            return c;
        }

        /// <summary>True when the team's values are in physical units.</summary>
        public static bool IsPhysical(ResourceTeamConfig tc)
        {
            string u = (tc?.Units ?? "").Trim();
            if (u.Length == 0 || u.Equals(nameof(ResourceUnits.Model), StringComparison.OrdinalIgnoreCase)) return false;
            if (u.Equals(nameof(ResourceUnits.Physical), StringComparison.OrdinalIgnoreCase)) return true;
            throw new ArgumentException("Resource team '" + tc.Id + "': unknown units '" + tc.Units + "' (Model or Physical).");
        }

        /// <summary>A team from its config; its members are appended to <paramref name="executors"/>. Spatial
        /// travel needs the model's locations (<paramref name="space"/>: network, matrices).</summary>
        public static ResourceDispatcher BuildTeam(ResourceTeamConfig tc, SimClock clock, List<TaskExecutor> executors,
                                                   ModelSpace space = null)
        {
            space = space ?? new ModelSpace();
            tc = InModelUnits(tc, clock.Context.Calendar.SecondsPerUnit);
            ITravelModel travel = TravelOf(tc, space);
            bool onNetwork = travel != null;
            if (onNetwork && (string.IsNullOrEmpty(tc.HomeNode) || !space.IsLocation(tc.HomeNode)))
                throw new ArgumentException("Resource team '" + tc.Id + "': home '" + tc.HomeNode + "' is not a location of the model.");
            var d = new ResourceDispatcher(string.IsNullOrEmpty(tc.Name) ? tc.Id : tc.Name, clock)
            {
                RequestOrder = OrderOf(tc.RequestOrder),
                ResourceOrder = OrderOf(tc.ResourceOrder),
            };
            int i = 0;
            foreach (var m in tc.Members)
            {
                i++;
                var ex = new TaskExecutor(string.IsNullOrEmpty(m.Name) ? d.Name + "_" + i : m.Name, clock,
                                          onNetwork ? ResourceMode.Spatial : ResourceMode.Abstract)
                {
                    LoadTime = m.LoadTime, UnloadTime = m.UnloadTime,
                    HomeNode = onNetwork ? tc.HomeNode : null, TravelModel = travel,
                    RecordMoves = travel is ExternalTravel,   // moves timed outside: the calibration's input
                };
                foreach (string rec in m.Labels ?? new List<string>())
                {
                    int eq = rec == null ? -1 : rec.IndexOf('=');
                    if (eq > 0) ex.SetLabelValue(rec.Substring(0, eq).Trim(), rec.Substring(eq + 1).Trim());
                }
                d.Add(ex);
                executors.Add(ex);
            }
            return d;
        }

        /// <summary>How the items of an element walk by themselves (<see cref="ResourceUseKeys.WalkSpeed"/> &gt; 0), or null:
        /// the resources' travel model (network — the default —, walk grid or matrix) at the walking speed, given in m/s
        /// and converted to model units as a physical team's speed.</summary>
        public static ITravelModel WalkTravel(ElementConfig ec, ModelSpace space, double secondsPerUnit)
        {
            double speed = ec.GetParameter<double>(ResourceUseKeys.WalkSpeed, 0.0);
            if (!(speed > 0)) return null;
            string kind = ec.GetParameter<string>(ResourceUseKeys.WalkTravel, "");
            kind = string.IsNullOrWhiteSpace(kind) ? "Network" : kind.Trim();
            if (!kind.Equals("Network", StringComparison.OrdinalIgnoreCase) && !kind.Equals("Grid", StringComparison.OrdinalIgnoreCase)
                && !kind.Equals("Matrix", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Element '{ec.Name}': items walk on the Network, a Grid or a Matrix, not '{kind}'.");
            var tc = new ResourceTeamConfig
            {
                Id = ec.Name, Travel = kind, Speed = speed, Units = nameof(ResourceUnits.Physical),
                Grid = ec.GetParameter<string>(ResourceUseKeys.WalkGrid, ""), Matrix = ec.GetParameter<string>(ResourceUseKeys.WalkMatrix, ""),
            };
            return TravelOf(InModelUnits(tc, secondsPerUnit), space ?? new ModelSpace());
        }

        // The team's travel model (null = abstract).
        private static ITravelModel TravelOf(ResourceTeamConfig tc, ModelSpace space) => TravelOf(tc, space, (tc.Travel ?? "").Trim());

        private static ITravelModel TravelOf(ResourceTeamConfig tc, ModelSpace space, string kind)
        {
            if (kind.Length == 0 || kind.Equals("Abstract", StringComparison.OrdinalIgnoreCase)) return null;
            if (kind.Equals("External", StringComparison.OrdinalIgnoreCase))
            {
                // Moves timed outside the engine (Unity physics in a scene run): the estimate model gives distances.
                string baseKind = (tc.CalibrationBase ?? "").Trim();
                if (baseKind.Length == 0 || baseKind.Equals("External", StringComparison.OrdinalIgnoreCase)
                    || baseKind.Equals("Calibrated", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Resource team '" + tc.Id + "': external travel needs an estimate model (Network, Matrix or Grid).");
                return new ExternalTravel(TravelOf(tc, space, baseKind));
            }
            if (kind.Equals("Calibrated", StringComparison.OrdinalIgnoreCase))
            {
                string baseKind = (tc.CalibrationBase ?? "").Trim();
                if (baseKind.Length == 0 || baseKind.Equals("Calibrated", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Resource team '" + tc.Id + "': calibrated travel needs an estimate model (Network, Matrix or Grid).");
                var estimate = TravelOf(tc, space, baseKind);
                var abc = Triple(tc.CalibrationFit, tc.Id);
                var cal = new CalibratedTravel(estimate, abc[0], abc[1], abc[2], tc.CalibrationMinSamples);
                foreach (string rec in tc.CalibrationPairs ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(rec)) cal.AddPair(CalibratedTravel.PairStat.Decode(rec));
                return cal;
            }
            if (kind.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                if (space.Network == null)
                    throw new ArgumentException("Resource team '" + tc.Id + "' travels on the network, but the model has none.");
                var nt = new NetworkTravel(space.Network, tc.Speed);
                if (!tc.Traffic) return nt;
                if (space.Traffic == null) space.Traffic = new TrafficControl();
                return new TrafficNetworkTravel(nt, space.Traffic);
            }
            if (kind.Equals("Matrix", StringComparison.OrdinalIgnoreCase))
            {
                if (tc.Matrix == null || !space.Matrices.TryGetValue(tc.Matrix, out DistanceMatrix m))
                    throw new ArgumentException("Resource team '" + tc.Id + "': travel matrix '" + tc.Matrix + "' does not exist.");
                return new MatrixTravel(m, tc.Speed);
            }
            if (kind.Equals("Grid", StringComparison.OrdinalIgnoreCase))
            {
                if (tc.Grid == null || !space.Grids.TryGetValue(tc.Grid, out WalkGrid grid))
                    throw new ArgumentException("Resource team '" + tc.Id + "': walk grid '" + tc.Grid + "' does not exist.");
                if (space.Network == null)
                    throw new ArgumentException("Resource team '" + tc.Id + "' walks between points, but the model has none.");
                return new GridTravel(grid, space.Network, tc.Speed);
            }
            if (kind.Equals("Axes", StringComparison.OrdinalIgnoreCase))
            {
                if (space.Network == null)
                    throw new ArgumentException("Resource team '" + tc.Id + "' travels by axes between points, but the model has none.");
                return new AxisTravel(space.Network, Triple(tc.AxisSpeed, tc.Id), Triple(tc.AxisAccel, tc.Id), tc.AxesSequential);
            }
            throw new ArgumentException("Resource team '" + tc.Id + "': unknown travel '" + tc.Travel + "'.");
        }

        private static double[] Triple(string spec, string team)
        {
            var v = new double[3];
            if (string.IsNullOrWhiteSpace(spec)) return v;
            string[] f = spec.Split('|');
            for (int i = 0; i < 3 && i < f.Length; i++)
                if (!double.TryParse(f[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    throw new FormatException("Resource team '" + team + "': bad axis values '" + spec + "' (expected 'x|y|z').");
            return v;
        }

        public static string TripleSpec(double x, double y, double z)
            => x.ToString("R", CultureInfo.InvariantCulture) + "|" + y.ToString("R", CultureInfo.InvariantCulture) + "|" + z.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>A matrix from its config (records <c>from|to|distance</c>).</summary>
        public static DistanceMatrix BuildMatrix(TravelMatrixConfig mc)
        {
            var m = new DistanceMatrix(mc.Symmetric);
            foreach (string rec in mc.Entries ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(rec)) continue;
                string[] f = rec.Split('|');
                double d;
                if (f.Length != 3 || !double.TryParse(f[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw new FormatException("Travel matrix '" + mc.Id + "': bad entry '" + rec + "' (expected 'from|to|distance').");
                m.Set(f[0].Trim(), f[1].Trim(), d);
            }
            return m;
        }

        /// <summary>A request priority given by an expression over the item and the element (a bare name = the item's
        /// property, e.g. <c>prio</c>; or <c>item.prio * 10 + item.age</c>).</summary>
        public static Func<Item, double> PriorityOf(string expression, Element element)
        {
            var ex = SimuLean.Expressions.Expression.Parse(expression);
            var scope = new SimuLean.Expressions.EntityScope();
            return item => ex.Number(scope.Set(item, element));
        }

        public static WalkGrid BuildGrid(WalkGridConfig gc)
            => WalkGrid.FromRows(gc.OriginX, gc.OriginZ, gc.CellSize, gc.Rows, gc.Y);

        public static string MatrixEntry(string from, string to, double distance)
            => from + "|" + to + "|" + distance.ToString("R", CultureInfo.InvariantCulture);

        private static ListQuery OrderOf(string spec)
            => string.IsNullOrWhiteSpace(spec) ? null : ListQuerySpec.Decode(spec).ToQuery();

        /// <summary>The element's process resources (null when it has no process team).</summary>
        public static StationResources ProcessUse(ElementConfig ec, IReadOnlyDictionary<string, ResourceDispatcher> teams, Element element = null,
                                                  SimClock clock = null)
        {
            var use = Use(ec, ResourceUseKeys.ProcessTeam, ResourceUseKeys.ProcessRequirements, ResourceUseKeys.ProcessPriority, teams, element);
            string release = ec.GetParameter<string>(ResourceUseKeys.ProcessRelease, "");
            if (use != null && !string.IsNullOrWhiteSpace(release))
            {
                // an unknown release fails the build (it was ignored, giving EndOfProcess)
                if (!Enum.TryParse(release.Trim(), out ProcessRelease r) || !Enum.IsDefined(typeof(ProcessRelease), r))
                    throw new FormatException($"'{ec.Name}': unknown process release '{release}' (EndOfProcess, ItemLeaves or AtElement).");
                use.Release = r;
            }
            if (use != null) use.HoldFor = Sampler(ec, ResourceUseKeys.ProcessHoldFor, clock);
            return use;
        }

        /// <summary>The element's transport resources (null when its items are not carried by resources).</summary>
        public static StationResources TransportUse(ElementConfig ec, IReadOnlyDictionary<string, ResourceDispatcher> teams, Element element = null)
            => Use(ec, ResourceUseKeys.TransportTeam, ResourceUseKeys.TransportRequirements, ResourceUseKeys.TransportPriority, teams, element);

        /// <summary>Sets the move times of a transport link from the element's sampler specs (one stream each).</summary>
        public static void ApplyTransportTimes(ResourceTransportLink link, ElementConfig ec, SimClock clock)
        {
            var load = Sampler(ec, ResourceUseKeys.TransportLoad, clock);
            var travel = Sampler(ec, ResourceUseKeys.TransportTravel, clock);
            var unload = Sampler(ec, ResourceUseKeys.TransportUnload, clock);
            var origin = link.Origin;
            if (load != null) link.LoadTime = item => SimuLean.Expressions.Samplers.Sample(load, item, origin);
            if (unload != null) link.UnloadTime = item => SimuLean.Expressions.Samplers.Sample(unload, item, origin);
            if (travel != null) link.TravelTime = (item, dest) => SimuLean.Expressions.Samplers.Sample(travel, item, origin);
        }

        /// <summary>The same for the moves of a list holder.</summary>
        public static void ApplyTransportTimes(TransportMoves link, ElementConfig ec, SimClock clock)
        {
            var load = Sampler(ec, ResourceUseKeys.TransportLoad, clock);
            var travel = Sampler(ec, ResourceUseKeys.TransportTravel, clock);
            var unload = Sampler(ec, ResourceUseKeys.TransportUnload, clock);
            var origin = link.Origin;
            if (load != null) link.LoadTime = item => SimuLean.Expressions.Samplers.Sample(load, item, origin);
            if (unload != null) link.UnloadTime = item => SimuLean.Expressions.Samplers.Sample(unload, item, origin);
            if (travel != null) link.TravelTime = (item, dest) => SimuLean.Expressions.Samplers.Sample(travel, item, origin);
        }

        private static IRandomSampler Sampler(ElementConfig ec, string key, SimClock clock)
        {
            string spec = ec.GetParameter<string>(key, "");
            return string.IsNullOrWhiteSpace(spec) ? null : SamplerSpec.Decode(spec, clock, ec.Name, key);
        }


        private static StationResources Use(ElementConfig ec, string teamKey, string reqKey, string prioKey,
                                            IReadOnlyDictionary<string, ResourceDispatcher> teams, Element element)
        {
            string team = ec.GetParameter<string>(teamKey, "");
            if (string.IsNullOrEmpty(team)) return null;
            ResourceDispatcher d;
            if (!teams.TryGetValue(team, out d))
                throw new ArgumentException("Element '" + ec.Name + "': resource team '" + team + "' does not exist.");
            var reqs = new List<ResourceRequirement>();
            foreach (string rec in ec.GetParameter<List<string>>(reqKey, null) ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(rec)) reqs.Add(DecodeRequirement(rec));
            var use = new StationResources(d, reqs.ToArray());
            string priority = ec.GetParameter<string>(prioKey, "");
            if (!string.IsNullOrWhiteSpace(priority)) use.Priority = PriorityOf(priority, element);
            return use;
        }

        /// <summary><c>count#query#label</c> (query = <see cref="ListQuerySpec"/> text).</summary>
        public static string EncodeRequirement(int count, string query, string label)
            => count.ToString(CultureInfo.InvariantCulture) + "#" + (query ?? "") + "#" + (label ?? "");

        public static ResourceRequirement DecodeRequirement(string rec)
        {
            string[] p = rec.Split('#');
            int count;
            if (!int.TryParse(p[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1)
                throw new FormatException("Resource requirement '" + rec + "': expected 'count#query#label' with count >= 1.");
            string q = p.Length > 1 ? p[1] : "";
            string label = p.Length > 2 ? p[2] : null;
            return new ResourceRequirement(count, string.IsNullOrWhiteSpace(q) ? null : ListQuerySpec.Decode(q).ToQuery(),
                                           string.IsNullOrEmpty(label) ? null : label);
        }
    }
}
