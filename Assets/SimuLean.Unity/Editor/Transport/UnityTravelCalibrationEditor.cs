using System.IO;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Inspector for <see cref="UnityTravelCalibration"/>: fit from the run, report, scene vs headless.</summary>
    [CustomEditor(typeof(UnityTravelCalibration))]
    public class UnityTravelCalibrationEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var cal = (UnityTravelCalibration)target;
            var team = cal.team;
            if (team == null) { EditorGUILayout.HelpBox("Choose a team with Unity Physics travel.", MessageType.Info); return; }
            if (team.travel != TeamTravel.UnityPhysics)
                EditorGUILayout.HelpBox($"'{team.TeamName}' does not travel with Unity physics.", MessageType.Warning);

            EditorGUILayout.Space();
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField($"Moves measured so far: {cal.MovesSoFar}");
                using (new EditorGUI.DisabledScope(cal.MovesSoFar == 0))
                {
                    if (GUILayout.Button("Fit from this run"))
                    {
                        if (team.calibration == null) team.calibration = CreateAsset(team);
                        cal.FitFromRun();
                        EditorUtility.SetDirty(team.calibration);
                        AssetDatabase.SaveAssets();
                    }
                    using (new EditorGUI.DisabledScope(team.calibration == null || team.calibration.IsEmpty))
                        if (GUILayout.Button("Compare with headless (calibrated)")) cal.Compare();
                }
            }
            else EditorGUILayout.HelpBox("Run the scene (paced clock, a moderate time scale), then fit here in Play mode.", MessageType.None);

            var a = team.calibration;
            if (a != null && !a.IsEmpty)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Calibration", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"time = {a.a:F3} + {a.b:F4}·distance + {a.c:F3}·loaded");
                EditorGUILayout.LabelField($"R² {a.r2:F3}   residual sd {a.residualSd:F3}   {a.samples} moves");
                EditorGUILayout.LabelField($"pairs: {a.pairs.Count} seen, {a.UsablePairs} with ≥ {a.minSamples} moves (used as measured)");
                EditorGUILayout.LabelField(a.source, EditorStyles.miniLabel);
                if (a.r2 < 0.5) EditorGUILayout.HelpBox("Distance explains little of the measured times (congestion, detours?): measure longer, or rely on pairs.", MessageType.Warning);
            }
            if (cal.LastComparison.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Scene (physics) vs headless (calibrated)", EditorStyles.boldLabel);
                foreach (var r in cal.LastComparison)
                {
                    double diff = r.Scene != 0 ? (r.Headless - r.Scene) / System.Math.Abs(r.Scene) : 0;
                    EditorGUILayout.LabelField(r.Name, $"{r.Scene:G4}  vs  {r.Headless:G4}   ({diff:+0.0%;-0.0%;0%})");
                }
                EditorGUILayout.HelpBox("Different random numbers in each run: compare over long runs. Calibration holds near the measured operating point.", MessageType.None);
            }
        }

        private static TravelCalibrationAsset CreateAsset(UnityResourceTeam team)
        {
            var asset = ScriptableObject.CreateInstance<TravelCalibrationAsset>();
            string scene = team.gameObject.scene.path;
            string dir = string.IsNullOrEmpty(scene) ? "Assets" : Path.GetDirectoryName(scene).Replace('\\', '/');
            string path = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{team.TeamName}_TravelCalibration.asset");
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
    }
}
