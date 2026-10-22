using UnityEditor;
using UnityEngine;
using SimuLean.Optimization.Editor;
using SimuLean.Experimentation.Controller;

namespace SimuLean.Experimentation.Editor
{
    [CustomEditor(typeof(Experimenter))]
    public class ExperimenterEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => ((Experimenter)target).IsRunning;

        public override void OnInspectorGUI()
        {
            var exp = (Experimenter)target;
            serializedObject.Update();
            SimuLean.Unity.Editor.SimuLeanGUI.ToolHeader(exp, exp.IsRunning ? "RUNNING" : null);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("modelRoot"),
                new GUIContent("Model Root (empty = scene)", "The model's root GameObject; empty = every SimuLean object of the scene."));
            EditorGUILayout.Space(4);
            var parameters = serializedObject.FindProperty("parameters");
            var scenarioList = serializedObject.FindProperty("scenarios");
            SpecGUI.ParameterList(parameters, "Parameters", ranges: false, remove: i => RemoveParameter(parameters, scenarioList, i));
            EditorGUILayout.Space(4);
            var infos = new System.Collections.Generic.List<SimuLean.Optimization.ParameterInfo>();
            foreach (var spec in exp.Parameters) infos.Add(SpecGUI.Info(spec));   // the kind of each value (null: schedule order / text)
            DrawScenarios(serializedObject.FindProperty("scenarios"), parameters, infos);
            EditorGUILayout.Space(4);
            SpecGUI.MetricList(serializedObject.FindProperty("metrics"), "Metrics");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Runs", EditorStyles.boldLabel);
            var headless = serializedObject.FindProperty("headlessMode");
            string unit = SimuLean.Unity.Editor.SimuLeanGUI.TimeUnit;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("runTime"), new GUIContent($"Run Time ({unit})", "Length of each run; 0 = the clock's Max Simulation Time."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("warmupTime"), new GUIContent($"Warm-up Time ({unit})", "Statistics start after it; 0 = none."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("headlessMode"));
            if (headless.boolValue) EditorGUILayout.PropertyField(serializedObject.FindProperty("threads"),
                new GUIContent("Threads (0 = auto)", "Runs at once: 0 = one per processor up to 8, 1 = one after another."));
            else EditorGUILayout.HelpBox("In the scene: every scenario × replication runs visibly in Play mode, one after another, with the same seeds as the headless runs.", MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("randomSeed"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exportFormats"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exportElementStats"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("seriesInterval"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("recordTimeline"));   // It had no field

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !exp.IsRunning;
            if (GUILayout.Button("Validate", GUILayout.Height(28)))
            {
                string problem = exp.Validate();
                if (problem == null) Debug.Log("[Experimenter] The set-up is valid.");
                else Debug.LogWarning("[Experimenter] " + problem);
            }
            GUI.enabled = !exp.IsRunning && (headless.boolValue || Application.isPlaying);
            if (GUILayout.Button("▶ Run", GUILayout.Height(28))) exp.StartExperiments();
            GUI.enabled = exp.IsRunning;
            if (GUILayout.Button("■ Stop", GUILayout.Height(28), GUILayout.Width(70))) exp.CancelExperiments();
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Status", exp.Status, EditorStyles.wordWrappedLabel);

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawScenarios(SerializedProperty scenarios, SerializedProperty parameters, System.Collections.Generic.List<SimuLean.Optimization.ParameterInfo> infos)
        {
            EditorGUILayout.LabelField("Scenarios", EditorStyles.boldLabel);
            for (int s = 0; s < scenarios.arraySize; s++)
            {
                var row = scenarios.GetArrayElementAtIndex(s);
                var values = row.FindPropertyRelative("parameterValues");
                values.arraySize = parameters.arraySize;   // one value per parameter
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(row.FindPropertyRelative("scenarioName"), GUIContent.none);
                EditorGUILayout.LabelField("reps", GUILayout.Width(30));
                var reps = row.FindPropertyRelative("replications");
                reps.intValue = Mathf.Max(1, EditorGUILayout.IntField(reps.intValue, GUILayout.Width(40)));
                bool remove = GUILayout.Button("✕", GUILayout.Width(22));
                EditorGUILayout.EndHorizontal();
                for (int p = 0; p < parameters.arraySize; p++)
                {
                    string label = parameters.GetArrayElementAtIndex(p).FindPropertyRelative("name").stringValue;
                    SpecGUI.Value(values.GetArrayElementAtIndex(p), p < infos.Count ? infos[p] : null, string.IsNullOrEmpty(label) ? $"Parameter {p + 1}" : label);
                }
                EditorGUILayout.EndVertical();
                if (remove) { scenarios.DeleteArrayElementAtIndex(s); break; }
            }
            if (GUILayout.Button("+ Scenario"))
            {
                string name = NewScenarioName(scenarios);
                scenarios.InsertArrayElementAtIndex(scenarios.arraySize);   // copies the last row (a good start for the next)
                scenarios.GetArrayElementAtIndex(scenarios.arraySize - 1).FindPropertyRelative("scenarioName").stringValue = name;
            }
        }

        /// <summary>
        /// Removes parameter <paramref name="index"/> and its value in every scenario (only the list was
        /// shortened, so each scenario lost its LAST value and the values after <paramref name="index"/> moved to the
        /// next parameter).
        /// </summary>
        public static void RemoveParameter(SerializedProperty parameters, SerializedProperty scenarios, int index)
        {
            parameters.DeleteArrayElementAtIndex(index);
            for (int s = 0; s < scenarios.arraySize; s++)
            {
                var values = scenarios.GetArrayElementAtIndex(s).FindPropertyRelative("parameterValues");
                if (index < values.arraySize) values.DeleteArrayElementAtIndex(index);
            }
        }

        /// <summary>The first "Scenario k" no scenario uses ("Scenario n+1" could repeat a name after a
        /// delete, and scenarios are told apart by name).</summary>
        public static string NewScenarioName(SerializedProperty scenarios)
        {
            var used = new System.Collections.Generic.HashSet<string>();
            for (int s = 0; s < scenarios.arraySize; s++) used.Add(scenarios.GetArrayElementAtIndex(s).FindPropertyRelative("scenarioName").stringValue);
            for (int k = scenarios.arraySize + 1; ; k++)
                if (!used.Contains($"Scenario {k}")) return $"Scenario {k}";
        }
    }
}
