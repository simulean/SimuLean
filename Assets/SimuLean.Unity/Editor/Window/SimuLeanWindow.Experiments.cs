using System.Linq;
using SimuLean.Experimentation.Controller;
using SimuLean.Optimization.Evaluator;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    public partial class SimuLeanWindow
    {
        private bool AnyExperimentRunning()
            => InScenes<Experimenter>().Any(e => e.IsRunning) || InScenes<Optimizer>().Any(o => o.IsRunning);

        private void DrawExperiments()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            GroupHeader("Experimenter", "Experiments", "scenarios × replications");
            var experimenters = InScenes<Experimenter>().ToList();
            foreach (var e in experimenters)
                RunBlock(e, e.Status, e.IsRunning, e.StartExperiments, e.CancelExperiments,
                         $"{e.Scenarios.Count} scenario(s) · {e.Parameters.Count} parameter(s) · {e.Metrics.Count} metric(s)", null);
            CreateButton(typeof(Experimenter), "Experimenter", experimenters.Count == 0);

            GroupHeader("Optimizer", "Experiments", "search for the best parameters");
            var optimizers = InScenes<Optimizer>().ToList();
            foreach (var o in optimizers)
                RunBlock(o, o.Status, o.IsRunning, o.StartOptimization, o.CancelOptimization,
                         $"{o.Parameters.Count} parameter(s) · {o.Metrics.Count} metric(s)",
                         o.HasBest ? $"best fitness {SimuLeanGUI.Format(o.BestFitness)}: {string.Join(", ", o.BestValues)}" : null);
            CreateButton(typeof(Optimizer), "Optimizer", optimizers.Count == 0);

            var last = ResultsOutput.Last;
            if (last != null)
            {
                EditorGUILayout.Space(4);
                if (GUILayout.Button(new GUIContent($"Show results: {last.Name}", "Charts of the last results (Results tab).")))
                    OpenResults(last);
            }

            GroupHeader("Analysis", "Runtime", "reports and calibration");
            foreach (var (type, info) in tools.Where(t => t.info.Category == SimuLeanToolCategories.Analysis
                                                       || (t.info.Category == SimuLeanToolCategories.Experiments && t.type != typeof(Experimenter) && t.type != typeof(Optimizer))))
            {
                var instances = UnityCompat.FindAll(type, true).OfType<Component>().Where(c => c.gameObject.scene.IsValid()).ToList();
                var r = EditorGUILayout.GetControlRect();
                GUI.Label(new Rect(r.x + 10, r.y, r.width * 0.45f, r.height), new GUIContent(info.DisplayName, info.Description));
                GUI.Label(new Rect(r.x + 10 + r.width * 0.45f, r.y, r.width * 0.25f, r.height),
                          instances.Count == 0 ? "none" : instances.Count == 1 ? instances[0].gameObject.name : $"{instances.Count} in the scene", EditorStyles.miniLabel);
                bool single = info.Singleton && instances.Count > 0;
                if (instances.Count > 0 && GUI.Button(new Rect(r.xMax - 124, r.y, 60, r.height), "Select", EditorStyles.miniButton)) Select(instances[0]);
                if (!single && GUI.Button(new Rect(r.xMax - 60, r.y, 60, r.height), "Create", EditorStyles.miniButton)) CreateComponent(type, info.DisplayName);
            }
            EditorGUILayout.EndScrollView();
        }

        private static void RunBlock(Component c, string status, bool running, System.Action run, System.Action stop, string summary, string result)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(c.gameObject.name, EditorStyles.boldLabel)) Select(c);
            GUILayout.FlexibleSpace();
            GUI.enabled = !running;
            if (GUILayout.Button("▶ Run", EditorStyles.miniButtonLeft, GUILayout.Width(60))) run();
            GUI.enabled = running;
            if (GUILayout.Button("■ Stop", EditorStyles.miniButtonMid, GUILayout.Width(60))) stop();
            GUI.enabled = true;
            if (GUILayout.Button(new GUIContent("Set up…", "Select it: parameters, metrics and settings are in its inspector"), EditorStyles.miniButtonRight, GUILayout.Width(64))) Select(c);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Status", status, EditorStyles.wordWrappedMiniLabel);
            if (result != null) EditorGUILayout.LabelField("Result", result, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        private static void CreateButton(System.Type type, string name, bool none)
        {
            var r = EditorGUILayout.GetControlRect();
            if (none) GUI.Label(new Rect(r.x + 10, r.y, r.width * 0.6f, r.height), $"No {name} in the scene.", EditorStyles.miniLabel);
            string label = none ? $"Create {name}" : $"Add another {name}";
            float bw = EditorStyles.miniButton.CalcSize(new GUIContent(label)).x + 16;
            if (GUI.Button(new Rect(r.xMax - bw, r.y, bw, r.height), label, EditorStyles.miniButton))
                CreateComponent(type, name);
        }

        /// <summary>A new object with the component (it lands in its hierarchy group by itself).</summary>
        private static void CreateComponent(System.Type type, string name)
        {
            var go = new GameObject(GameObjectUtility.GetUniqueNameForSibling(null, name));
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Select(Undo.AddComponent(go, type));
        }
    }
}
