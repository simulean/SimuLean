using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Resources
{
    /// <summary>One move made by a resource (its move log): where, loaded or empty, when, and the distance the estimate
    /// model gives for it. Duration excludes stops (the leg's times are shifted by them).</summary>
    public struct MoveRecord
    {
        public string From, To;
        public bool Loaded;
        public double Start, End, Distance;
        public double Duration => End - Start;
    }

    /// <summary>
    /// A move whose end is <b>reported from outside</b> — Unity physics (a NavMeshAgent, a rigidbody, any controller),
    /// <see cref="Plan"/> is open-ended (duration NaN) and whoever moves the resource calls
    /// <see cref="TaskExecutor.Arrive"/>. Scene only (no headless run: there is nobody to report). An
    /// <see cref="Estimate"/> model (grid, matrix, network…) gives the distances "nearest resource" rules compare and
    /// that calibration relates the measured times to.
    /// </summary>
    public sealed class ExternalTravel : ITravelModel
    {
        public ExternalTravel(ITravelModel estimate) { Estimate = estimate ?? throw new ArgumentNullException(nameof(estimate)); }

        public ITravelModel Estimate { get; }

        public double Distance(string from, string to) => Estimate.Distance(from, to);

        public TravelPlan Plan(TaskExecutor executor, string from, string to)
        {
            double d = Estimate.Distance(from, to);
            if (double.IsPositiveInfinity(d))
                throw new InvalidOperationException($"'{executor?.GetName()}' cannot reach '{to}' from '{from}' (estimate model).");
            return new TravelPlan { Route = new[] { from, to }, Distance = d, Duration = double.NaN };
        }
    }

    /// <summary>
    /// Travel times <b>calibrated</b> from measured moves: per origin–destination pair (and loaded /
    /// empty) the mean of the observed times when it was seen at least <see cref="MinSamples"/> times; otherwise the
    /// general model <c>time = A + B · distance + C · loaded</c> on the <see cref="Estimate"/> model's distance — which
    /// still holds when the layout changes (the estimate is recomputed, e.g. the walk grid's A*). Deterministic (means).
    /// </summary>
    public sealed class CalibratedTravel : ITravelModel
    {
        private readonly Dictionary<string, PairStat> pairs = new Dictionary<string, PairStat>();

        public CalibratedTravel(ITravelModel estimate, double a, double b, double c, int minSamples = 3)
        {
            Estimate = estimate ?? throw new ArgumentNullException(nameof(estimate));
            A = a; B = b; C = c; MinSamples = Math.Max(1, minSamples);
        }

        public ITravelModel Estimate { get; }
        public double A { get; }
        public double B { get; }
        public double C { get; }
        public int MinSamples { get; }

        public CalibratedTravel AddPair(PairStat p) { pairs[Key(p.From, p.To, p.Loaded)] = p; return this; }

        public IEnumerable<PairStat> Pairs => pairs.Values;

        public double Distance(string from, string to) => Estimate.Distance(from, to);

        /// <summary>The calibrated time of a move (pair mean, else the general model).</summary>
        public double Time(string from, string to, bool loaded, out bool fromPair)
        {
            fromPair = false;
            if (from == to) return 0;
            if (pairs.TryGetValue(Key(from, to, loaded), out PairStat p) && p.N >= MinSamples) { fromPair = true; return p.Mean; }
            double d = Estimate.Distance(from, to);
            if (double.IsPositiveInfinity(d)) return double.PositiveInfinity;
            return Math.Max(0, A + B * d + (loaded ? C : 0));
        }

        public TravelPlan Plan(TaskExecutor executor, string from, string to)
        {
            bool loaded = executor != null && executor.Carried.Count > 0;
            double t = Time(from, to, loaded, out _);
            if (double.IsPositiveInfinity(t))
                throw new InvalidOperationException($"'{executor?.GetName()}' cannot reach '{to}' from '{from}' (estimate model).");
            var basePlan = Estimate.Plan(executor, from, to);   // route / path for views
            return new TravelPlan { Route = basePlan.Route, Path = basePlan.Path, Distance = basePlan.Distance, Duration = t };
        }

        internal static string Key(string from, string to, bool loaded) => from + "\u0001" + to + (loaded ? "\u0001L" : "\u0001E");

        /// <summary>Observed times of one pair: <c>from|to|loaded|mean|sd|n</c> in configs.</summary>
        public struct PairStat
        {
            public string From, To;
            public bool Loaded;
            public double Mean, Sd;
            public int N;

            public string Encode() => string.Join("|", From, To, Loaded ? "1" : "0",
                Mean.ToString("R", CultureInfo.InvariantCulture), Sd.ToString("R", CultureInfo.InvariantCulture), N.ToString(CultureInfo.InvariantCulture));

            public static PairStat Decode(string rec)
            {
                string[] f = rec.Split('|');
                if (f.Length != 6) throw new FormatException("Calibration pair '" + rec + "': expected 'from|to|loaded|mean|sd|n'.");
                return new PairStat
                {
                    From = f[0], To = f[1], Loaded = f[2] == "1",
                    Mean = double.Parse(f[3], CultureInfo.InvariantCulture), Sd = double.Parse(f[4], CultureInfo.InvariantCulture),
                    N = int.Parse(f[5], CultureInfo.InvariantCulture),
                };
            }
        }
    }

    /// <summary>
    /// Fits a <see cref="CalibratedTravel"/> from move logs: per-pair statistics and a least-squares
    /// general model <c>time = A + B · distance + C · loaded</c> (C dropped when every move was loaded or every move empty;
    /// B and A from a straight line through (distance, time) when all distances are equal). <see cref="R2"/> and the
    /// residual deviation report how well distance explains the measured times.
    /// </summary>
    public sealed class TravelCalibrationFit
    {
        public double A, B, C, R2, ResidualSd;
        public int Samples;
        public List<CalibratedTravel.PairStat> Pairs = new List<CalibratedTravel.PairStat>();

        public static TravelCalibrationFit Fit(IEnumerable<MoveRecord> moves)
        {
            var fit = new TravelCalibrationFit();
            var groups = new Dictionary<string, List<MoveRecord>>();
            var xs = new List<double[]>(); var ys = new List<double>();
            bool anyLoaded = false, anyEmpty = false;
            foreach (var m in moves)
            {
                if (m.From == null || m.To == null || m.From == m.To || !(m.Duration >= 0) || double.IsInfinity(m.Distance) || double.IsNaN(m.Distance)) continue;
                string k = CalibratedTravel.Key(m.From, m.To, m.Loaded);
                if (!groups.TryGetValue(k, out var g)) groups[k] = g = new List<MoveRecord>();
                g.Add(m);
                xs.Add(new[] { 1.0, m.Distance, m.Loaded ? 1.0 : 0.0 }); ys.Add(m.Duration);
                if (m.Loaded) anyLoaded = true; else anyEmpty = true;
            }
            fit.Samples = ys.Count;
            foreach (var g in groups.Values)
            {
                double mean = 0; foreach (var m in g) mean += m.Duration; mean /= g.Count;
                double ss = 0; foreach (var m in g) ss += (m.Duration - mean) * (m.Duration - mean);
                fit.Pairs.Add(new CalibratedTravel.PairStat
                {
                    From = g[0].From, To = g[0].To, Loaded = g[0].Loaded, Mean = mean,
                    Sd = g.Count > 1 ? Math.Sqrt(ss / (g.Count - 1)) : 0, N = g.Count,
                });
            }
            if (ys.Count == 0) return fit;

            int p = anyLoaded && anyEmpty ? 3 : 2;            // with or without the loaded term
            double[] beta = LeastSquares(xs, ys, p);
            if (beta == null)                                  // all distances equal: time = mean
            {
                double mean = 0; foreach (var y in ys) mean += y; mean /= ys.Count;
                beta = new double[] { mean, 0, 0 };
            }
            fit.A = beta[0]; fit.B = beta[1]; fit.C = p == 3 ? beta[2] : 0;
            double my = 0; foreach (var y in ys) my += y; my /= ys.Count;
            double sst = 0, sse = 0;
            for (int i = 0; i < ys.Count; i++)
            {
                double pred = fit.A + fit.B * xs[i][1] + fit.C * xs[i][2];
                sse += (ys[i] - pred) * (ys[i] - pred);
                sst += (ys[i] - my) * (ys[i] - my);
            }
            fit.R2 = sst > 0 ? 1 - sse / sst : 1;
            fit.ResidualSd = ys.Count > p ? Math.Sqrt(sse / (ys.Count - p)) : 0;
            return fit;
        }

        // Normal equations (p ≤ 3), Gaussian elimination; null when singular.
        private static double[] LeastSquares(List<double[]> xs, List<double> ys, int p)
        {
            var m = new double[p, p + 1];
            for (int i = 0; i < xs.Count; i++)
                for (int r = 0; r < p; r++)
                {
                    for (int c = 0; c < p; c++) m[r, c] += xs[i][r] * xs[i][c];
                    m[r, p] += xs[i][r] * ys[i];
                }
            for (int col = 0; col < p; col++)
            {
                int piv = col;
                for (int r = col + 1; r < p; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
                if (Math.Abs(m[piv, col]) < 1e-12) return null;
                for (int c = 0; c <= p; c++) { double t = m[col, c]; m[col, c] = m[piv, c]; m[piv, c] = t; }
                for (int r = 0; r < p; r++)
                {
                    if (r == col) continue;
                    double f = m[r, col] / m[col, col];
                    for (int c = col; c <= p; c++) m[r, c] -= f * m[col, c];
                }
            }
            var beta = new double[3];
            for (int r = 0; r < p; r++) beta[r] = m[r, p] / m[r, r];
            return beta;
        }

        /// <summary>The fitted model on <paramref name="estimate"/>.</summary>
        public CalibratedTravel ToTravel(ITravelModel estimate, int minSamples = 3)
        {
            var t = new CalibratedTravel(estimate, A, B, C, minSamples);
            foreach (var p in Pairs) t.AddPair(p);
            return t;
        }
    }
}
