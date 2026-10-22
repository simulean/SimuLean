using System.Collections.Generic;
using SimuLean.Collections;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>One list field on one line: name | kind | source (label, built-in or formula) | monitor.</summary>
    [CustomPropertyDrawer(typeof(ListFieldSpec))]
    public class ListFieldSpecDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => EditorGUIUtility.singleLineHeight;

        public override void OnGUI(Rect r, SerializedProperty property, GUIContent label)
        {
            var name = property.FindPropertyRelative("name");
            var kind = property.FindPropertyRelative("kind");
            var source = property.FindPropertyRelative("source");
            var monitor = property.FindPropertyRelative("monitor");

            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            float x = r.x, w = r.width;
            var rName = new Rect(x, r.y, w * 0.24f, r.height);
            var rKind = new Rect(x + w * 0.25f, r.y, w * 0.2f, r.height);
            var rSource = new Rect(x + w * 0.46f, r.y, w * 0.44f, r.height);
            var rMon = new Rect(x + w * 0.91f, r.y, w * 0.09f, r.height);

            name.stringValue = EditorGUI.TextField(rName, name.stringValue);
            if (string.IsNullOrEmpty(name.stringValue))
                EditorGUI.LabelField(rName, "name", EditorStyles.centeredGreyMiniLabel);
            EditorGUI.PropertyField(rKind, kind, GUIContent.none);

            switch ((ListFieldKind)kind.enumValueIndex)
            {
                case ListFieldKind.BuiltIn:
                {
                    int i = System.Array.IndexOf(SimList.BuiltInFields, source.stringValue);
                    int j = EditorGUI.Popup(rSource, Mathf.Max(0, i), SimList.BuiltInFields);
                    source.stringValue = SimList.BuiltInFields[j];
                    break;
                }
                case ListFieldKind.Expression:
                {
                    source.stringValue = EditorGUI.TextField(rSource, source.stringValue);
                    string error = Validate(source.stringValue);
                    if (string.IsNullOrEmpty(source.stringValue))
                        EditorGUI.LabelField(rSource, "formula, e.g. prio * 10 + age", EditorStyles.centeredGreyMiniLabel);
                    else if (error != null)
                        EditorGUI.LabelField(rSource, new GUIContent("", error), EditorStyles.helpBox);
                    break;
                }
                default:
                    source.stringValue = EditorGUI.TextField(rSource, source.stringValue);
                    if (string.IsNullOrEmpty(source.stringValue))
                        EditorGUI.LabelField(rSource, "label (= name)", EditorStyles.centeredGreyMiniLabel);
                    break;
            }
            monitor.boolValue = EditorGUI.ToggleLeft(rMon, new GUIContent("Mon", "Monitor: record its values on push and pull"), monitor.boolValue);
            EditorGUI.indentLevel = indent;
        }

        private static string Validate(string formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) return null;
            try { SimuLean.Expressions.Expression.Parse(formula); return null; }
            catch (System.FormatException e) { return e.Message; }
        }
    }

    /// <summary>
    /// Query builder for a pull: one row per condition (field ▾ / operator ▾ / value), add / remove, and the
    /// order (field ▾, highest first). Fields come from the list the query reads (sibling <c>pullFromList</c>
    /// or <c>list</c> property); "(other…)" allows any item label.
    /// </summary>
    [CustomPropertyDrawer(typeof(ListQuerySpec))]
    public class ListQuerySpecDrawer : PropertyDrawer
    {
        private const string Other = "(other…)";
        private const string ListDefault = "(list default)";
        private static readonly string[] OpLabels = { "==", "!=", "<", "<=", ">", ">=" };
        private static float Line => EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (OrderOnly(property)) return Line * 2;   // label, order
            int conditions = property.FindPropertyRelative("conditions").arraySize;
            return Line * (3 + conditions);   // label, conditions, "+ condition", order
        }

        // A resource team's request / resource order: only the order is used.
        private static bool OrderOnly(SerializedProperty p)
            => p.serializedObject.targetObject is UnityResourceTeam && (p.name == "requestOrder" || p.name == "resourceOrder");

        public override void OnGUI(Rect r, SerializedProperty property, GUIContent label)
        {
            List<string> fields = FieldsFor(property);
            var conditions = property.FindPropertyRelative("conditions");
            var orderBy = property.FindPropertyRelative("orderBy");
            var descending = property.FindPropertyRelative("descending");

            var row = new Rect(r.x, r.y, r.width, EditorGUIUtility.singleLineHeight);
            EditorGUI.LabelField(row, label, EditorStyles.boldLabel);
            int indent = EditorGUI.indentLevel;
            Rect inner = EditorGUI.IndentedRect(new Rect(r.x, r.y, r.width, r.height));
            EditorGUI.indentLevel = 0;

            if (OrderOnly(property))
            {
                row = new Rect(inner.x + 12, r.y + Line, inner.width - 12, EditorGUIUtility.singleLineHeight);
                DrawOrder(row, orderBy, descending, fields);
                EditorGUI.indentLevel = indent;
                return;
            }

            int remove = -1;
            for (int i = 0; i < conditions.arraySize; i++)
            {
                row = new Rect(inner.x + 12, r.y + Line * (i + 1), inner.width - 12, EditorGUIUtility.singleLineHeight);
                var c = conditions.GetArrayElementAtIndex(i);
                float w = row.width;
                FieldPopup(new Rect(row.x, row.y, w * 0.4f, row.height), c.FindPropertyRelative("field"), fields, null);
                var op = c.FindPropertyRelative("op");
                op.enumValueIndex = EditorGUI.Popup(new Rect(row.x + w * 0.41f, row.y, w * 0.14f, row.height), op.enumValueIndex, OpLabels);
                var value = c.FindPropertyRelative("value");
                value.stringValue = EditorGUI.TextField(new Rect(row.x + w * 0.56f, row.y, w * 0.36f, row.height), value.stringValue);
                if (GUI.Button(new Rect(row.x + w * 0.93f, row.y, w * 0.07f, row.height), "−")) remove = i;
            }
            if (remove >= 0) conditions.DeleteArrayElementAtIndex(remove);

            row = new Rect(inner.x + 12, r.y + Line * (conditions.arraySize + 1), 110, EditorGUIUtility.singleLineHeight);
            if (GUI.Button(row, "+ condition"))
            {
                conditions.InsertArrayElementAtIndex(conditions.arraySize);
                var c = conditions.GetArrayElementAtIndex(conditions.arraySize - 1);
                c.FindPropertyRelative("field").stringValue = fields.Count > 0 ? fields[0] : "";
                c.FindPropertyRelative("op").enumValueIndex = (int)ListOp.Equal;
                c.FindPropertyRelative("value").stringValue = "";
            }

            row = new Rect(inner.x + 12, r.y + Line * (conditions.arraySize + 2), inner.width - 12, EditorGUIUtility.singleLineHeight);
            DrawOrder(row, orderBy, descending, fields);
            EditorGUI.indentLevel = indent;
        }

        private static void DrawOrder(Rect row, SerializedProperty orderBy, SerializedProperty descending, List<string> fields)
        {
            EditorGUI.LabelField(new Rect(row.x, row.y, 60, row.height), "Order by");
            FieldPopup(new Rect(row.x + 62, row.y, row.width * 0.5f, row.height), orderBy, fields, ListDefault);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(orderBy.stringValue)))
                descending.boolValue = EditorGUI.ToggleLeft(new Rect(row.x + 70 + row.width * 0.5f, row.y, row.width * 0.5f - 70, row.height),
                                                            "Highest first", descending.boolValue);
        }

        // Popup over the list's fields (+ an optional empty choice); a value not in the list is edited as text.
        private static void FieldPopup(Rect r, SerializedProperty prop, List<string> fields, string emptyChoice)
        {
            var options = new List<string>();
            if (emptyChoice != null) options.Add(emptyChoice);
            options.AddRange(fields);
            options.Add(Other);
            string current = prop.stringValue ?? "";
            int index = emptyChoice != null && current.Length == 0 ? 0 : options.IndexOf(current);
            bool custom = index < 0 || (index == options.Count - 1);
            if (custom)
            {
                float half = r.width * 0.55f;
                prop.stringValue = EditorGUI.TextField(new Rect(r.x, r.y, half, r.height), current);
                int pick = EditorGUI.Popup(new Rect(r.x + half + 2, r.y, r.width - half - 2, r.height), options.Count - 1, options.ToArray());
                if (pick != options.Count - 1) prop.stringValue = emptyChoice != null && pick == 0 ? "" : options[pick];
                return;
            }
            int chosen = EditorGUI.Popup(r, index, options.ToArray());
            if (chosen == options.Count - 1) prop.stringValue = current.Length > 0 && !fields.Contains(current) ? current : "label";
            else prop.stringValue = emptyChoice != null && chosen == 0 ? "" : options[chosen];
        }

        // The list this query reads: the sibling "pullFromList" (SElement) or "list" (combiner list input).
        private static List<string> FieldsFor(SerializedProperty property)
        {
            string path = property.propertyPath;
            // Resource teams: request order → request fields; resource order / a requirement's query → member fields.
            if (property.serializedObject.targetObject is UnityResourceTeam owner)
                return property.name == "requestOrder" ? RequestFields() : MemberFields(owner);
            int req = path.IndexOf(".requirements.", System.StringComparison.Ordinal);
            if (req >= 0)
            {
                var team = property.serializedObject.FindProperty(path.Substring(0, req) + ".team");
                return MemberFields(team?.objectReferenceValue as UnityResourceTeam);
            }
            int dot = path.LastIndexOf('.');
            string parent = dot >= 0 ? path.Substring(0, dot + 1) : "";
            foreach (string sibling in new[] { "pullFromList", "list" })
            {
                var p = property.serializedObject.FindProperty(parent + sibling);
                if (p != null && p.objectReferenceValue is UnitySimList list) return list.AvailableFields();
            }
            return new List<string>(SimList.BuiltInFields);
        }

        private static List<string> RequestFields()
        {
            var f = new List<string> { "priority", "requestTime", "count", "station", "taskType", "skill" };
            foreach (var b in SimList.BuiltInFields) if (!f.Contains(b)) f.Add(b);
            return f;
        }

        private static List<string> MemberFields(UnityResourceTeam team)
        {
            var f = new List<string> { "idleTime", "utilization", "tasksDone", "capacity", "name", "location" };
            if (team != null)
                foreach (var m in team.members)
                    if (m?.labels != null)
                        foreach (var l in m.labels)
                            if (l != null && !string.IsNullOrEmpty(l.label) && !f.Contains(l.label)) f.Add(l.label);
            return f;
        }
    }
}
