using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// An element's use of a resource team, for its process or to carry its items: only the team until one is
    /// assigned, then its requirements, priority label and the options of that use (release / move times). The
    /// process block is hidden on elements without a process (<see cref="IProcessResourceUser"/>). An Assembly Station without
    /// a process team whose tasks name their own teams shows the settings those tasks' uses take from it.
    /// </summary>
    [CustomPropertyDrawer(typeof(ProcessResourceUse))]
    [CustomPropertyDrawer(typeof(TransportResourceUse))]
    [CustomPropertyDrawer(typeof(RepairResourceUse))]
    [CustomPropertyDrawer(typeof(SetupResourceUse))]
    public class ResourceUseDrawer : PropertyDrawer
    {
        private static float Spacing => EditorGUIUtility.standardVerticalSpacing;

        private bool Hidden(SerializedProperty p)
            => fieldInfo.FieldType == typeof(ProcessResourceUse) && !(p.serializedObject.targetObject is IProcessResourceUser);

        // no process team, but assembly tasks with teams of their own — the block's settings apply to their uses.
        private bool TaskSettingsOnly(SerializedProperty p)
            => fieldInfo.FieldType == typeof(ProcessResourceUse) && p.FindPropertyRelative("team").objectReferenceValue == null
               && !RequirementNamesTeam(p) && p.serializedObject.targetObject is UnityAssemblyStation asy && asy.TasksUseOwnTeams;

        // no team, but a requirement names its own — the use is active, so its settings show.
        private static UnityResourceTeam FirstRequirementTeam(SerializedProperty p)
        {
            var reqs = p.FindPropertyRelative("requirements");
            if (reqs == null || !reqs.isArray) return null;
            for (int i = 0; i < reqs.arraySize; i++)
                if (reqs.GetArrayElementAtIndex(i).FindPropertyRelative("team")?.objectReferenceValue is UnityResourceTeam t) return t;
            return null;
        }

        private static bool RequirementNamesTeam(SerializedProperty p) => FirstRequirementTeam(p) != null;

        private List<(SerializedProperty prop, GUIContent label)> Rows(SerializedProperty p, GUIContent label)
        {
            bool taskSettings = TaskSettingsOnly(p);
            var rows = new List<(SerializedProperty, GUIContent)>
            {
                (p.FindPropertyRelative("team"), new GUIContent(label.text + " (team)",
                    fieldInfo.FieldType == typeof(RepairResourceUse)
                        ? "Resource team whose members repair each stop: the element waits for them (WAITING_FOR_REPAIR), they travel to it, and the repair time starts when they are there (empty = no crew: a stop lasts its time)."
                    : fieldInfo.FieldType == typeof(SetupResourceUse)
                        ? "Resource team that sets the station up: its members travel to it and show SETUP (empty = the machine sets up alone)."
                    : taskSettings
                        ? "Resource team needed for the process (empty = none). The tasks that name their own teams ask them with the settings below."
                    : fieldInfo.FieldType == typeof(ProcessResourceUse)
                        ? "Resource team needed for the process (empty = none)."
                        : "Resource team that carries the items to the next elements (empty = they move by themselves).")),
            };
            if (p.FindPropertyRelative("team").objectReferenceValue == null && !taskSettings && !RequirementNamesTeam(p)) return rows;
            bool repair = fieldInfo.FieldType == typeof(RepairResourceUse);
            if (!taskSettings) rows.Add((p.FindPropertyRelative("requirements"), new GUIContent("Requirements", repair
                ? "Crew members per repair, all at once (empty = one member of the team); e.g. skill == electrical."
                : "Resources per job (empty = one member of the team); a requirement may name another team (not for transport).")));
            if (fieldInfo.FieldType != typeof(TransportResourceUse))
                rows.Add((p.FindPropertyRelative("acquire"), new GUIContent("Acquire",
                    "Several requirements: All At Once (nothing held while one is missing) or In Order (each kept, HELD, while the " +
                    "next is waited for — a crane, then a driver; two uses in opposite orders can deadlock).")));
            rows.Add((p.FindPropertyRelative("priority"), new GUIContent("Priority",
                repair ? "Priority of the repair requests: an expression over the element (empty = 1; larger = more important), e.g. 5: served first when the team's Request Order sorts by priority, and with a preemption mode it takes the crew from lower work."
                       : "Request priority: an expression over the item and the element (empty = 1; larger = more important).")));
            rows.Add((p.FindPropertyRelative("preemption"), new GUIContent("Preemption",
                "When no member is free: wait (None), or take one from work of strictly lower priority (Resume / Restart the " +
                "interrupted work; Abort only work without an item).")));
            rows.Add((p.FindPropertyRelative("preemptable"), new GUIContent("Preemptable",
                repair ? "Work of higher priority with a preemption mode may take the crew from the repair (it waits for the crew again, then goes on). Off by default."
                       : "Requests of higher priority may take this work's members.")));
            if (fieldInfo.FieldType != typeof(TransportResourceUse))
            {
                var maxWait = p.FindPropertyRelative("maxWait");
                rows.Add((maxWait, new GUIContent("Max Wait", maxWait.tooltip)));
                if (!string.IsNullOrWhiteSpace(maxWait.stringValue))
                {
                    var onTimeout = p.FindPropertyRelative("onTimeout");
                    rows.Add((onTimeout, new GUIContent("On Timeout", fieldInfo.FieldType == typeof(ProcessResourceUse)
                        ? onTimeout.tooltip
                        : "Keep Waiting: only record it. Give Up: release what is held for it and ask again behind the others.")));
                    if (fieldInfo.FieldType == typeof(ProcessResourceUse) && !taskSettings && onTimeout.enumValueIndex == (int)SimuLean.TimeoutAction.Reroute)
                        rows.Add((p.FindPropertyRelative("timeoutTarget"), new GUIContent("Timeout Target",
                            "Where the item goes, unprocessed, when its job gives up (a Multi-Server only).")));
                }
            }
            if (repair || fieldInfo.FieldType == typeof(SetupResourceUse)) return rows;   // a setup crew's time is the setup's
            if (taskSettings) return rows;   // release / own time are the station team's (the tasks' uses take only these)
            if (fieldInfo.FieldType == typeof(ProcessResourceUse))
            {
                if (p.serializedObject.targetObject is UnityMultiServer)   // the Multi-Server only (the build refuses it elsewhere)
                    rows.Add((p.FindPropertyRelative("acquireBeforeEntry"), new GUIContent("Before The Part",
                        "Secure these resources before taking the part: the item stays upstream until they are at the station.")));
                var own = p.FindPropertyRelative("holdForOwnTime");
                rows.Add((own, new GUIContent("Own Time", "The resources work for their own time and are then free; the item stays for the rest of the process.")));
                if (own.boolValue) rows.Add((p.FindPropertyRelative("holdFor"), new GUIContent("Resource Time")));
                else
                {
                    var release = p.FindPropertyRelative("release");
                    rows.Add((release, new GUIContent("Release")));
                    if (release.enumValueIndex == (int)SimuLean.Resources.ProcessRelease.AtElement)
                        rows.Add((p.FindPropertyRelative("releaseAt"), new GUIContent("Release At",
                            "The element where the resource comes back, when the item enters it (e.g. the device return desk).")));
                }
            }
            else
            {
                var team = p.FindPropertyRelative("team").objectReferenceValue as UnityResourceTeam ?? FirstRequirementTeam(p);
                if (team == null || team.travel == TeamTravel.Abstract)
                    rows.Add((p.FindPropertyRelative("travelTime"), new GUIContent("Travel Time")));
                var over = p.FindPropertyRelative("overrideLoadTimes");
                rows.Add((over, new GUIContent("Override Load Times", "Use these load / unload times instead of each member's own.")));
                if (over.boolValue)
                {
                    rows.Add((p.FindPropertyRelative("loadTime"), new GUIContent("Load Time")));
                    rows.Add((p.FindPropertyRelative("unloadTime"), new GUIContent("Unload Time")));
                }
            }
            return rows;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (Hidden(property)) return -Spacing;
            float h = 0;
            foreach (var (prop, l) in Rows(property, label)) h += EditorGUI.GetPropertyHeight(prop, l, true) + Spacing;
            return h - Spacing;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (Hidden(property)) return;
            EditorGUI.BeginProperty(position, label, property);
            float y = position.y;
            bool first = true;
            foreach (var (prop, l) in Rows(property, label))
            {
                float h = EditorGUI.GetPropertyHeight(prop, l, true);
                if (!first) EditorGUI.indentLevel++;
                EditorGUI.PropertyField(new Rect(position.x, y, position.width, h), prop, l, true);
                if (!first) EditorGUI.indentLevel--;
                first = false;
                y += h + Spacing;
            }
            EditorGUI.EndProperty();
        }
    }
}
