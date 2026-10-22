using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// An element's use of a resource team, for its process or to carry its items: only the team until one is
    /// assigned, then its requirements, priority label and the options of that use (release / move times). The
    /// process block is hidden on elements without a process (<see cref="IProcessResourceUser"/>).
    /// </summary>
    [CustomPropertyDrawer(typeof(ProcessResourceUse))]
    [CustomPropertyDrawer(typeof(TransportResourceUse))]
    public class ResourceUseDrawer : PropertyDrawer
    {
        private static float Spacing => EditorGUIUtility.standardVerticalSpacing;

        private bool Hidden(SerializedProperty p)
            => fieldInfo.FieldType == typeof(ProcessResourceUse) && !(p.serializedObject.targetObject is IProcessResourceUser);

        private List<(SerializedProperty prop, GUIContent label)> Rows(SerializedProperty p, GUIContent label)
        {
            var rows = new List<(SerializedProperty, GUIContent)>
            {
                (p.FindPropertyRelative("team"), new GUIContent(label.text + " (team)",
                    fieldInfo.FieldType == typeof(ProcessResourceUse)
                        ? "Resource team needed for the process (empty = none)."
                        : "Resource team that carries the items to the next elements (empty = they move by themselves).")),
            };
            if (p.FindPropertyRelative("team").objectReferenceValue == null) return rows;
            rows.Add((p.FindPropertyRelative("requirements"), new GUIContent("Requirements", "Resources per job (empty = one member of the team).")));
            rows.Add((p.FindPropertyRelative("priority"), new GUIContent("Priority", "Item label giving the request priority (used by the team's request order).")));
            if (fieldInfo.FieldType == typeof(ProcessResourceUse))
            {
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
                var team = p.FindPropertyRelative("team").objectReferenceValue as UnityResourceTeam;
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
