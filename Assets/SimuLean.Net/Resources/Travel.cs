using System;
using System.Collections.Generic;
using SimuLean.Transport;

namespace SimuLean.Resources
{
    /// <summary>
    /// A move being made by a <see cref="TaskExecutor"/> — <b>the animation contract</b>: the engine gives the
    /// route and the times, a view interpolates the position from the simulation time (the engine never computes
    /// intermediate positions). <see cref="EndTime"/> is NaN for an open-ended move (the arrival is reported to
    /// the executor with <see cref="TaskExecutor.Arrive"/>).
    /// </summary>
    public sealed class TravelLeg
    {
        public string From { get; internal set; }
        public string To { get; internal set; }

        /// <summary>Location ids along the way (network nodes), first = <see cref="From"/>; null when the model has
        /// no route (defined time).</summary>
        public IReadOnlyList<string> Route { get; internal set; }

        /// <summary>The path as points (free movement over a walk grid), null when the route (or a straight line)
        /// is the path. Views follow it when set.</summary>
        public IReadOnlyList<WayPoint> Path { get; internal set; }

        public double Distance { get; internal set; }
        public double StartTime { get; internal set; }
        public double EndTime { get; internal set; }

        /// <summary>Items on board during the move.</summary>
        public IReadOnlyList<Item> Carried { get; internal set; }

        /// <summary>Traffic (driven moves): the route segment being travelled (route[i] → route[i + 1]) and its times;
        /// -1 / NaN until the first segment starts. While waiting at a node the last segment's values stay (its end).</summary>
        public int SegmentIndex { get; internal set; } = -1;
        public double SegmentStartTime { get; internal set; } = double.NaN;
        public double SegmentEndTime { get; internal set; } = double.NaN;

        /// <summary>Simulation time the move was paused at (the resource stopped mid-way), NaN while moving. On
        /// resume the start / end times shift by the pause, so the position continues from where it stopped.</summary>
        public double PausedAt { get; internal set; } = double.NaN;

        public bool IsOpenEnded => double.IsNaN(EndTime);

        /// <summary>Fraction of the move done at <paramref name="now"/> (0 for open-ended moves; frozen while paused).</summary>
        public double Fraction(double now)
        {
            if (IsOpenEnded) return 0;   // (start shifts by stops even while open-ended)
            double d = EndTime - StartTime;
            if (d <= 0) return 1;
            double t = double.IsNaN(PausedAt) ? now : PausedAt;
            return Math.Max(0, Math.Min(1, (t - StartTime) / d));
        }
    }

    /// <summary>What a travel model plans for a move: route, distance and duration (NaN = open-ended).</summary>
    public struct TravelPlan
    {
        public IReadOnlyList<string> Route;
        public double Distance;
        public double Duration;
        /// <summary>Points of a free path (walk grid), or null.</summary>
        public IReadOnlyList<WayPoint> Path;
    }

    /// <summary>
    /// How a spatial resource travels between locations. <b>Asynchronous by design</b>:
    /// the executor begins a move with <see cref="Plan"/> and is told when it arrives — after the planned
    /// duration, or, for an open-ended plan (duration NaN: congestion, Unity physics…), when someone calls
    /// <see cref="TaskExecutor.Arrive"/>.
    /// </summary>
    public interface ITravelModel
    {
        /// <summary>Distance (or travel cost) between two locations; +∞ = unreachable. Read by "nearest
        /// resource" rules (the resource field <c>distance</c>).</summary>
        double Distance(string from, string to);

        /// <summary>Plans the move of <paramref name="executor"/> from <paramref name="from"/> to
        /// <paramref name="to"/>. Throws if the destination cannot be reached.</summary>
        TravelPlan Plan(TaskExecutor executor, string from, string to);
    }

    /// <summary>Travel on a <see cref="TransportNetwork"/>: shortest route (cached by the network) at a speed.</summary>
    public sealed class NetworkTravel : ITravelModel
    {
        private readonly TrackType[] allowed;

