using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    public partial class SimuLeanWindow
    {
        private bool showConnections;
        private static GUIStyle badgeStyle;

        private void DrawModel()
        {
            EditorGUILayout.BeginHorizontal();
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
            if (GUILayout.Button(new GUIContent("Organize", "Moves the SimuLean objects at the scene root into their groups."), EditorStyles.miniButton, GUILayout.Width(70)))
                ModelHierarchy.OrganizeOpenScenes();
            EditorGUILayout.EndHorizontal();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            int problems = 0;
            foreach (string group in ModelHierarchy.Groups)
            {
                // one row per object, with its SimuLean components (the clock's object holds the clock and its views)
                var objects = ObjectsOf(group).Where(c => Matches(c.gameObject.name, c.GetType().Name))
                                              .GroupBy(c => c.gameObject).ToList();
                if (objects.Count == 0) continue;
                GroupHeader(group, group, objects.Count.ToString());
                foreach (var o in objects) problems += DrawModelRow(o.ToList());
            }
            if (problems > 0)
                EditorGUILayout.HelpBox($"{problems} problem(s) in the model: select an element marked ⚠ — its inspector says how to fix it.", MessageType.Warning);
            DrawConnectionList();
            EditorGUILayout.EndScrollView();
        }

        /// <summary>The scene objects of a hierarchy group (whatever their parent).</summary>
        private IEnumerable<Component> ObjectsOf(string group)
        {
            IEnumerable<Component> found = group == "Flow"
                ? InScenes<SElement>().Cast<Component>()
                : tools.Where(t => ModelHierarchy.GroupOf(t.type) == group)
                       .SelectMany(t => UnityCompat.FindAll(t.type, true).OfType<Component>()
                                                    .Where(c => c.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(c)));
            return found.OrderBy(c => c.gameObject.name, System.StringComparer.Ordinal);
        }

        private int DrawModelRow(List<Component> components)
        {
            var c = components.Find(x => x is SElement) ?? components.Find(x => x is UnitySimClock) ?? components[0];
            if (badgeStyle == null) badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = new Color(1f, 0.75f, 0.2f) } };
            int problems = c is SElement el ? ElementChecks.ProblemCount(el) : 0;
            bool selected = Selection.activeGameObject == c.gameObject;
            var r = EditorGUILayout.GetControlRect();
            if (selected) EditorGUI.DrawRect(r, new Color(0.24f, 0.48f, 0.9f, 0.35f));
            string type = string.Join(" · ", components.Select(TypeName));
            float w = r.width;
            if (GUI.Button(new Rect(r.x + 10, r.y, w * 0.5f, r.height), c.gameObject.name, EditorStyles.label)) Select(c);
            GUI.Label(new Rect(r.x + 10 + w * 0.5f, r.y, w * 0.5f - 90, r.height), new GUIContent(type, type), EditorStyles.miniLabel);
            if (problems > 0) GUI.Label(new Rect(r.xMax - 64, r.y, 40, r.height), new GUIContent($"⚠ {problems}", "Problems: see its inspector"), badgeStyle);
            if (GUI.Button(new Rect(r.xMax - 20, r.y, 20, r.height), new GUIContent("×", "Remove from the scene"), EditorStyles.miniButton)
                && EditorUtility.DisplayDialog("Remove", $"Remove '{c.gameObject.name}' from the scene?", "Remove", "Cancel"))
            {
                Undo.DestroyObjectImmediate(c.gameObject);
                GUIUtility.ExitGUI();
            }
            return problems;
        }

        private static string TypeName(Component c)
            => c.GetType().GetCustomAttributes(typeof(SimuLeanElementAttribute), false).OfType<SimuLeanElementAttribute>().FirstOrDefault()?.DisplayName
               ?? c.GetType().GetCustomAttributes(typeof(SimuLeanToolAttribute), false).OfType<SimuLeanToolAttribute>().FirstOrDefault()?.DisplayName
               ?? c.GetType().Name;

        private void DrawConnectionList()
        {
            var all = ConnectionModel.All();
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            showConnections = EditorGUILayout.Foldout(showConnections, $"Connections ({all.Count})", true, EditorStyles.foldoutHeader);
            if (GUILayout.Button(new GUIContent("Connect tool", "Edit the connections in the Scene view (Shift+C)"), EditorStyles.miniButton, GUILayout.Width(90)))
                SimuLeanConnectTool.Activate();
            EditorGUILayout.EndHorizontal();
            if (!showConnections) return;
            foreach (var kind in all.Select(c => c.Kind).Distinct())
            {
                var ofKind = all.Where(c => c.Kind == kind).ToList();
                EditorGUILayout.LabelField($"{ConnectionModel.Label(kind)} ({ofKind.Count})", EditorStyles.miniBoldLabel);
                foreach (var c in ofKind)
                {
                    var r = EditorGUILayout.GetControlRect();
                    EditorGUI.DrawRect(new Rect(r.x + 4, r.y + 6, 6, 6), ConnectionModel.ColorOf(ConnectionModel.GroupOf(kind)));
                    if (GUI.Button(new Rect(r.x + 14, r.y, r.width - 40, r.height), $"{c.From.name} → {c.To.name}", EditorStyles.label))
                        Select(c.Owner);
                    if (GUI.Button(new Rect(r.xMax - 20, r.y, 20, r.height), new GUIContent("×", "Remove the connection"), EditorStyles.miniButton))
                    {
                        ConnectionModel.Remove(c);
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }
    }
}
