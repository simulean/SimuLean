using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// <i>Tools ▸ SimuLean ▸ Modelling Layout</i>: the editor arranged for modelling in one click — Unity's
    /// default layout (Scene and Game views, Hierarchy, Inspector, Project and Console below) with the SimuLean window
    /// docked in front of the Project browser, so the library is at hand to drag elements into the scene. It replaces
    /// the current arrangement (after asking); Unity's <i>Window ▸ Layouts</i> brings any other back.
    /// </summary>
    public static class SimuLeanLayout
    {
        [MenuItem("Tools/SimuLean/Modelling Layout", priority = 2)]
        public static void Apply()
        {
            if (!EditorUtility.DisplayDialog("SimuLean modelling layout",
                    "Arrange the editor for modelling: Unity's default layout with the SimuLean window next to the Project " +
                    "browser. The current window arrangement is replaced (Window ▸ Layouts brings others back).",
                    "Arrange", "Cancel"))
                return;

            string layout = Path.Combine(EditorApplication.applicationContentsPath, "Resources", "Layouts", "Default.wlt");
            if (File.Exists(layout)) EditorUtility.LoadWindowLayout(layout);
            else Debug.LogWarning($"[SimuLean] Unity's default layout was not found ({layout}); the SimuLean window is docked only.");

            // the next editor tick: the windows of the loaded layout exist then
            EditorApplication.delayCall += () =>
            {
                Type project = typeof(EditorWindow).Assembly.GetType("UnityEditor.ProjectBrowser");
                var window = project != null ? EditorWindow.GetWindow<SimuLeanWindow>("SimuLean", true, project)
                                             : EditorWindow.GetWindow<SimuLeanWindow>("SimuLean");
                window.Show();
                window.Focus();
                EditorWindow.FocusWindowIfItsOpen<SceneView>();
            };
        }
    }
}
