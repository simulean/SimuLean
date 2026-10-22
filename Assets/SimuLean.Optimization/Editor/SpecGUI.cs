using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using SimuLean.Unity;

namespace SimuLean.Optimization.Editor
{
    /// <summary>
    /// Inspector drawing of <see cref="ParameterSpec"/> / <see cref="MetricSpec"/> lists and of the algorithm choice, from
    /// the catalogs (what each element / team offers, the statistics of each scope): shared by the Optimizer and the
    /// Experimenter.
    /// </summary>
    public static class SpecGUI
    {
        // What each owner offers, kept until the scene or an undo changes something: building it extracts the owner's config,
        // and the inspector repaints many times a second.
        private static readonly Dictionary<string, IReadOnlyList<ParameterInfo>> optionsCache = new Dictionary<string, IReadOnlyList<ParameterInfo>>();
        private static readonly Dictionary<string, int> rowsCache = new Dictionary<string, int>();

        [InitializeOnLoadMethod]
        private static void WatchChanges()
        {
            ObjectChangeEvents.changesPublished += (ref ObjectChangeEventStream _) => ClearCache();
            Undo.undoRedoPerformed += ClearCache;
            EditorApplication.hierarchyChanged += ClearCache;
            EditorApplication.playModeStateChanged += _ => ClearCache();
        }

        private static void ClearCache() { optionsCache.Clear(); rowsCache.Clear(); }

        /// <summary><see cref="ParameterSpec.Options"/>, cached for the inspector.</summary>
        public static IReadOnlyList<ParameterInfo> Options(ParameterSpec spec)
        {
            UnityEngine.Object owner = spec.target == ParameterTarget.Team ? spec.team
                                     : spec.target == ParameterTarget.Downtime ? (UnityEngine.Object)spec.downtime
                                     : spec.target == ParameterTarget.Reaction ? (UnityEngine.Object)spec.reaction : spec.element;
            string key = (int)spec.target + "|" + UnityCompat.ObjectId(owner);
            if (!optionsCache.TryGetValue(key, out var options)) optionsCache[key] = options = spec.Options();
            return options;
        }

        /// <summary><see cref="ParameterSpec.Info"/>, cached for the inspector.</summary>
        public static ParameterInfo Info(ParameterSpec spec)
        {
            foreach (var p in Options(spec)) if (p.Key == spec.key) return p;
            return null;
        }

