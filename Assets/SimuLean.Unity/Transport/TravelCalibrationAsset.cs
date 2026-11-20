using System.Collections.Generic;
using SimuLean.Resources;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Travel times of a team measured with Unity physics and fitted — an asset, so the result
    /// survives leaving Play mode and is shared by scene and headless runs. Written by <see cref="UnityTravelCalibration"/>;
    /// a team with <i>Unity Physics</i> travel exports it as its <c>Calibrated</c> travel model (pair means where seen
    /// at least <see cref="minSamples"/> times, else <c>time = A + B · distance + C · loaded</c> on the estimate model).
    /// </summary>
    public class TravelCalibrationAsset : ScriptableObject
    {
        [Min(1)] public int minSamples = 3;
        public double a, b, c, r2, residualSd;
        public int samples;
        [Tooltip("Observed pairs: from, to, loaded, mean, standard deviation and number of moves.")]
        public List<SimuLean.Serialization.CalibrationPairSettings> pairs = new List<SimuLean.Serialization.CalibrationPairSettings>();
        [Tooltip("Where / when it was measured.")]
        public string source = "";

        public bool IsEmpty => samples == 0;

        public void Store(TravelCalibrationFit fit, string from)
        {
            a = fit.A; b = fit.B; c = fit.C; r2 = fit.R2; residualSd = fit.ResidualSd; samples = fit.Samples;
            pairs = fit.Pairs.ConvertAll(SimuLean.Serialization.CalibrationPairSettings.Of);
            source = from;
        }

        /// <summary>The pairs seen at least <see cref="minSamples"/> times (used as they are).</summary>
        public int UsablePairs
        {
            get
            {
                int n = 0;
                foreach (var p in pairs) if (p != null && p.N >= minSamples) n++;
                return n;
            }
        }

        public void ApplyTo(SimuLean.Serialization.ResourceTeamConfig tc, SimuLean.Serialization.TravelKind estimateKind)
        {
            tc.Travel = SimuLean.Serialization.TravelKind.Calibrated;
            tc.CalibrationBase = estimateKind;
            tc.CalibrationFit = new[] { a, b, c };
            tc.CalibrationPairs = SimuLean.Serialization.ConfigCloner.DeepClone(pairs);
            tc.CalibrationMinSamples = minSamples;
        }
    }
}
