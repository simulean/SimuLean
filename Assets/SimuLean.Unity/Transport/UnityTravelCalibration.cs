using System.Collections.Generic;
using SimuLean.Headless;
using SimuLean.Resources;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Calibrates a team that travels with <i>Unity Physics</i> — our way back from a physics scene
    /// to a fast 1D model: run the scene (paced clock, moderate time scale), then <see cref="FitFromRun"/> fits the
    /// members' move logs (per pair + general model on the estimate distance) into the team's
    /// <see cref="TravelCalibrationAsset"/>; headless runs (experiments, the optimiser) then use the calibrated times.
    /// <see cref="Compare"/> runs the same model headless with the calibration for the time simulated so far and puts
    /// its KPIs next to the scene's, so the user sees whether the 1D model can stand for the physics one. Calibration
    /// holds near the measured operating point: recalibrate with the best solution when fleet or layout change a lot.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Analysis, "Travel Calibration",
        Description = "Fits the travel times measured with Unity physics into a 1D model for headless runs, and compares both")]
    [AddComponentMenu("SimuLean/Travel Calibration")]
    public class UnityTravelCalibration : MonoBehaviour
    {
        [Tooltip("Team with Unity Physics travel (its calibration asset receives the fit).")]
        public UnityResourceTeam team;

        /// <summary>A KPI in the scene and headless.</summary>
        public struct Row { public string Name; public double Scene, Headless; }

        /// <summary>Last comparison (after <see cref="Compare"/>).</summary>
        public List<Row> LastComparison { get; } = new List<Row>();

        public int MovesSoFar
        {
            get
            {
                int n = 0;
                if (team != null) foreach (var ex in team.Executors) n += ex.Moves.Count;
                return n;
            }
        }

        /// <summary>Fits this run's moves into the team's calibration asset (Play mode, after some moves).</summary>
        public TravelCalibrationFit FitFromRun()
        {
            if (team == null || team.calibration == null) { Debug.LogError("[SimuLean] Travel Calibration: set a team with a calibration asset."); return null; }
            var moves = new List<MoveRecord>();
            foreach (var ex in team.Executors) moves.AddRange(ex.Moves);
            var fit = TravelCalibrationFit.Fit(moves);
            double now = UnitySimClock.Instance != null ? UnitySimClock.Instance.clock.GetSimulationTime() : 0;
            team.calibration.Store(fit, $"{gameObject.scene.name}, {moves.Count} moves up to t = {now:F1}");
            Debug.Log($"[SimuLean] Calibrated '{team.TeamName}': time = {fit.A:F3} + {fit.B:F4}·d + {fit.C:F3}·loaded (R² {fit.R2:F3}, {fit.Samples} moves, {fit.Pairs.Count} pairs).");
            return fit;
        }

        /// <summary>Runs the model headless with the calibrated travel for the time simulated so far and compares the
        /// sinks' throughput and the members' utilisation with the scene.</summary>
        public void Compare()
        {
            LastComparison.Clear();
            var sim = UnitySimClock.Instance;
            if (sim == null || team == null) return;
            double t = sim.clock.GetSimulationTime();
            if (t <= 0) return;

            SimulationConfig cfg = SceneExtractor.Extract().Config;

            var clock = new SimClock(cfg.Seed);
            var factory = new HeadlessModelFactory(clock);
            var built = factory.BuildModel(cfg);
            factory.StartModel();
            clock.AdvanceClock(t);

            foreach (var el in UnityCompat.FindAll<SElement>())
            {
                var e = el.GetElement();
                if (!(e is Sink)) continue;
                var cfgEl = cfg.Elements.Find(x => x.Name == el.name);
                if (cfgEl == null || !built.TryGetValue(cfgEl.Id, out var h)) continue;
                LastComparison.Add(new Row { Name = el.name + " (items in)", Scene = e.GetStatsInputCount(), Headless = h.GetStatsInputCount() });
            }
            foreach (var ex in team.Executors)
            {
                TaskExecutor h = null;
                foreach (var x in factory.Executors) if (x.GetName() == ex.GetName()) h = x;
                if (h != null) LastComparison.Add(new Row { Name = ex.GetName() + " (utilisation)", Scene = ex.Utilization, Headless = h.Utilization });
            }
        }
    }
}
