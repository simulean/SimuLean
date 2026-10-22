using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Helper utility for SElement editors to provide common functionality
    /// such as adding Stats and Labels components.
    /// </summary>
    public static class SElementEditorHelper
    {
        /// <summary>Serialized fields drawn by <see cref="DrawConnections"/> (exclude them from default drawing).</summary>
        public static readonly string[] ConnectionFields =
            { "m_Script", "nextElements", "sendTo", "receiveFrom", "pushToList", "pullFromList", "pullQuery", "processBy", "transportBy", "location",
              "outputStrategy", "outputStrategyAsset", "inputStrategy", "entryLabels", "exitLabels", "tallies", "tallySplit" };

        /// <summary>
        /// The connections of an element, the same in every inspector: <b>Send To</b> elements (Next Elements)
        /// or a list, and <b>Receive From</b> connections or a list (with its pull query). Only the fields of
        /// the chosen option are shown. <paramref name="drawReceive"/> = false for elements that choose per
        /// input (Combiner) or never receive (sources). Then the element's <b>Resources</b> (<see cref="DrawResources"/>).
        /// </summary>
        public static void DrawConnections(SerializedObject so, bool drawReceive = true)
        {
            DrawSendReceive(so, drawReceive);
            DrawResources(so);
        }

        /// <summary>
        /// Resources the element uses, each block assigned or not: <b>Process</b> (a team needed for its process;
        /// stations only) and <b>Transport</b> (a team that carries its items to the next elements).
        /// </summary>
        public static void DrawResources(SerializedObject so)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Resources", EditorStyles.boldLabel);
            DrawResourceFields(so);
        }

        /// <summary>The Flow section of the inspector standard: Send To / Next Elements / routing, admission, Receive From.</summary>
        public static void DrawFlow(SerializedObject so, bool drawReceive = true) => DrawSendReceive(so, drawReceive);

        /// <summary>The Resources section of the inspector standard: location node, process and transport teams.</summary>
        public static void DrawResourceFields(SerializedObject so)
        {
            EditorGUILayout.PropertyField(so.FindProperty("location"), new GUIContent("Location (node)",
                "Network node where the element stands: vehicles and walking operators carry its items from / to it."));
            if (so.targetObject is IProcessResourceUser)
                EditorGUILayout.PropertyField(so.FindProperty("processBy"), new GUIContent("Process"));
            EditorGUILayout.PropertyField(so.FindProperty("transportBy"), new GUIContent("Transport"));
        }

        // The Flow section (review2 #39, #40): where the items go (a compact list of the next elements, the routing), where
        // they come from, and the rarely used settings in an Advanced foldout that says how many of them are set.
        private static void DrawSendReceive(SerializedObject so, bool drawReceive)
        {
            var element = so.targetObject as SElement;
            bool hasOutput = element == null || element.HasOutput, hasInput = element == null || element.HasInput;

            var send = so.FindProperty("sendTo");
            EditorGUILayout.PropertyField(send, new GUIContent("Send To"));
            EditorGUI.indentLevel++;
            if (send.enumValueIndex == (int)SElement.SendMode.List)
            {
                var list = so.FindProperty("pushToList");
                EditorGUILayout.PropertyField(list, new GUIContent("List"));
                if (list.objectReferenceValue == null)
                    EditorGUILayout.HelpBox("Choose the list the items are pushed to.", MessageType.Warning);
            }
            else
            {
                DrawNextElements(so.FindProperty("nextElements"));
                var asset = so.FindProperty("outputStrategyAsset");
                if (hasOutput && asset.objectReferenceValue == null)
                    EditorGUILayout.PropertyField(so.FindProperty("outputStrategy"), new GUIContent("Routing",
                        "Which next element gets each item (the numbers above are the outputs' indexes)."), true);
                else if (hasOutput)
                    EditorGUILayout.LabelField("Routing", $"shared asset '{asset.objectReferenceValue.name}' (Advanced)");
            }
            EditorGUI.indentLevel--;

            if (drawReceive)
            {
                var receive = so.FindProperty("receiveFrom");
                EditorGUILayout.PropertyField(receive, new GUIContent("Receive From"));
                if (receive.enumValueIndex == (int)SElement.ReceiveMode.List)
                {
                    EditorGUI.indentLevel++;
                    var list = so.FindProperty("pullFromList");
                    EditorGUILayout.PropertyField(list, new GUIContent("List"));
                    if (list.objectReferenceValue == null)
                        EditorGUILayout.HelpBox("Choose the list the items are pulled from.", MessageType.Warning);
                    else
                        EditorGUILayout.PropertyField(so.FindProperty("pullQuery"), new GUIContent("Pull Query"), true);
                    EditorGUI.indentLevel--;
                }
            }

            // ── advanced: shared routing, labels, admission, item statistics ──
            var routingAsset = so.FindProperty("outputStrategyAsset");
            var exitLabels = so.FindProperty("exitLabels");
            var admission = so.FindProperty("inputStrategy");
            var entryLabels = so.FindProperty("entryLabels");
            var tallies = so.FindProperty("tallies");
            int set = 0;
            if (hasOutput && routingAsset.objectReferenceValue != null) set++;
            if (hasOutput && exitLabels.arraySize > 0) set++;
            if (drawReceive && hasInput)
            {
                var admissionType = admission.FindPropertyRelative("strategyType");
                if (admissionType != null && admissionType.enumValueIndex != 0) set++;
                if (entryLabels.arraySize > 0) set++;
                if (tallies.arraySize > 0) set++;
            }
            bool open = SessionState.GetBool("SimuLean.Flow.Advanced", false);
            bool now = EditorGUILayout.Foldout(open, set > 0 ? $"Advanced ({set} set)" : "Advanced", true);
            if (now != open) SessionState.SetBool("SimuLean.Flow.Advanced", now);
            if (!now) return;
            EditorGUI.indentLevel++;
            if (hasOutput)
            {
                EditorGUILayout.PropertyField(routingAsset, new GUIContent("Routing Asset",
                    "Optional shared routing strategy (Assets ▸ Create ▸ SimuLean ▸ Output Strategy). Overrides the routing above."));
                EditorGUILayout.PropertyField(exitLabels, new GUIContent("Labels on Exit",
                    "'name = expression' set on every item that leaves (before the next element takes it)."), true);
            }
            if (drawReceive && hasInput)
            {
                EditorGUILayout.PropertyField(admission, new GUIContent("Admission",
                    "Which items this element admits (checked before every hand-over)."), true);
                EditorGUILayout.PropertyField(entryLabels, new GUIContent("Labels on Entry",
                    "'name = expression' set on every item that enters (before this element takes it): counters, " +
                    "timestamps, random draws."), true);
                EditorGUILayout.PropertyField(tallies, new GUIContent("Item Statistics",
                    "'name = expression' recorded for every item that enters (e.g. time_in_system = age): mean, std, min, " +
                    "max, P50, P90 in the results (tally:name:p90)."), true);
                if (tallies.arraySize > 0)
                    EditorGUILayout.PropertyField(so.FindProperty("tallySplit"), new GUIContent("Split By",
                        "A label: the item statistics per value as well (e.g. priority). Empty = no split."));
            }
            EditorGUI.indentLevel--;
        }

        // The next elements as a compact list: the output's number (what routing rules and weights refer to), the element,
        // a button that removes it; + Add for a new one (the Connect tool draws them in the Scene view).
        private static void DrawNextElements(SerializedProperty next)
        {
            if (next.arraySize == 0) EditorGUILayout.LabelField("Next Elements", "none", EditorStyles.miniLabel);
            for (int i = 0; i < next.arraySize; i++)
            {
                var item = next.GetArrayElementAtIndex(i);
                var r = EditorGUILayout.GetControlRect();
                var field = EditorGUI.PrefixLabel(r, new GUIContent(i == 0 ? "Next Elements" : " ", "In output order: routing rules and weights use these numbers."));
                int indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                GUI.Label(new Rect(field.x, field.y, 18, field.height), i.ToString(), EditorStyles.miniLabel);
                EditorGUI.PropertyField(new Rect(field.x + 18, field.y, field.width - 18 - 24, field.height), item, GUIContent.none);
                if (GUI.Button(new Rect(field.xMax - 22, field.y, 22, field.height), new GUIContent("✕", "Remove this connection"), EditorStyles.miniButton))
                {
                    item.objectReferenceValue = null;
                    next.DeleteArrayElementAtIndex(i);
                    EditorGUI.indentLevel = indent;
                    break;
                }
                EditorGUI.indentLevel = indent;
            }
            var add = EditorGUILayout.GetControlRect();
            var addField = EditorGUI.PrefixLabel(add, new GUIContent(" "));
            if (GUI.Button(new Rect(addField.x, addField.y, 80, addField.height), new GUIContent("+ Add", "A new output: choose its element (or drag in the Scene view with the Connect tool, Shift+C)."), EditorStyles.miniButton))
                next.InsertArrayElementAtIndex(next.arraySize);
        }

        /// <summary>The element's user properties: a button that adds them when missing (edited in their own component).</summary>
        public static void DrawAddComponentButtons(Object target)
        {
            if (!(target is MonoBehaviour mb) || mb.GetComponent<SElementLabels>() != null) return;
            EditorGUILayout.Space();
            var r = EditorGUILayout.GetControlRect();
            r = EditorGUI.PrefixLabel(r, new GUIContent("Properties", "User properties (key = value) read by expressions, lists and routing."));
            if (GUI.Button(r, "+ Add properties")) Undo.AddComponent<SElementLabels>(mb.gameObject);
        }
    }
}
