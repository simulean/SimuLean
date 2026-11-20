using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Queue inspector (<see cref="SElementInspector"/>).</summary>
    [CustomEditor(typeof(UnityQueue))]
    public class UnityQueueEditor : SElementInspector
    {
        protected override string[] DrawnFields => new[]
        {
            "capacity", "offerOrder", "offerSort", "offerRule", "ruleParameters", "gated", "groupBySetup", "groupLimit", "offerScan", "maxWait", "onTimeout",
            "timeoutTarget", "layoutMode", "separation", "gridArea", "itemPosition", "lineLength", "showFootprint",
        };

        protected override void DrawParameters()
        {
            EditorGUILayout.PropertyField(Prop("capacity"), new GUIContent("Capacity", "Items it can hold."));
            var release = Prop("offerOrder");
            EditorGUILayout.PropertyField(release, new GUIContent("Release order", release.tooltip));
            EditorGUILayout.PropertyField(Prop("offerSort"), new GUIContent("Offer by", Prop("offerSort").tooltip));
            EditorGUILayout.PropertyField(Prop("offerRule"), new GUIContent("Offer by rule", Prop("offerRule").tooltip));
            if (!string.IsNullOrWhiteSpace(Prop("offerRule").stringValue))
                EditorGUILayout.PropertyField(Prop("ruleParameters"), new GUIContent("Rule parameters", Prop("ruleParameters").tooltip), true);
            EditorGUILayout.PropertyField(Prop("gated"), new GUIContent("Release on command",
                "Items leave only when released: a reaction's Release (orders every period, a kanban) or the button in Play. " +
                "A release not used yet is kept for the next items."));
            var group = Prop("groupBySetup");
            EditorGUILayout.PropertyField(group, new GUIContent("Group by setup", group.tooltip));
            if (group.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(Prop("groupLimit"), new GUIContent("Most in a row", Prop("groupLimit").tooltip));
                EditorGUILayout.PropertyField(Prop("offerScan"), new GUIContent("Items looked at", "Items looked through for one " +
                    "the receiver is set up for (0 = every held item)."));
                EditorGUI.indentLevel--;
            }
            else if (release.enumValueIndex == (int)OfferOrder.AnyAccepted)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(Prop("offerScan"), new GUIContent("Items looked at", Prop("offerScan").tooltip));
                EditorGUI.indentLevel--;
            }
            var maxWait = Prop("maxWait");
            EditorGUILayout.PropertyField(maxWait, new GUIContent("Max wait", maxWait.tooltip));
            if (!string.IsNullOrWhiteSpace(maxWait.stringValue))
            {
                EditorGUI.indentLevel++;
                var onTimeout = Prop("onTimeout");
                EditorGUILayout.PropertyField(onTimeout, new GUIContent("On timeout", onTimeout.tooltip));
                if (onTimeout.enumValueIndex != (int)SimuLean.TimeoutAction.KeepWaiting)
                    EditorGUILayout.PropertyField(Prop("timeoutTarget"), new GUIContent("Timeout target", Prop("timeoutTarget").tooltip));
                EditorGUI.indentLevel--;
            }
        }

        private static int releaseQuantity = 1;

        protected override void DrawPlayExtra()
        {
            var queue = (UnityQueue)target;
            if (!queue.gated) return;
            SimuLeanGUI.Row("Pending release", queue.PendingRelease.ToString());
            EditorGUILayout.BeginHorizontal();
            releaseQuantity = Mathf.Max(1, EditorGUILayout.IntField("Release", releaseQuantity));
            if (GUILayout.Button("Release", GUILayout.Width(70))) queue.ReleaseItems(releaseQuantity);
            EditorGUILayout.EndHorizontal();
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
            if (!string.IsNullOrWhiteSpace(queue.maxWait) && queue.onTimeout != SimuLean.TimeoutAction.KeepWaiting && queue.timeoutTarget == null)
                issues.Add(new InspectorIssue(MessageType.Error, "Items that wait too long need a Timeout Target (e.g. a Sink)."));
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
