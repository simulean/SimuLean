using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Collections;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>List inspector: configuration (fields, default order), the elements that push / pull, and
    /// (Play mode) the live content with one column per field, pending pulls and statistics (also per
    /// monitored field).</summary>
    [CustomEditor(typeof(UnitySimList))]
    public class UnitySimListEditor : UnityEditor.Editor
    {
        private const int MaxRows = 25;

        public override void OnInspectorGUI()
        {
            var list = (UnitySimList)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("listName"));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Fields   (name · kind · label / built-in / formula · Mon = monitor)", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("fields"), new GUIContent("Fields"), true);
            string fieldError = ValidateFields(list);
            if (fieldError != null) EditorGUILayout.HelpBox(fieldError, MessageType.Error);

            var order = serializedObject.FindProperty("defaultOrderBy");
            var options = new List<string> { "(first in, first out)" };
            options.AddRange(list.AvailableFields());
            if (!string.IsNullOrEmpty(order.stringValue) && !options.Contains(order.stringValue)) options.Add(order.stringValue);
            int index = string.IsNullOrEmpty(order.stringValue) ? 0 : options.IndexOf(order.stringValue);
            int chosen = EditorGUILayout.Popup(new GUIContent("Default Order By", "Order of the pulls without their own"), index, options.ToArray());
            order.stringValue = chosen == 0 ? "" : options[chosen];
            using (new EditorGUI.DisabledScope(chosen == 0))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultDescending"), new GUIContent("Highest First"));
            serializedObject.ApplyModifiedProperties();

            // Who uses it (edit time).
            var all = UnityCompat.FindAll<SElement>(true);
            var pushers = all.Where(e => e.PushList == list).Select(e => e.name).OrderBy(n => n).ToList();
            var pullers = all.Where(e => e.PullList == list).Select(e => e.name).ToList();
            foreach (var c in all.OfType<UnityCombiner>())
                foreach (var li in c.listInputs ?? new System.Collections.Generic.List<UnityCombiner.ListInput>())
                    if (li != null && li.list == list) pullers.Add($"{c.name} ({(li.input == 0 ? "main" : "component " + (li.input - 1))})");
            pullers.Sort();
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Push: " + (pushers.Count > 0 ? string.Join(", ", pushers) : "none"), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("Pull: " + (pullers.Count > 0 ? string.Join(", ", pullers) : "none"), EditorStyles.wordWrappedMiniLabel);

            SimList l = list.List;
            if (!Application.isPlaying || l == null)
            {
                EditorGUILayout.HelpBox("Live content, pending pulls and statistics appear here in Play mode.", MessageType.None);
                return;
            }

            double now = l.Clock.GetSimulationTime();
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Entries {l.Count}   pending pulls {l.PendingPullCount}   pushed {l.Pushed}   pulled {l.Pulled}");
            EditorGUILayout.LabelField($"Content avg {F(l.Content.GetTimeWeightedAverage(now))}   max {F(l.Content.Max)}   wait avg {F(l.WaitTime.Average)}");
            foreach (var fs in l.FieldStatistics.Values)
                EditorGUILayout.LabelField($"{fs.Field}:  on push avg {F(fs.AtPush.Average)} [{F(fs.AtPush.Min)}, {F(fs.AtPush.Max)}]   " +
                                           $"on pull avg {F(fs.AtPull.Average)} [{F(fs.AtPull.Min)}, {F(fs.AtPull.Max)}]", EditorStyles.miniLabel);

            var entries = l.Entries;
            var columns = new List<string>();
            foreach (var f in l.Fields) columns.Add(f.name);
            if (!string.IsNullOrEmpty(list.defaultOrderBy) && !columns.Contains(list.defaultOrderBy)) columns.Add(list.defaultOrderBy);
            for (int i = 0; i < entries.Count && i < MaxRows; i++)
            {
                ListEntry e = entries[i];
                string what = e.Item != null ? $"{e.Item.GetTypeName()} #{e.Item.GetId()}" : e.Value?.ToString();
                var sb = new System.Text.StringBuilder($"{what}   @{e.Owner?.GetName() ?? "-"}   age {F(now - e.PushTime)}");
                foreach (string c in columns) sb.Append($"   {c} {l.GetText(e, c) ?? "-"}");
                EditorGUILayout.LabelField(sb.ToString(), EditorStyles.miniLabel);
            }
            if (entries.Count > MaxRows) EditorGUILayout.LabelField($"… {entries.Count - MaxRows} more", EditorStyles.miniLabel);
            Repaint();
        }

        private static string F(double v) => double.IsNaN(v) ? "-" : v.ToString("0.##", CultureInfo.InvariantCulture);

        // Edit-time check of the field definitions (same rules as SimList.DefineFields).
        private static string ValidateFields(UnitySimList list)
        {
            if (list.fields == null || list.fields.Count == 0) return null;
            try
            {
                var probe = new SimList("probe", new SimClock(1));
                probe.DefineFields(list.fields);
                return null;
            }
            catch (System.Exception e) { return e.Message; }
        }
    }
}
