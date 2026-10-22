using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    public partial class SimuLeanWindow
    {
        private void DrawSettings()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            GroupHeader("Scene", "Runtime");
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Set Up Scene", "Adds what a modelling scene needs: the clock with its views and the navigation camera.")))
                SimuLeanSceneSetup.SetUpActiveScene();
            if (GUILayout.Button(new GUIContent("Organize Hierarchy", "Moves the SimuLean objects at the scene root into their groups.")))
                ModelHierarchy.OrganizeOpenScenes();
            EditorGUILayout.EndHorizontal();
            bool auto = EditorGUILayout.ToggleLeft(new GUIContent("Set up new scenes automatically", "Project setting (ProjectSettings/SimuLeanSettings.json)."),
                                                   SimuLeanSceneSetup.AutoSetUpNewScenes);
            if (auto != SimuLeanSceneSetup.AutoSetUpNewScenes) SimuLeanSceneSetup.AutoSetUpNewScenes = auto;
            bool org = EditorGUILayout.ToggleLeft(new GUIContent("Put new objects in their hierarchy group", "Per user."), ModelHierarchy.AutoOrganize);
            if (org != ModelHierarchy.AutoOrganize) ModelHierarchy.AutoOrganize = org;

            GroupHeader("Simulation clock", "Runtime");
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var clock = InScenes<UnitySimClock>().FirstOrDefault(c => c.gameObject.scene == active);
            var r = EditorGUILayout.GetControlRect();
            GUI.Label(new Rect(r.x + 10, r.y, r.width * 0.6f, r.height),
                      clock != null ? $"{clock.gameObject.name}: run time, speed, calendar, seed, warm-up" : "No clock in the scene: the model cannot run.",
                      clock != null ? EditorStyles.miniLabel : EditorStyles.miniBoldLabel);
            if (GUI.Button(new Rect(r.xMax - 90, r.y, 90, r.height), clock != null ? "Select" : "Create", EditorStyles.miniButton))
            {
                if (clock != null) Select(clock);
                else SimuLeanSceneSetup.SetUpActiveScene();
            }

            GroupHeader("Run-time views", "Runtime", "what Play shows");
            foreach (var (type, info) in tools.Where(t => (t.info.Category == SimuLeanToolCategories.Visualization
                                                       || t.info.Category == SimuLeanToolCategories.Simulation) && t.type != typeof(UnitySimClock)))
            {
                var instances = UnityCompat.FindAll(type, true).OfType<Component>().Where(c => c.gameObject.scene.IsValid()).ToList();
                if (info.Singleton) ViewSwitch(type, info, instances.FirstOrDefault(), clock);
                else
                {
                    var row = EditorGUILayout.GetControlRect();
                    GUI.Label(new Rect(row.x + 10, row.y, row.width * 0.6f, row.height), new GUIContent($"{info.DisplayName} ({instances.Count})", info.Description));
                    if (instances.Count > 0 && GUI.Button(new Rect(row.xMax - 124, row.y, 60, row.height), "Select", EditorStyles.miniButton)) Select(instances[0]);
                    if (GUI.Button(new Rect(row.xMax - 60, row.y, 60, row.height), "Add", EditorStyles.miniButton)) CreateComponent(type, info.DisplayName);
                }
            }

            GroupHeader("Results", "Experiments", "project setting");
            {
                var s = SimuLeanProjectSettings.Load();
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.BeginHorizontal();
                s.resultsFolder = EditorGUILayout.TextField(new GUIContent("Folder", "Where results are exported: relative to the project, or absolute (git-ignored)."), s.resultsFolder);
                if (GUILayout.Button("…", GUILayout.Width(24)))
                {
                    string root = ResultsOutput.ProjectRoot;
                    string picked = EditorUtility.OpenFolderPanel("Results folder", ResultsOutput.Folder(root), "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        string full = System.IO.Path.GetFullPath(picked), rootFull = System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar;
                        s.resultsFolder = full.StartsWith(rootFull, System.StringComparison.OrdinalIgnoreCase) ? full.Substring(rootFull.Length) : full;
                        GUI.FocusControl(null);
                    }
                }
                EditorGUILayout.EndHorizontal();
                s.confidence = EditorGUILayout.Slider(new GUIContent("Confidence level", "Of the intervals across replications (t-based)."), (float)s.confidence, 0.80f, 0.99f);
                s.defaultFormats = (SimuLean.Results.ResultFormats)EditorGUILayout.EnumFlagsField(
                    new GUIContent("Export formats", "Formats of an export made by hand (the clock's run, the Results tab). The Experimenter, the Optimizer and the clock's run-end export have their own."),
                    s.defaultFormats);
                s.storeResults = EditorGUILayout.Toggle(new GUIContent("Store results",
                    "Keep every experiment, study and optimisation (with all its candidates) in the project database — Results tab ▸ Stored, " +
                    "where they are deleted by hand. Off by default: a result set is kept only with its Save button."), s.storeResults);
                if (EditorGUI.EndChangeCheck()) { s.confidence = System.Math.Round(s.confidence, 3); s.Save(); }
            }

            GroupHeader("Maintenance", "Presets");
            if (GUILayout.Button(new GUIContent("Reimport 3D Model Library", "Re-applies the import settings of Art/Models (materials, LODs).")))
                EditorApplication.ExecuteMenuItem("Tools/SimuLean/Reimport 3D Model Library");
            // the checks a modeller uses here; the developers' (test runs, camera measurements) in a closed foldout (review2 #46)
            var checks = new System.Collections.Generic.List<string>();
            foreach (var m in TypeCache.GetMethodsWithAttribute<MenuItem>())
                foreach (MenuItem mi in m.GetCustomAttributes(typeof(MenuItem), false))
                    if (!mi.validate && mi.menuItem.StartsWith("Tools/SimuLean/Checks/")) checks.Add(mi.menuItem);
            bool IsDeveloper(string item) => item.Contains("Test") || item.Contains("Camera Controller");
            foreach (string item in checks.Where(i => !IsDeveloper(i)))
                if (GUILayout.Button(item.Substring("Tools/SimuLean/Checks/".Length))) EditorApplication.ExecuteMenuItem(item);
            bool dev = EditorPrefs.GetBool("SimuLean.Settings.Developer", false);
            bool open = EditorGUILayout.Foldout(dev, "Developer tools", true);
            if (open != dev) EditorPrefs.SetBool("SimuLean.Settings.Developer", open);
            if (open)
                foreach (string item in checks.Where(IsDeveloper))
                    if (GUILayout.Button(item.Substring("Tools/SimuLean/Checks/".Length))) EditorApplication.ExecuteMenuItem(item);

            EditorGUILayout.EndScrollView();
        }

        /// <summary>A view that is one per scene, as an on / off switch (added to the clock's object, the camera controller to the camera).</summary>
        private static void ViewSwitch(System.Type type, SimuLeanToolAttribute info, Component instance, UnitySimClock clock)
        {
            var r = EditorGUILayout.GetControlRect();
            bool on = EditorGUI.ToggleLeft(new Rect(r.x + 10, r.y, r.width - 80, r.height), new GUIContent(info.DisplayName, info.Description), instance != null);
            if (instance != null && GUI.Button(new Rect(r.xMax - 60, r.y, 60, r.height), "Select", EditorStyles.miniButton)) Select(instance);
            if (on == (instance != null)) return;
            if (!on) { Undo.DestroyObjectImmediate(instance); return; }

            GameObject host;
            if (info.OnMainCamera)
            {
                var cam = Camera.main != null ? Camera.main : UnityCompat.FindAny<Camera>();
                if (cam == null) { EditorUtility.DisplayDialog("SimuLean", $"{info.DisplayName} needs a camera in the scene.", "OK"); return; }
                host = cam.gameObject;
            }
            else if (clock != null) host = clock.gameObject;
            else { EditorUtility.DisplayDialog("SimuLean", "Create the simulation clock first (Simulation clock ▸ Create).", "OK"); return; }

            var added = Undo.AddComponent(host, type);
            var camCtl = UnityCompat.FindAny<SimCameraController>();
            if (added is SimRuntimeHud hud) { hud.simClock = clock; hud.cameraController = camCtl; EditorUtility.SetDirty(hud); }
            if (added is ElementInspectorPanel panel) { panel.simClock = clock; panel.cameraController = camCtl; EditorUtility.SetDirty(panel); }
        }
    }
}
