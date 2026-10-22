using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Queue inspector (<see cref="SElementInspector"/>).</summary>
    [CustomEditor(typeof(UnityQueue))]
    public class UnityQueueEditor : SElementInspector
    {
        protected override void DrawParameters()
        {
            EditorGUILayout.PropertyField(Prop("capacity"), new GUIContent("Capacity", "Items it can hold."));
        }

        protected override void DrawVisual()
        {
            var layout = Prop("layoutMode");
            EditorGUILayout.PropertyField(layout,
                new GUIContent("Layout", "Grid: centred on the queue's footprint, layers on top when full.\nStack: one column upwards.\n" +
                               "Seats: the queue's Seat nodes.\nLine: a single file behind the head, facing +Z (a waiting line)."));
            EditorGUILayout.PropertyField(Prop("itemPosition"),
                new GUIContent("Item position", "Centre of the grid / base of the stack / head of the line. Empty = the top of the queue's footprint."));
            SimuLeanGUI.Field(Prop("separation"),
                new GUIContent("Item spacing", "Distance between item centres. 0 = automatic, from the size of the items."));
            if (layout.enumValueIndex == (int)QueueLayoutMode.Line)
                SimuLeanGUI.Field(Prop("lineLength"),
                    new GUIContent("Line length", "Length of one run (m) before the line folds back beside itself. 0 = one straight line."));
            if (layout.enumValueIndex == (int)QueueLayoutMode.Seats)
            {
                var q = (UnityQueue)target;
                int seats = q.Seats().Count;
                EditorGUILayout.LabelField(" ", $"{seats} seat(s) for {q.capacity} place(s): patients sit on them in order" +
                    (seats < q.capacity ? "; the others stand" : ""), EditorStyles.miniLabel);
                if (ChairRow(q) != null && GUILayout.Button(new GUIContent("Fit Chairs to Capacity",
                        "Rebuild the row of chairs with one seat per place of the queue.")))
                    FitChairs(q);
            }
            EditorGUILayout.PropertyField(Prop("showFootprint"),
                new GUIContent("Show pad", "The queue's own model. Off for a waiting line or a list drawn by other means."));
            if (layout.enumValueIndex == (int)QueueLayoutMode.Grid)
            {
                EditorGUILayout.PropertyField(Prop("gridArea"),
                    new GUIContent("Grid area", "Width (X) and depth (Z) of the grid in the queue's axes. 0 = the queue's footprint."));
                var q = (UnityQueue)target;
                ItemVisuals.Footprint(q, out Vector3 size, out _);
                var cells = q.GridCells(Vector3.zero);
                string text = q.separation > 0f ? $"{cells.x} × {cells.y} cells per layer" : "cells from the item size at run time";
                EditorGUILayout.LabelField(" ", $"footprint {SimuLeanGUI.Format(size.x)} × {SimuLeanGUI.Format(size.z)} m · {text}", EditorStyles.miniLabel);
            }
        }

        /// <summary>The queue's row of chairs (a modular strip of seats among its children, e.g. the Waiting Room preset).</summary>
        public static UnityModularStrip ChairRow(UnityQueue q) => q.GetComponentInChildren<UnityModularStrip>(true);

        /// <summary>Rebuilds the chair row with one seat per place (healthcare: where the waiting patients sit),
        /// keeping its end caps and centred on the queue. Undoable.</summary>
        public static void FitChairs(UnityQueue q)
        {
            var strip = ChairRow(q);
            if (strip == null) return;
            int seats = Mathf.Max(1, q.Seats().Count);
            float caps = Mathf.Max(0f, strip.length - seats * strip.segmentLength);   // what the end caps take
            Undo.RecordObject(strip, "Fit Chairs");
            Undo.RecordObject(strip.transform, "Fit Chairs");
            strip.length = q.capacity * strip.segmentLength + caps;
            if (Mathf.Abs(strip.transform.localPosition.x) > 1e-4f)                   // centred as the preset places it
                strip.transform.localPosition = new Vector3(strip.length * 0.5f * Mathf.Sign(strip.transform.localPosition.x),
                                                            strip.transform.localPosition.y, strip.transform.localPosition.z);
            strip.Rebuild();
            EditorUtility.SetDirty(strip);
        }

        protected override void CollectIssues(List<InspectorIssue> issues)
        {
            var queue = (UnityQueue)target;
            if (queue.layoutMode == QueueLayoutMode.Seats && queue.Seats().Count < queue.capacity)
                issues.Add(new InspectorIssue(MessageType.Warning,
                    $"Only {queue.Seats().Count} of {queue.capacity} places have a seat: the others stand.",
                    ChairRow(queue) != null ? "Fit chairs" : null, ChairRow(queue) != null ? () => FitChairs(queue) : (System.Action)null));
            if (Prop("capacity").intValue < 1)
                issues.Add(new InspectorIssue(MessageType.Error, "Capacity must be at least 1.", "Set to 1", () =>
                {
                    serializedObject.Update();
                    Prop("capacity").intValue = 1;
                    serializedObject.ApplyModifiedProperties();
                }));
        }
    }
}
