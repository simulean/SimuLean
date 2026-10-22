using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Prepares a scene for modelling: one <see cref="UnitySimClock"/> with the default runtime views
    /// on the same object (<see cref="SimRuntimeHud"/>, <see cref="ElementInspectorPanel"/>,
    /// <see cref="SimVisualDetail"/>) and a <see cref="SimCameraController"/> on the main camera, all
    /// wired to each other. Editor-only (nothing is added at run time); the views never change
    /// simulation results.
    /// <para>Runs: from <i>Tools ▸ SimuLean ▸ Set Up Scene</i>; when the clock is created from the
    /// SimuLean window or added by hand; automatically in every new scene and, once per project, in the
    /// scene open when SimuLean is first loaded — both switchable with
    /// <i>Tools ▸ SimuLean ▸ Auto Set-Up New Scenes</i> (project setting,
    /// <c>ProjectSettings/SimuLeanSettings.json</c>). Never in batch mode.</para>
    /// </summary>
    [InitializeOnLoad]
    public static class SimuLeanSceneSetup
    {
        static SimuLeanSceneSetup()
        {
            if (Application.isBatchMode) return;
            EditorSceneManager.newSceneCreated += OnNewScene;
            ObjectFactory.componentWasAdded += OnComponentAdded;
            EditorApplication.update += FirstLoadOnce;   // first editor tick after loading (delayCall can be skipped)
        }

        // ── settings ─────────────────────────────────────────────────────────

        // the project settings (shared with the results settings: SimuLeanProjectSettings)
        private static SimuLeanProjectSettings Load() => SimuLeanProjectSettings.Load();
        private static void Save(SimuLeanProjectSettings s) => s.Save();

        /// <summary>New scenes (and the first load of the project) are set up automatically (SimuLean window ▸ Settings).</summary>
        public static bool AutoSetUpNewScenes
        {
            get => Load().autoSetUpNewScenes;
            set { var s = Load(); s.autoSetUpNewScenes = value; Save(s); }
        }

        // ── triggers ─────────────────────────────────────────────────────────

        private static void FirstLoadOnce()
        {
            EditorApplication.update -= FirstLoadOnce;
            FirstLoad();
        }

        private static void FirstLoad()
        {
            var s = Load();
            if (s.initialSetUpDone || EditorApplication.isPlayingOrWillChangePlaymode) return;
            s.initialSetUpDone = true;
            Save(s);
            if (s.autoSetUpNewScenes && UnityCompat.FindAny<UnitySimClock>() == null)
                SetUpScene(SceneManager.GetActiveScene());
        }

        private static void OnNewScene(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            if (mode == NewSceneMode.Single && Load().autoSetUpNewScenes)
                SetUpScene(scene);
        }

        private static void OnComponentAdded(Component c)
        {
            // Adding the clock by hand gives it its default views too.
            if (c is UnitySimClock clock) AddDefaultViews(clock);
        }

        // ── set-up ───────────────────────────────────────────────────────────

        [MenuItem("Tools/SimuLean/Set Up Scene", priority = 100)]
        public static void SetUpActiveScene() => Selection.activeObject = SetUpScene(SceneManager.GetActiveScene());

        /// <summary>Ensures the scene has a clock with its default views and a navigation camera.
        /// Idempotent: existing components are kept and wired. Returns the clock.</summary>
        public static UnitySimClock SetUpScene(Scene scene)
        {
            // the scene's own clock (with several scenes open, another scene's clock is not this one's)
            UnitySimClock clock = InScene<UnitySimClock>(scene);
            if (clock == null)
            {
                var go = new GameObject("SimClock");
                if (scene.IsValid()) SceneManager.MoveGameObjectToScene(go, scene);
                Undo.RegisterCreatedObjectUndo(go, "Set up SimuLean scene");
                clock = Undo.AddComponent<UnitySimClock>(go);   // componentWasAdded adds the views
                if (ModelHierarchy.AutoOrganize) ModelHierarchy.Place(go);   // Runtime group, now (not at the next editor tick)
            }
            AddDefaultViews(clock);
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            return clock;
        }

        /// <summary>Adds (if missing) and wires the default runtime views of <paramref name="clock"/>.</summary>
        public static void AddDefaultViews(UnitySimClock clock)
        {
            if (clock == null) return;
            GameObject host = clock.gameObject;
            Scene scene = host.scene;

            SimCameraController cam = InScene<SimCameraController>(scene);
            if (cam == null)
            {
                Camera main = InScene<Camera>(scene, c => c.CompareTag("MainCamera")) ?? InScene<Camera>(scene);
                if (main != null) cam = Undo.AddComponent<SimCameraController>(main.gameObject);
            }

            var hud = InScene<SimRuntimeHud>(scene) ?? Undo.AddComponent<SimRuntimeHud>(host);
            var panel = InScene<ElementInspectorPanel>(scene) ?? Undo.AddComponent<ElementInspectorPanel>(host);
            if (InScene<SimVisualDetail>(scene) == null) Undo.AddComponent<SimVisualDetail>(host);

            Undo.RecordObjects(new Object[] { hud, panel }, "Wire SimuLean views");
            if (hud.simClock == null) hud.simClock = clock;
            if (hud.cameraController == null) hud.cameraController = cam;
            if (panel.simClock == null) panel.simClock = clock;
            if (panel.cameraController == null) panel.cameraController = cam;
            EditorUtility.SetDirty(hud);
            EditorUtility.SetDirty(panel);
        }

        /// <summary>The first <typeparamref name="T"/> of <paramref name="scene"/> (optionally matching), or null.</summary>
        private static T InScene<T>(Scene scene, System.Func<T, bool> match = null) where T : Component
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var c in root.GetComponentsInChildren<T>(true))
                    if (match == null || match(c)) return c;
            return null;
        }
    }
}