        public NetworkTravel(TransportNetwork network, double speed, TrackType[] allowedTypes = null)
        {
            Network = network ?? throw new ArgumentNullException(nameof(network));
            if (!(speed > 0)) throw new ArgumentException("speed must be > 0.", nameof(speed));
            Speed = speed;
            allowed = allowedTypes ?? new[] { TrackType.TravelPath };
        }

        public TransportNetwork Network { get; }
        public double Speed { get; }

        public double Distance(string from, string to)
        {
            if (from == to) return 0;
            if (Network.GetNode(from) == null || Network.GetNode(to) == null) return double.PositiveInfinity;
            return Network.GetDistance(from, to, allowed);
        }

        public TravelPlan Plan(TaskExecutor executor, string from, string to)
        {
            var route = Network.GetRoute(from, to, allowed);
            if (route == null)
                throw new InvalidOperationException($"'{executor?.GetName()}' cannot reach node '{to}' from '{from}' on the network.");
            var ids = new List<string>(route.Nodes.Count);
            foreach (var n in route.Nodes) ids.Add(n.Id);
            return new TravelPlan { Route = ids, Distance = route.Distance, Duration = route.Distance / Speed };
        }
    }

    /// <summary>
    /// Distances between locations (model data, shared by the teams that use it). Missing pairs use the reverse
    /// pair when <see cref="Symmetric"/>; a pair still missing is unreachable (+∞).
    /// </summary>
    public sealed class DistanceMatrix
    {
        private readonly Dictionary<string, double> distances = new Dictionary<string, double>();

        public DistanceMatrix(bool symmetric = true) { Symmetric = symmetric; }

        public bool Symmetric { get; }

        /// <summary>Locations that appear in the matrix.</summary>
        public HashSet<string> Locations { get; } = new HashSet<string>();

        public DistanceMatrix Set(string from, string to, double distance)
        {
            if (!(distance >= 0)) throw new ArgumentException($"Distance {from} → {to} must be >= 0.");
            distances[from + "" + to] = distance;
            Locations.Add(from); Locations.Add(to);
            return this;
        }

        public double Distance(string from, string to)
        {
            if (from == to) return 0;
            if (distances.TryGetValue(from + "" + to, out double d)) return d;
            if (Symmetric && distances.TryGetValue(to + "" + from, out d)) return d;
            return double.PositiveInfinity;
        }
    }

    /// <summary>
    /// Travel by a <see cref="DistanceMatrix"/> ÷ speed (operators walking, forklifts on a known layout): O(1) per
    /// move, no route (views go straight from one location to the other). A missing pair fails loudly when planned.
    /// </summary>
    public sealed class MatrixTravel : ITravelModel
    {
        public MatrixTravel(DistanceMatrix matrix, double speed)
        {
            Matrix = matrix ?? throw new ArgumentNullException(nameof(matrix));
            if (!(speed > 0)) throw new ArgumentException("speed must be > 0.", nameof(speed));
            Speed = speed;
        }

        public DistanceMatrix Matrix { get; }
        public double Speed { get; }

        public double Distance(string from, string to) => Matrix.Distance(from, to);

        public TravelPlan Plan(TaskExecutor executor, string from, string to)
        {
            double d = Matrix.Distance(from, to);
            if (double.IsPositiveInfinity(d))
                throw new InvalidOperationException($"'{executor?.GetName()}': the travel matrix has no distance {from} → {to}.");
            return new TravelPlan { Route = new[] { from, to }, Distance = d, Duration = d / Speed };
        }
    }

    /// <summary>
    /// <b>Kinematic travel by axes</b> (gantry cranes, AS/RS cranes, robots): between the coordinates of
    /// two locations (points of a <see cref="TransportNetwork"/>; tracks are not used), each axis x / y / z moves
    /// its distance with a trapezoidal profile — max speed, acceleration (0 = instant) — and the axes move
    /// together (time = the slowest axis) or one after the other (time = the sum). An axis with speed 0 does not
    /// move (a distance on it is unreachable). <see cref="Distance"/> is the travel time (the cost "nearest" rules
    /// compare).
    /// </summary>
    public sealed class AxisTravel : ITravelModel
    {
        private readonly double[] speed, accel;

