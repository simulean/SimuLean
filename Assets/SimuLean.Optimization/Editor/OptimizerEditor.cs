using UnityEditor;
using UnityEngine;
using SimuLean.Expressions;
using SimuLean.Optimization.Evaluator;

namespace SimuLean.Optimization.Editor
{
    [CustomEditor(typeof(Optimizer))]
    public class OptimizerEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => ((Optimizer)target).IsRunning;

        public override void OnInspectorGUI()
        {
            var opt = (Optimizer)target;
            serializedObject.Update();
            SimuLean.Unity.Editor.SimuLeanGUI.ToolHeader(opt);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("modelRoot"),
                new GUIContent("Model Root (empty = scene)", "The model's root GameObject; empty = every SimuLean object of the scene."));
            EditorGUILayout.Space(4);
            SpecGUI.ParameterList(serializedObject.FindProperty("parameters"), "Parameters");
            EditorGUILayout.Space(4);
            SpecGUI.MetricList(serializedObject.FindProperty("metrics"), "Metrics");
            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("Objective (maximised)", EditorStyles.boldLabel);
            var fitness = serializedObject.FindProperty("fitness");
            EditorGUILayout.PropertyField(fitness, new GUIContent("Fitness"));
            string error = Expression.Check(fitness.stringValue);
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
            else EditorGUILayout.LabelField(" ", "metric names are variables; negate to minimise", EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Search", EditorStyles.boldLabel);
            SpecGUI.Algorithm(serializedObject.FindProperty("algorithm"));
            string unit = SimuLean.Unity.Editor.SimuLeanGUI.TimeUnit;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("replications"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("runTime"), new GUIContent($"Run Time ({unit})", "Length of each run; 0 = the clock's Max Simulation Time."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("seed"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxEvaluations"), new GUIContent("Max Evaluations (0 = algorithm)", "Stop after this many simulated candidates; 0 = when the algorithm ends."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxSeconds"), new GUIContent("Max Seconds (0 = no limit)", "Wall-clock time limit of the search."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("threads"), new GUIContent("Threads (0 = auto)", "Candidates simulated at once: 0 = one per processor up to 8."));
            foreach (string f in new[] { "exportFormats", "seriesInterval", "recordTimeline" })
                EditorGUILayout.PropertyField(serializedObject.FindProperty(f));

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !opt.IsRunning;
            if (GUILayout.Button("Validate", GUILayout.Height(28)))
            {
                string problem = opt.Validate();
                if (problem == null) Debug.Log("[Optimizer] The setup is valid.");
                else Debug.LogWarning("[Optimizer] " + problem);
            }
            if (GUILayout.Button("▶ Optimise", GUILayout.Height(28))) opt.StartOptimization();
            GUI.enabled = opt.IsRunning;
            if (GUILayout.Button("■ Stop", GUILayout.Height(28), GUILayout.Width(70))) opt.CancelOptimization();
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Status", opt.Status, EditorStyles.wordWrappedLabel);
            if (opt.HasBest)
            {
                EditorGUILayout.LabelField("Best fitness", opt.BestFitness.ToString("G6"));
                EditorGUILayout.LabelField("Best values", EditorStyles.miniBoldLabel);
                foreach (var v in opt.BestValues) EditorGUILayout.LabelField("  " + v, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Its metrics", EditorStyles.miniBoldLabel);
                foreach (var m in opt.BestMetrics) EditorGUILayout.LabelField("  " + m, EditorStyles.miniLabel);
            }
            else EditorGUILayout.LabelField("No result yet (results are kept until the editor reloads; the report keeps them).", EditorStyles.wordWrappedMiniLabel);
            if (opt.IsRunning) Repaint();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
