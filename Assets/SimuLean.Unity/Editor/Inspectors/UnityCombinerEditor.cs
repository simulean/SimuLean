using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Combiner inspector (<see cref="SElementInspector"/>).</summary>
    [CustomEditor(typeof(UnityCombiner))]
    public class UnityCombinerEditor : SElementInspector
    {
        protected override bool DrawsReceive => false;   // the inputs are shown below (Flow)

        private UnityCombiner Combiner => (UnityCombiner)target;

        protected override void DrawParameters()
        {
            EditorGUILayout.PropertyField(Prop("requirements"),
                new GUIContent("Components per input", "How many items each component input gives to one main item (one entry per component input)."), true);
            EditorGUILayout.PropertyField(Prop("delayStrategy"), new GUIContent("Process time", "Time to assemble one main item."));
            EditorGUILayout.PropertyField(Prop("batchMode"),
                new GUIContent("Keep components", "On: the components travel on inside the main item (batch). Off: they are consumed."));
            var update = Prop("updateRequirements");
            EditorGUILayout.PropertyField(update, new GUIContent("Components from labels", "Read the requirements from labels of the main item."));
            if (update.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(Prop("updateLabels"), new GUIContent("Labels", "One label per component input."), true);
                EditorGUI.indentLevel--;
            }
            var pull = Prop("pullModeType");
            EditorGUILayout.PropertyField(pull, new GUIContent("Component admission", "Which component items it accepts."));
            EditorGUI.indentLevel++;
            switch ((UnityCombiner.PullModeType)pull.enumValueIndex)
            {
                case UnityCombiner.PullModeType.SingleLabel:
                    EditorGUILayout.PropertyField(Prop("singleLabelName"), new GUIContent("Label"));
                    EditorGUILayout.PropertyField(Prop("singleLabelValue"), new GUIContent("Value", "Empty = the main item's value of the label."));
                    break;
                case UnityCombiner.PullModeType.MultiLabel:
                    EditorGUILayout.PropertyField(Prop("multiLabelPairs"), new GUIContent("Label = value pairs"), true);
                    break;
            }
            EditorGUI.indentLevel--;
        }

        protected override void DrawFlowExtra()
        {
            SimuLeanGUI.SubHeader("Inputs (0 = main, then one per component)");
            var slots = Combiner.ResolveInputSlots();
            for (int i = 0; i < slots.Count; i++)
            {
                var li = Combiner.ListInputAt(i);
                string feeder = li != null ? $"list '{li.list.ListName}'" : slots[i] != null ? slots[i].name : "— missing —";
                SimuLeanGUI.Row(i == 0 ? "Main" : $"Component {i}", feeder);
            }
            EditorGUILayout.PropertyField(Prop("inputOrder"),
                new GUIContent("Input order", "[0] = main input, then the component inputs. Unlisted connected elements follow in hierarchy order."), true);
            EditorGUILayout.PropertyField(Prop("listInputs"),
                new GUIContent("List inputs", "Inputs pulled from a list instead of a connection (0 = main, k = component k)."), true);
        }

        protected override void DrawVisual()
        {
            var mode = Prop("visualMode");
            EditorGUILayout.PropertyField(mode, new GUIContent("Assembled look",
                "Attach: the components ride on top of the main item.\nMainOnly: they disappear.\nNewItem: a new visual replaces them all."));
            if (mode.enumValueIndex == (int)UnityCombiner.VisualMode.NewItem)
                EditorGUILayout.PropertyField(Prop("combinedItemPrefab"), new GUIContent("Assembled item prefab", "Empty = Attach."));
            EditorGUILayout.PropertyField(Prop("itemPosition"), new GUIContent("Item position", "Where the assembly happens. Empty = the top of the footprint."));
            EditorGUILayout.PropertyField(Prop("outItemPosition"), new GUIContent("Out position", "Optional: the assembled item moves here while processed."));
            SimuLeanGUI.Field(Prop("separation"), new GUIContent("Component spacing", "0 = automatic, from the size of the items."));
            EditorGUILayout.PropertyField(Prop("serverAnimator"), new GUIContent("Animator", "Optional: WorkInProgress while assembling."));
        }

        protected override void CollectIssues(List<InspectorIssue> issues)
        {
            var c = Combiner;
            int needed = (c.requirements?.Length ?? 0) + 1, fed = c.FedInputCount();
            if (fed != needed)
                issues.Add(new InspectorIssue(MessageType.Warning,
                    $"{fed} input(s) fed, {needed} needed (1 main + one per component input). Connect elements to it or add list inputs.",
                    "Connect…", () => SimuLeanConnectTool.Activate()));
            var order = c.ResolveInputOrder();
            if (order.Count > 0 && (c.inputOrder == null || c.inputOrder.Count < order.Count))
                issues.Add(new InspectorIssue(MessageType.Info, "The input order follows the hierarchy: fix it so moving objects cannot change it.",
                    "Fix order", () =>
                    {
                        Undo.RecordObject(c, "Fix combiner input order");
                        c.inputOrder = new List<SElement>(c.ResolveInputOrder());
                        EditorUtility.SetDirty(c);
                    }));
        }
    }
}
