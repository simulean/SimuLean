using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Data;
using SimuLean.Results;
using SimuLean.Results.Charts;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Inspector of a <see cref="ChartDefinition"/>: only the fields the chosen kind uses, a ▾ beside every name
    /// listing what the results have (metrics, statistics, parameters, objects, scenarios — from the last results, or the
    /// first stored set), and a preview drawn by the engine, as in the Results tab and the PNG.
    /// </summary>
    [CustomEditor(typeof(ChartDefinition))]
    public sealed class ChartDefinitionEditor : UnityEditor.Editor
    {
        private Texture2D preview;
        private string previewKey, dataKey;
        private ChartData data;
        private Dictionary<string, ResultTable> names;
        private object namesFrom;

        public override void OnInspectorGUI()
        {
            var def = (ChartDefinition)target;
            serializedObject.Update();
            var spec = serializedObject.FindProperty("spec");
            var kind = (ChartKind)spec.FindPropertyRelative("Kind").enumValueIndex;
            var tables = Tables(def);

            EditorGUILayout.PropertyField(spec.FindPropertyRelative("Title"), new GUIContent("Title", "Empty = made from what is plotted."));
            EditorGUILayout.PropertyField(spec.FindPropertyRelative("Kind"));
            if (kind == ChartKind.Stacked || kind == ChartKind.Area)
                EditorGUILayout.HelpBox("This kind is drawn from live data (the element inspector, dashboards), not from results.", MessageType.Info);

            bool statesOnly = kind == ChartKind.States || kind == ChartKind.Gantt;
            if (kind == ChartKind.JobGantt)
                NameField(spec.FindPropertyRelative("Y").FindPropertyRelative("Objects"), "Items", "Item names (Type n) or * patterns separated by ; (empty = all).", null, true);
            if (kind != ChartKind.JobGantt)
                Value(spec.FindPropertyRelative("Y"), kind == ChartKind.Scatter ? "Y" : statesOnly ? "Objects" : "Value", tables, statesOnly);
            if (kind == ChartKind.Scatter) Value(spec.FindPropertyRelative("X"), "X", tables, false);
            if (kind == ChartKind.Bar || kind == ChartKind.Box) EditorGUILayout.PropertyField(spec.FindPropertyRelative("GroupBy"), new GUIContent("Categories"));
            NameField(spec.FindPropertyRelative("Scenarios"), "Scenarios", "Names or * patterns separated by ; (empty = all).",
                      tables == null ? null : Distinct(tables, "runs", "scenario").Concat(Distinct(tables, "metrics", "scenario")).Distinct(), true);
            if (kind == ChartKind.Histogram) EditorGUILayout.PropertyField(spec.FindPropertyRelative("Bins"), new GUIContent("Bins", "0 = Sturges' rule."));
            if (kind == ChartKind.Gantt || kind == ChartKind.JobGantt)
            {
                EditorGUILayout.PropertyField(spec.FindPropertyRelative("Replication"), new GUIContent("Replication", "Of the first scenario kept (0 = the first one)."));
                EditorGUILayout.PropertyField(spec.FindPropertyRelative("From"), new GUIContent("From", "Time window (From = To = 0: the whole run)."));
                EditorGUILayout.PropertyField(spec.FindPropertyRelative("To"));
                if (tables != null && !tables.ContainsKey("timeline"))
                    EditorGUILayout.HelpBox("These results have no timeline: switch on Record timeline (clock, Experimenter, Optimizer, Study).", MessageType.Info);
            }
            if (kind == ChartKind.Bar)
            {
                EditorGUILayout.PropertyField(spec.FindPropertyRelative("Confidence"));
                EditorGUILayout.PropertyField(spec.FindPropertyRelative("ValueLabels"), new GUIContent("Values on bars"));
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("source"));
            if (def.source == ChartDataSource.StoredSets)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("storedSets"), new GUIContent("Stored sets (#id)"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exportSize"));
            serializedObject.ApplyModifiedProperties();

            // preview
            EditorGUILayout.Space();
            string newDataKey = EditorJsonUtility.ToJson(def) + "|" + RuntimeHelpersId(namesFrom);
            if (data == null || newDataKey != dataKey)   // built when the definition or the results change, not on every GUI event
            {
                data = def.Build(def.source == ChartDataSource.ShownResults ? tables : null, ResultsOutput.ProjectRoot);
                dataKey = newDataKey;
            }
            var rect = GUILayoutUtility.GetRect(10, 240, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && rect.width > 20)
            {
                float ppp = EditorGUIUtility.pixelsPerPoint;
                int w = Mathf.RoundToInt(rect.width * ppp), h = Mathf.RoundToInt(rect.height * ppp);
                string key = dataKey + w + "x" + h;
                if (preview == null || key != previewKey)
                {
                    preview = ChartTextures.ToTexture(ChartRenderer.Render(data, w, h, EditorGUIUtility.isProSkin ? ChartTheme.Dark : ChartTheme.Light, ppp), preview);
                    previewKey = key;
                }
                GUI.DrawTexture(rect, preview);
            }
            EditorGUILayout.LabelField(def.source == ChartDataSource.ShownResults ? "Preview with the last results of this session." : "Preview with the stored sets.",
                                       EditorStyles.miniLabel);
            if (GUILayout.Button("Open in Results")) SimuLeanWindow.OpenResults(null);
        }

        private void OnDisable()
        {
            if (preview != null) DestroyImmediate(preview);
        }

        private static int RuntimeHelpersId(object o) => o == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

        // ── a value: source, name, kind of object, objects ───────────────────
        private void Value(SerializedProperty v, string label, Dictionary<string, ResultTable> tables, bool statesOnly)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            var source = v.FindPropertyRelative("Source");
            var scope = v.FindPropertyRelative("Scope");
            if (!statesOnly) EditorGUILayout.PropertyField(source);
            var src = statesOnly ? ChartSource.Statistic : (ChartSource)source.enumValueIndex;
            if (src == ChartSource.Statistic)
            {
                var scopes = Enum.GetNames(typeof(ResultScope)).Select(n => n.ToLowerInvariant()).ToArray();
                int i = Math.Max(0, Array.IndexOf(scopes, scope.stringValue));
                scope.stringValue = scopes[EditorGUILayout.Popup(new GUIContent("Kind of object"), i, scopes.Select(n => new GUIContent(n)).ToArray())];
            }
            if (!statesOnly)
            {
                IEnumerable<string> options = null;
                if (tables != null)
                    options = src == ChartSource.Metric ? Distinct(tables, "metrics", "metric")
                            : src == ChartSource.Parameter ? Distinct(tables, "scenarios", "parameter")
                            : RowsWhere(tables, "results", "scope", scope.stringValue, "metric").Where(m => !ResultMetrics.IsState(m));
                NameField(v.FindPropertyRelative("Name"), src == ChartSource.Statistic ? "Statistic" : src == ChartSource.Metric ? "Metric" : "Parameter",
                          "Choose from the results with ▾.", options, false);
            }
            if (src == ChartSource.Statistic)
                NameField(v.FindPropertyRelative("Objects"), "Objects", "Names or * patterns separated by ; (empty = all of that kind).",
                          tables == null ? null : RowsWhere(tables, "results", "scope", scope.stringValue, "name"), true);
            EditorGUI.indentLevel--;
        }

        /// <summary>A text field with a ▾ menu of the names found; <paramref name="add"/> = the menu adds to a ; list.</summary>
        private static void NameField(SerializedProperty p, string label, string tip, IEnumerable<string> options, bool add)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(p, new GUIContent(label, tip));
            var list = options?.Where(o => !string.IsNullOrEmpty(o)).Distinct().OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
            GUI.enabled = list != null && list.Count > 0;
            if (GUILayout.Button(new GUIContent("▾", list == null ? "No results to list names from." : "Names in the results."), EditorStyles.miniButton, GUILayout.Width(22)))
            {
                var menu = new GenericMenu();
                var so = p.serializedObject;
                string path = p.propertyPath;
                if (add) menu.AddItem(new GUIContent("(all)"), false, () => { so.Update(); so.FindProperty(path).stringValue = ""; so.ApplyModifiedProperties(); });
                foreach (string o in list.Take(200))
                {
                    string value = o;
                    menu.AddItem(new GUIContent(value.Replace("/", "∕")), false, () =>
                    {
                        so.Update();
                        var prop = so.FindProperty(path);
                        var current = ChartBuilder.Patterns(prop.stringValue);
                        prop.stringValue = add && current.Count > 0 && !current.Contains(value) ? prop.stringValue.TrimEnd(' ', ';') + ";" + value : value;
                        so.ApplyModifiedProperties();
                    });
                }
                menu.ShowAsContext();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        // ── where the names come from ────────────────────────────────────────
        private Dictionary<string, ResultTable> Tables(ChartDefinition def)
        {
            object from = ResultsOutput.Last;
            if (def.source == ChartDataSource.StoredSets && def.storedSets.Count > 0) from = def.storedSets[0];
            if (Equals(from, namesFrom) && names != null) return names;
            namesFrom = from;
            names = null;
            try
            {
                if (from is ResultSet set) names = ChartDefinition.Tables(set);
                else if (from is int id) names = ChartDefinition.Tables(ResultsOutput.Store(ResultsOutput.ProjectRoot).Load(id));
            }
            catch (Exception) { names = null; }
            return names;
        }

        private static IEnumerable<string> Distinct(Dictionary<string, ResultTable> tables, string table, string column)
        {
            if (!tables.TryGetValue(table, out var t)) yield break;
            int c = Col(t, column);
            if (c < 0) yield break;
            foreach (var r in t.Rows) yield return Convert.ToString(r[c], CultureInfo.InvariantCulture);
        }

        private static IEnumerable<string> RowsWhere(Dictionary<string, ResultTable> tables, string table, string whereCol, string whereValue, string column)
        {
            if (!tables.TryGetValue(table, out var t)) yield break;
            int w = Col(t, whereCol), c = Col(t, column);
            if (w < 0 || c < 0) yield break;
            foreach (var r in t.Rows)
                if (string.Equals(Convert.ToString(r[w], CultureInfo.InvariantCulture), whereValue, StringComparison.OrdinalIgnoreCase))
                    yield return Convert.ToString(r[c], CultureInfo.InvariantCulture);
        }

        private static int Col(ResultTable t, string name)
        {
            for (int i = 0; i < t.Columns.Count; i++) if (t.Columns[i] == name) return i;
            return -1;
        }
    }
}