        public AxisTravel(TransportNetwork points, double[] axisSpeed, double[] axisAccel = null, bool sequential = false)
        {
            Points = points ?? throw new ArgumentNullException(nameof(points));
            if (axisSpeed == null || axisSpeed.Length != 3) throw new ArgumentException("three axis speeds (x, y, z) expected.");
            speed = (double[])axisSpeed.Clone();
            accel = axisAccel != null && axisAccel.Length == 3 ? (double[])axisAccel.Clone() : new double[3];
            Sequential = sequential;
        }

        public TransportNetwork Points { get; }
        public bool Sequential { get; }

        /// <summary>Max speed / acceleration of axis <paramref name="i"/> (0 = x, 1 = y, 2 = z).</summary>
        public double Speed(int i) => speed[i];
        public double Accel(int i) => accel[i];

        /// <summary>Distance covered along one axis <paramref name="t"/> after starting a move of
        /// <paramref name="d"/> (same profile as <see cref="AxisTime"/>; views use it to place each axis).</summary>
        public static double AxisPosition(double d, double v, double a, double t)
        {
            d = Math.Abs(d);
            double total = AxisTime(d, v, a);
            if (d <= 0 || t <= 0) return 0;
            if (t >= total || double.IsInfinity(total)) return d;
            if (!(a > 0)) return v * t;
            double tRamp = v / a;
            if (d < v * v / a)                                  // triangular: accelerate half the way, brake the rest
            {
                double half = total / 2;
                return t <= half ? 0.5 * a * t * t : d - 0.5 * a * (total - t) * (total - t);
            }
            if (t <= tRamp) return 0.5 * a * t * t;
            if (t >= total - tRamp) return d - 0.5 * a * (total - t) * (total - t);
            return 0.5 * a * tRamp * tRamp + v * (t - tRamp);
        }

        /// <summary>Time to move <paramref name="d"/> along one axis (trapezoidal / triangular profile).</summary>
        public static double AxisTime(double d, double v, double a)
        {
            d = Math.Abs(d);
            if (d <= 0) return 0;
            if (!(v > 0)) return double.PositiveInfinity;
            if (!(a > 0)) return d / v;
            double dRamp = v * v / a;                 // accelerating to v and braking from v
            return d >= dRamp ? d / v + v / a : 2 * Math.Sqrt(d / a);
        }

        private double Time(string from, string to)
        {
            if (from == to) return 0;
            var a = Points.GetNode(from); var b = Points.GetNode(to);
            if (a == null || b == null) return double.PositiveInfinity;
            double tx = AxisTime(b.X - a.X, speed[0], accel[0]);
            double ty = AxisTime(b.Y - a.Y, speed[1], accel[1]);
            double tz = AxisTime(b.Z - a.Z, speed[2], accel[2]);
            return Sequential ? tx + ty + tz : Math.Max(tx, Math.Max(ty, tz));
        }

        public double Distance(string from, string to) => Time(from, to);

        public TravelPlan Plan(TaskExecutor executor, string from, string to)
        {
            double t = Time(from, to);
            if (double.IsPositiveInfinity(t))
                throw new InvalidOperationException($"'{executor?.GetName()}' cannot reach '{to}' from '{from}' by its axes (unknown point or an axis without speed).");
            var a = Points.GetNode(from); var b = Points.GetNode(to);
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            return new TravelPlan { Route = new[] { from, to }, Distance = Math.Sqrt(dx * dx + dy * dy + dz * dz), Duration = t };
        }
    }

    /// <summary>Travel time given by a function of (from, to) — no route; distance = the time.</summary>
    public sealed class FunctionTravel : ITravelModel
    {
        private readonly Func<string, string, double> time;

        public FunctionTravel(Func<string, string, double> time)
        {
            this.time = time ?? throw new ArgumentNullException(nameof(time));
        }

        public double Distance(string from, string to) => from == to ? 0 : time(from, to);

        public TravelPlan Plan(TaskExecutor executor, string from, string to)
        {
            double t = Distance(from, to);
            return new TravelPlan { Distance = t, Duration = t };
        }
    }
}
