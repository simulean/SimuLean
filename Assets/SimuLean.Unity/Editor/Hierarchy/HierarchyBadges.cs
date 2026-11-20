using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The Hierarchy shows what each SimuLean object is: its library name in grey at the right of its row
    /// (e.g. <i>Multi-Server</i> for a "CNC" GameObject) and, in Play, a dot with the element's state — green working,
    /// red blocked, orange stopped / down, blue waiting for a resource, grey idle — refreshed twice a second. Read-only
    /// (nothing is changed); <i>Tools ▸ SimuLean ▸ Hierarchy Badges</i> (per user, on) hides them.
    /// </summary>
    [InitializeOnLoad]
    public static class HierarchyBadges
    {
        private const string PrefKey = "SimuLean.HierarchyBadges";
        private const string MenuPath = "Tools/SimuLean/Hierarchy Badges";

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set { EditorPrefs.SetBool(PrefKey, value); Menu.SetChecked(MenuPath, value); EditorApplication.RepaintHierarchyWindow(); }
        }

        private static readonly Dictionary<Type, string> names = new Dictionary<Type, string>();
        private static GUIStyle style;
        private static double nextRepaint;

        static HierarchyBadges()
        {
            EditorApplication.hierarchyWindowItemByEntityIdOnGUI += OnItem;
            EditorApplication.update += RepaintInPlay;
            EditorApplication.delayCall += () => Menu.SetChecked(MenuPath, Enabled);
        }

        [MenuItem(MenuPath, priority = 111)]
        private static void Toggle() => Enabled = !Enabled;

        private static void RepaintInPlay()
        {
            if (!EditorApplication.isPlaying || !Enabled || EditorApplication.timeSinceStartup < nextRepaint) return;
            nextRepaint = EditorApplication.timeSinceStartup + 0.5;
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void OnItem(EntityId id, Rect row)
        {
            if (!Enabled || Event.current.type != EventType.Repaint) return;
            if (!(EditorUtility.EntityIdToObject(id) is GameObject go)) return;

            string label = null;
            SElement element = null;
            foreach (MonoBehaviour c in go.GetComponents<MonoBehaviour>())
            {
                if (c == null) continue;
                label = LibraryName(c.GetType());
                if (label == null) continue;
                element = c as SElement;
                break;
            }
            if (label == null) return;

            style ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight, normal = { textColor = new Color(0.55f, 0.55f, 0.55f) } };
            float right = row.xMax - 4f;
            if (EditorApplication.isPlaying && element != null && element.GetElement() != null)
            {
                var dot = new Rect(right - 8f, row.y + (row.height - 8f) * 0.5f, 8f, 8f);
                EditorGUI.DrawRect(dot, StateColor(element.GetElement().CurrentState.Name));
                right -= 12f;
            }
            float width = style.CalcSize(new GUIContent(label)).x;
            float nameEnd = row.x + 18f + EditorStyles.label.CalcSize(new GUIContent(go.name)).x + 8f;
            if (right - width < nameEnd) return;   // a narrow window: the name first
            GUI.Label(new Rect(right - width, row.y, width, row.height), label, style);
        }

        // The library name of a SimuLean component type (element or tool), or null.
        private static string LibraryName(Type type)
        {
            if (names.TryGetValue(type, out string cached)) return cached;
            string name = type.GetCustomAttribute<SimuLeanElementAttribute>()?.DisplayName
                       ?? type.GetCustomAttribute<SimuLeanToolAttribute>()?.DisplayName;
            names[type] = name;
            return name;
        }

        private static Color StateColor(string state)
        {
            switch (state)
            {
                case "PROCESSING": case "RECEIVING": case "SETUP": case "TRAVELING": return new Color(0.30f, 0.80f, 0.35f);
                case "BLOCKED": return new Color(0.90f, 0.25f, 0.20f);
                case "STOPPED": case "BREAKDOWN": case "WAITING_FOR_REPAIR": case "SCHEDULED_DOWN": case "OFF_SHIFT": return new Color(1.00f, 0.60f, 0.10f);
                case "WAITING_FOR_RESOURCE": case "STARVED": return new Color(0.30f, 0.55f, 0.95f);
                default: return new Color(0.55f, 0.55f, 0.55f);
            }
        }
    }
}