        private static int ScheduleRows(SElement source)
        {
            string key = UnityCompat.ObjectId(source);
            if (!rowsCache.TryGetValue(key, out int rows))
                rowsCache[key] = rows = ModelParameterCatalog.ScheduleRows(SceneExtractor.ExtractElement(source));
            return rows;
        }
        /// <param name="ranges">Draw each parameter's range (Optimizer); the Experimenter's values come from its scenarios.</param>
        /// <param name="remove">Removes parameter i (null = from the list only); the Experimenter also removes its scenario
        /// values.</param>
        public static void ParameterList(SerializedProperty list, string title, bool ranges = true, Action<int> remove = null)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            for (int i = 0; i < list.arraySize; i++)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                if (Parameter(list.GetArrayElementAtIndex(i), ranges))
                {
                    if (remove != null) remove(i); else list.DeleteArrayElementAtIndex(i);
                    EditorGUILayout.EndVertical();
                    break;
                }
                EditorGUILayout.EndVertical();
            }
            if (GUILayout.Button("+ Parameter")) list.InsertArrayElementAtIndex(list.arraySize);
        }

        /// <summary>Draws one parameter; true when the user removes it.</summary>
        public static bool Parameter(SerializedProperty p, bool ranges = true)
        {
            var target = p.FindPropertyRelative("target");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(target, GUIContent.none);
            bool remove = GUILayout.Button("✕", GUILayout.Width(22));
            EditorGUILayout.EndHorizontal();
            var kind = (ParameterTarget)target.enumValueIndex;
            EditorGUILayout.PropertyField(p.FindPropertyRelative("name"), new GUIContent("Name"));
            string owner = kind == ParameterTarget.Team ? "team" : kind == ParameterTarget.Downtime ? "downtime"
                         : kind == ParameterTarget.Reaction ? "reaction" : "element";
            var ownerProp = p.FindPropertyRelative(owner);
            EditorGUILayout.PropertyField(ownerProp, new GUIContent(kind == ParameterTarget.Team ? "Team" : kind == ParameterTarget.Downtime ? "Downtime"
                                                                   : kind == ParameterTarget.Reaction ? "Reaction"
                                                                   : kind == ParameterTarget.Element ? "Element" : "Source"));
            if (ownerProp.objectReferenceValue == null) return remove;

            if (kind == ParameterTarget.ScheduleSequence || kind == ParameterTarget.ScheduleBinary)
            {
                int rows = ScheduleRows(ownerProp.objectReferenceValue as SElement);
                if (rows == 0) EditorGUILayout.HelpBox("Needs a source with a loaded schedule table.", MessageType.Warning);
                else EditorGUILayout.LabelField(" ", kind == ParameterTarget.ScheduleSequence ? $"Order of {rows} rows (time slots stay)" : $"{rows} yes / no decisions");
                if (kind == ParameterTarget.ScheduleBinary) EditorGUILayout.PropertyField(p.FindPropertyRelative("column"), new GUIContent("Column"));
                return remove;
            }

            var spec = (ParameterSpec)p.boxedValue;
            var options = Options(spec);
            if (options.Count == 0) { EditorGUILayout.HelpBox("Nothing to parameterise.", MessageType.Info); return remove; }
            var key = p.FindPropertyRelative("key");
            ParameterInfo info = KeyPopup(key, options);
            if (info == null)
            {
                EditorGUILayout.HelpBox($"'{key.stringValue}' is not offered by the element's current settings (e.g. another distribution " +
                                        "type). It is kept: it applies again when the settings offer it, or pick another one above.", MessageType.Warning);
                return remove;
            }
            EditorGUILayout.LabelField(" ", Describe(info), EditorStyles.miniLabel);
            bool number = info.Kind == VariableKind.Integer || info.Kind == VariableKind.Real;
            if (ranges && number)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(p.FindPropertyRelative("min"), new GUIContent(info.Length > 1 ? "Range (each value)" : "Range"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("max"), GUIContent.none);
                if (GUILayout.Button(new GUIContent("Suggest", $"Range {info.Min:G4} – {info.Max:G4} around the current value"), GUILayout.Width(60)))
                {
                    p.FindPropertyRelative("min").doubleValue = info.Min;
                    p.FindPropertyRelative("max").doubleValue = info.Max;
                }
                EditorGUILayout.EndHorizontal();
            }
            return remove;
        }

        /// <summary>"integer ×3, now 1, 2, 3" / "choice of 4, now RoundRobin" / "yes / no, now true".</summary>
        public static string Describe(ParameterInfo info)
        {
            string what = info.Kind == VariableKind.Choice ? $"choice of {info.Options.Count}"
                        : info.Kind == VariableKind.Binary ? "yes / no"
                        : (info.Kind == VariableKind.Integer ? "integer" : "real") + (info.Length > 1 ? " ×" + info.Length : "");
            return $"{what}, now {info.CurrentText}";
        }

        /// <summary>A scenario value of a parameter, drawn by its kind: a popup for a choice, a toggle for a yes / no, a
        /// number field, or text for vectors / sequences.</summary>
        public static void Value(SerializedProperty value, ParameterInfo info, string label)
        {
            var content = new GUIContent(label, info != null ? Describe(info) : "a number, an option, true / false, or values separated by commas");
            if (info != null && info.Kind == VariableKind.Choice)
            {
                var opts = info.Options.ToList();
                int i = Math.Max(0, opts.IndexOf(value.stringValue));
                if (opts.IndexOf(value.stringValue) < 0 && string.IsNullOrEmpty(value.stringValue)) i = (int)info.Current;
                value.stringValue = opts[EditorGUILayout.Popup(content, i, opts.Select(o => new GUIContent(o)).ToArray())];
            }
            else if (info != null && info.Kind == VariableKind.Binary && info.Length == 1)
            {
                bool on = string.IsNullOrEmpty(value.stringValue) ? info.Current > 0.5
                        : value.stringValue == "1" || value.stringValue.Equals("true", StringComparison.OrdinalIgnoreCase);
                value.stringValue = EditorGUILayout.Toggle(content, on) ? "true" : "false";
            }
            else
            {
                if (string.IsNullOrEmpty(value.stringValue) && info != null) value.stringValue = info.CurrentText;
                value.stringValue = EditorGUILayout.TextField(content, value.stringValue);
            }
        }

        /// <summary>
        /// The key's popup. A saved key the current options do not offer (the element's distribution changed, say) is
        /// shown as such and kept until the user picks another (null returned): drawing the inspector used to replace it
        /// with the first option, silently, and dirty the scene.
        /// </summary>
        private static ParameterInfo KeyPopup(SerializedProperty key, IReadOnlyList<ParameterInfo> options)
        {
            int found = SelectedOption(key.stringValue, options);
            if (found < 0)
            {
                var labels = new[] { key.stringValue + " (not offered now)" }.Concat(options.Select(o => o.Label)).ToArray();
                int chosen = EditorGUILayout.Popup("What", 0, labels);
                if (chosen == 0) return null;
                key.stringValue = options[chosen - 1].Key;
                return options[chosen - 1];
            }
            int index = EditorGUILayout.Popup("What", found, options.Select(o => o.Label).ToArray());
            if (index != found || string.IsNullOrEmpty(key.stringValue)) key.stringValue = options[index].Key;
            return options[index];
        }

        /// <summary>The option of <paramref name="key"/>: its index; 0 for a new parameter (no key yet); −1 for a saved key
        /// the options do not offer (kept as it is).</summary>
        public static int SelectedOption(string key, IReadOnlyList<ParameterInfo> options)
        {
            int i = options.ToList().FindIndex(o => o.Key == key);
            if (i >= 0) return i;
            return string.IsNullOrEmpty(key) ? 0 : -1;
        }

        public static void MetricList(SerializedProperty list, string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            for (int i = 0; i < list.arraySize; i++)
            {
                var m = list.GetArrayElementAtIndex(i);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(m.FindPropertyRelative("name"), GUIContent.none);
                var scope = m.FindPropertyRelative("scope");
                EditorGUILayout.PropertyField(scope, GUIContent.none, GUILayout.Width(80));
                bool remove = GUILayout.Button("✕", GUILayout.Width(22));
                EditorGUILayout.EndHorizontal();
                var s = (MetricScope)scope.enumValueIndex;
                if (s != MetricScope.Model)   // the model's statistics (makespan, deadlocked…) have no target
                    EditorGUILayout.PropertyField(m.FindPropertyRelative(s == MetricScope.Element ? "element" : s == MetricScope.Team ? "team"
                                                                       : s == MetricScope.Reaction ? "reaction" : "list"), new GUIContent(s.ToString()));
                var stat = m.FindPropertyRelative("statistic");
                var options = MetricSpec.StatisticsOf(s).ToList();
                if (s == MetricScope.Element) { options.Add("state:…"); options.Add("member:…"); }
                int idx = options.IndexOf(stat.stringValue);
                foreach (string prefix in new[] { "state:", "member:" })
                    if (stat.stringValue.StartsWith(prefix, StringComparison.Ordinal)) idx = options.IndexOf(prefix + "…");
                int sel = EditorGUILayout.Popup("Statistic", Math.Max(0, idx), options.ToArray());
                if (options[sel].EndsWith(":…", StringComparison.Ordinal))
                {
                    string prefix = options[sel].Substring(0, options[sel].Length - 1);
                    bool state = prefix == "state:";
                    string value = stat.stringValue.StartsWith(prefix, StringComparison.Ordinal) ? stat.stringValue.Substring(prefix.Length) : state ? "PROCESSING" : "";
                    stat.stringValue = prefix + EditorGUILayout.TextField(new GUIContent(state ? "State" : "Member",
                        state ? "Share of time in this state." : "A public number of the engine element (property or method without parameters), e.g. GetMeanServiceTime."), value);
                }
                else stat.stringValue = options[sel];
                EditorGUILayout.EndVertical();
                if (remove) { list.DeleteArrayElementAtIndex(i); break; }
            }
            if (GUILayout.Button("+ Metric")) list.InsertArrayElementAtIndex(list.arraySize);
        }

        /// <summary>Choice of the algorithm (every <see cref="AlgorithmSettings"/> subclass) and its settings.</summary>
        public static void Algorithm(SerializedProperty algorithm)
        {
            var types = TypeCache.GetTypesDerivedFrom<AlgorithmSettings>().Where(t => !t.IsAbstract).OrderBy(t => t.Name).ToList();
            var names = types.Select(t => ((AlgorithmSettings)Activator.CreateInstance(t)).DisplayName).ToArray();
            object current = algorithm.managedReferenceValue;
            int index = current != null ? types.IndexOf(current.GetType()) : -1;
            int chosen = EditorGUILayout.Popup("Algorithm", index, names);
            if (chosen != index && chosen >= 0)
            {
                algorithm.managedReferenceValue = Activator.CreateInstance(types[chosen]);
                return;
            }
            if (current == null) return;
            EditorGUI.indentLevel++;
            var it = algorithm.Copy();
            var end = it.GetEndProperty();
            if (it.NextVisible(true))
                while (!SerializedProperty.EqualContents(it, end)) { EditorGUILayout.PropertyField(it, true); if (!it.NextVisible(false)) break; }
            EditorGUI.indentLevel--;
        }
    }
}
