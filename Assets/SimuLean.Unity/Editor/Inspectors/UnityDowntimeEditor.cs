using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Downtime inspector: the targets and the stop behaviour, then only the section of the chosen kind (MTBF / MTTR,
    /// timetable — with its database options when used —, shift calendar or periodic stop); live stops in Play mode.
    /// </summary>
    [CustomEditor(typeof(UnityDowntime))]
    public class UnityDowntimeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var d = (UnityDowntime)target;

            Field("id");
            EditorGUILayout.PropertyField(serializedObject.FindProperty("downtimeEnabled"), new GUIContent("Enabled"));
            var kindBefore = (UnityDowntime.DowntimeKind)serializedObject.FindProperty("kind").enumValueIndex;
            Field("kind");
            Field("targets", true);
            Field("teamTargets", true);
            var kind = (UnityDowntime.DowntimeKind)serializedObject.FindProperty("kind").enumValueIndex;
            // a state left at a kind's default follows the kind: switching kind clears it (empty = the new kind's default)
            var stateProp = serializedObject.FindProperty("stateName");
            if (kind != kindBefore && stateProp.stringValue == SimuLean.Serialization.DowntimeBuilder.DefaultStateName(kindBefore.ToString()))
                stateProp.stringValue = "";
            if (kind == UnityDowntime.DowntimeKind.Timetable) Field("targetKeys", true);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Stop behaviour", EditorStyles.boldLabel);
            Field("stateName");
            if (string.IsNullOrEmpty(stateProp.stringValue))
                EditorGUILayout.LabelField(" ", $"empty: {SimuLean.Serialization.DowntimeBuilder.DefaultStateName(kind.ToString())} (the kind's)", EditorStyles.miniLabel);
            Field("mode");
            if (serializedObject.FindProperty("mode").intValue == (int)UnityDowntime.DowntimeMode.KindDefault)
                EditorGUILayout.LabelField(" ", $"{SimuLean.Serialization.DowntimeBuilder.DefaultMode(kind.ToString())} (the kind's)", EditorStyles.miniLabel);
            Field("blockInput");
            Field("blockOutput");

            EditorGUILayout.Space(4);
            switch (kind)
            {
                case UnityDowntime.DowntimeKind.MtbfMttr:
                    EditorGUILayout.LabelField("MTBF / MTTR", EditorStyles.boldLabel);
                    Field("timeToFailure", true);
                    Field("timeToRepair", true);
                    Field("useFirstFailure");
                    if (serializedObject.FindProperty("useFirstFailure").boolValue) Field("firstFailure", true);
                    Field("basis");
                    if (serializedObject.FindProperty("basis").enumValueIndex == (int)SimuLean.Downtime.DowntimeBasis.BusyTime)
                        Field("busyStates", true);
                    Field("failureCode");
                    break;

                case UnityDowntime.DowntimeKind.Shift:
                    EditorGUILayout.LabelField("Shift calendar", EditorStyles.boldLabel);
                    var clock = UnityCompat.FindAny<UnitySimClock>();
                    if (clock == null || string.IsNullOrWhiteSpace(clock.StartDateTime))
                        EditorGUILayout.HelpBox("Shifts need a Start Date/Time on the Simulation Clock (the calendar of the run).", MessageType.Warning);
                    Field("shiftPattern");
                    Field("holidays", true);
                    break;

                case UnityDowntime.DowntimeKind.Periodic:
                    EditorGUILayout.LabelField("Periodic stop", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("Stops every Period for Duration, the first at First (simulation units). " +
                                            "Planned stops are usually SCHEDULED_DOWN; After Current lets the job in progress finish first.",
                                            MessageType.Info);
                    Field("periodicFirst");
                    Field("periodicPeriod");
                    Field("periodicDuration");
                    Field("useUntil");
                    if (serializedObject.FindProperty("useUntil").boolValue) Field("periodicUntil");
                    Field("allowOverlap");
                    Field("periodicCode");
                    double p = serializedObject.FindProperty("periodicPeriod").doubleValue;
                    double dur = serializedObject.FindProperty("periodicDuration").doubleValue;
                    if (p <= 0 || dur <= 0)
                        EditorGUILayout.HelpBox("Period and duration must be positive: the run will not build.", MessageType.Error);
                    else if (dur > p && !serializedObject.FindProperty("allowOverlap").boolValue)
                        EditorGUILayout.HelpBox("The duration is longer than the period: allow overlap or the run will not build.", MessageType.Error);
                    break;

                case UnityDowntime.DowntimeKind.Timetable:
                    EditorGUILayout.LabelField("Timetable", EditorStyles.boldLabel);
                    Field("overlap");
                    Field("rows", true);
                    Field("dataTable");   // the stops from a data table of the model
                    if (serializedObject.FindProperty("dataTable").objectReferenceValue != null)
                    {
                        EditorGUI.indentLevel++;
                        Field("targetColumn");
                        Field("startColumn");
                        Field("endColumn");
                        Field("durationColumn");
                        Field("codeColumn");
                        Field("reasonColumn");
                        Field("durationScale");
                        EditorGUI.indentLevel--;
                    }
                    Field("codeRules", true);
                    break;
            }

            if (kind != UnityDowntime.DowntimeKind.Shift)   // the crew that repairs each stop (a shift needs none)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Repair", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("repairBy"), new GUIContent("Repair By"), true);
            }

            if (Application.isPlaying && d.Generators.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"{d.TotalStops} stops, {d.TotalDowntime:F1} down (all targets)");
                if (d.TotalRepairs >= 0) EditorGUILayout.LabelField($"{d.TotalRepairs} repairs, wait for the crew {d.RepairWaitAvg:F1} on average");
                Repaint();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void Field(string name, bool children = false)
            => EditorGUILayout.PropertyField(serializedObject.FindProperty(name), children);
    }
}
