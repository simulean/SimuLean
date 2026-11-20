using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The SimuLean toolbar of the Scene view (shown by default, movable and hideable as any overlay):
    /// the Place and Connect tools, Frame (the Scene view on the selection, else on the whole model; Shift: from above),
    /// Validate (the model built without running, the answer in a notification and the Console), Fit Floor, the per-user switches Snap, Connections and Edit Parts (they follow their setting however it
    /// is changed — the Tools menu too) and the ⋯ menu (Set Up Scene, Fit Floor, Modelling Layout, Organize Hierarchy).
    /// </summary>
    [Overlay(typeof(SceneView), "SimuLean", true)]
    [Icon("d_UnityEditor.SceneView")]
    public class SimuLeanSceneOverlay : ToolbarOverlay
    {
        public const string Place = "SimuLean/Place", Connect = "SimuLean/Connect", Frame = "SimuLean/Frame", Validate = "SimuLean/Validate",
                            Floor = "SimuLean/FitFloor", Snap = "SimuLean/Snap", Connections = "SimuLean/Connections",
                            Parts = "SimuLean/EditParts", More = "SimuLean/More";

        public SimuLeanSceneOverlay() : base(Place, Connect, Frame, Validate, Floor, Snap, Connections, Parts, More) { }

        // A built-in icon by name (the dark-skin variant first); null — text only — when Unity has none: FindTexture does not
        // log a missing icon as IconContent does.
        internal static Texture2D Icon(string name) =>
            (EditorGUIUtility.isProSkin ? EditorGUIUtility.FindTexture("d_" + name) : null) ?? EditorGUIUtility.FindTexture(name);

        /// <summary>Centres the Scene view, in edit mode and in Play: on the selection, else on the whole model
        /// (every element); <paramref name="fromAbove"/> looks straight down. The game camera is not touched (it frames an
        /// element by itself).</summary>
        public static void FrameView(bool fromAbove)
        {
            GameObject selected = Selection.activeGameObject;
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null) return;
            if (!ModelBounds(selected, out Bounds b)) { view.ShowNotification(new GUIContent("Nothing to frame"), 1.5); return; }
            if (fromAbove) view.LookAt(b.center, Quaternion.Euler(90f, 0f, 0f), Mathf.Max(b.extents.x, b.extents.z) * 1.2f + 0.5f);
            else view.Frame(b, false);
        }

        // the bounds of the selection (its renderers), else of every element of the open scenes
        private static bool ModelBounds(GameObject selected, out Bounds bounds)
        {
            Bounds b = default;
            bool any = false;
            void Add(Bounds more)
            {
                if (any) b.Encapsulate(more); else { b = more; any = true; }
            }
            var objects = new System.Collections.Generic.List<GameObject>();
            if (selected != null) objects.AddRange(Selection.gameObjects);
            else foreach (var e in UnityCompat.FindAll<SElement>()) objects.Add(e.gameObject);
            foreach (var go in objects)
            {
                bool drawn = false;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    if (r.enabled) { Add(r.bounds); drawn = true; }
                if (!drawn) Add(new Bounds(go.transform.position, Vector3.one));   // an element without a model
            }
            bounds = b;
            return any;
        }

        /// <summary>Builds the scene's model without running it and says what a run would meet.</summary>
        public static void ValidateModel()
        {
            JObject answer = JObject.FromObject(SimuLeanCommands.Validate());
            var errors = answer["errors"] as JArray ?? new JArray();
            var warnings = answer["warnings"] as JArray ?? new JArray();
            foreach (var w in warnings) Debug.LogWarning("[SimuLean] " + w);
            foreach (var er in errors) Debug.LogError("[SimuLean] " + er);
            string text = errors.Count > 0 ? $"Model: {errors.Count} error(s) — see the Console"
                        : warnings.Count > 0 ? $"Model OK, {warnings.Count} warning(s) — see the Console"
                        : $"Model OK: {answer["elements"]} elements, {answer["connections"]} connections";
            if (errors.Count == 0) Debug.Log("[SimuLean] " + text);
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null) view.ShowNotification(new GUIContent(text), 3.0);
        }
    }

    [EditorToolbarElement(SimuLeanSceneOverlay.Place, typeof(SceneView))]
    internal class PlaceToolButton : EditorToolbarToggle
    {
        public PlaceToolButton()
        {
            text = "Place"; icon = SimuLeanSceneOverlay.Icon("MoveTool");
            tooltip = "Move and turn elements on the floor (Shift+P)";
            this.RegisterValueChangedCallback(e =>
            {
                if (e.newValue) ToolManager.SetActiveTool<SimuLeanPlaceTool>();
                else if (ToolManager.activeToolType == typeof(SimuLeanPlaceTool)) ToolManager.RestorePreviousPersistentTool();
            });
            ToolManager.activeToolChanged += Sync;
            RegisterCallback<UnityEngine.UIElements.DetachFromPanelEvent>(_ => ToolManager.activeToolChanged -= Sync);
            Sync();
        }

        private void Sync() => SetValueWithoutNotify(ToolManager.activeToolType == typeof(SimuLeanPlaceTool));
    }

    [EditorToolbarElement(SimuLeanSceneOverlay.Connect, typeof(SceneView))]
    internal class ConnectToolButton : EditorToolbarToggle
    {
        public ConnectToolButton()
        {
            text = "Connect"; icon = SimuLeanSceneOverlay.Icon("Linked");
            tooltip = "Drag from one object to another to connect them (Shift+C)";
            this.RegisterValueChangedCallback(e =>
            {
                if (e.newValue) SimuLeanConnectTool.Activate();
                else if (ToolManager.activeToolType == typeof(SimuLeanConnectTool)) ToolManager.RestorePreviousPersistentTool();
            });
            ToolManager.activeToolChanged += Sync;
            RegisterCallback<UnityEngine.UIElements.DetachFromPanelEvent>(_ => ToolManager.activeToolChanged -= Sync);
            Sync();
        }

        private void Sync() => SetValueWithoutNotify(ToolManager.activeToolType == typeof(SimuLeanConnectTool));
    }

    [EditorToolbarElement(SimuLeanSceneOverlay.Frame, typeof(SceneView))]
    internal class FrameButton : EditorToolbarButton
    {
        public FrameButton()
        {
            text = "Frame"; icon = SimuLeanSceneOverlay.Icon("ViewToolZoom");
            tooltip = "Centre the Scene view on the selection, or on the whole model when nothing is selected (Shift: from above)";
            clicked += () => SimuLeanSceneOverlay.FrameView(Event.current != null && Event.current.shift);
        }
    }

    [EditorToolbarElement(SimuLeanSceneOverlay.Validate, typeof(SceneView))]
    internal class ValidateButton : EditorToolbarButton
    {
        public ValidateButton()
        {
            text = "Validate"; icon = SimuLeanSceneOverlay.Icon("FilterSelectedOnly");
            tooltip = "Build the model without running it and list what a run would meet";
            clicked += SimuLeanSceneOverlay.ValidateModel;
        }
    }

    [EditorToolbarElement(SimuLeanSceneOverlay.Floor, typeof(SceneView))]
    internal class FitFloorButton : EditorToolbarButton
    {
        public FitFloorButton()
        {
            text = "Floor"; icon = SimuLeanSceneOverlay.Icon("Grid.Default");
            tooltip = "Fit the floor to the model";
            clicked += () => EditorApplication.ExecuteMenuItem("Tools/SimuLean/Fit Floor to Model");
        }
    }

    [EditorToolbarElement(SimuLeanSceneOverlay.Snap, typeof(SceneView))]
    internal class SnapToggle : EditorToolbarToggle
    {
        public SnapToggle()
        {
            text = "Snap"; icon = SimuLeanSceneOverlay.Icon("SceneViewSnap");
            tooltip = $"Place snaps to {SimuLeanPlaceTool.MoveStep} m and {SimuLeanPlaceTool.AngleStep}° (hold Ctrl to place freely)";
            this.Follow(() => SimuLeanPlaceTool.Snap);
            this.RegisterValueChangedCallback(e => SimuLeanPlaceTool.Snap = e.newValue);
        }
    }

    [EditorToolbarElement(SimuLeanSceneOverlay.Connections, typeof(SceneView))]
    internal class ConnectionsToggle : EditorToolbarToggle
    {
        public ConnectionsToggle()
        {
            text = "Links"; icon = SimuLeanSceneOverlay.Icon("Linked");
            tooltip = "Show the connections in the Scene view";
            this.Follow(() => ConnectionModel.ShowWhenIdle);
            this.RegisterValueChangedCallback(e => ConnectionModel.ShowWhenIdle = e.newValue);
        }
    }

    [EditorToolbarElement(SimuLeanSceneOverlay.Parts, typeof(SceneView))]
    internal class EditPartsToggle : EditorToolbarToggle
    {
        public EditPartsToggle()
        {
            text = "Parts"; icon = SimuLeanSceneOverlay.Icon("Prefab Icon");
            tooltip = "Edit element parts: a click selects the part instead of its element";
            this.Follow(() => ElementSelection.EditParts);
            this.RegisterValueChangedCallback(e => ElementSelection.EditParts = e.newValue);
        }
    }

    internal static class ToggleFollow
    {
        /// <summary>Keeps the toggle on its setting, changed here or elsewhere (a menu, the SimuLean window): read now and
        /// a few times a second while the toggle is shown.</summary>
        public static void Follow(this EditorToolbarToggle toggle, System.Func<bool> setting)
        {
            toggle.SetValueWithoutNotify(setting());
            toggle.schedule.Execute(() => { if (toggle.value != setting()) toggle.SetValueWithoutNotify(setting()); }).Every(250);
        }
    }

    [EditorToolbarElement(SimuLeanSceneOverlay.More, typeof(SceneView))]
    internal class MoreDropdown : EditorToolbarDropdown
    {
        public MoreDropdown()
        {
            text = "⋯";
            tooltip = "More: set up the scene, fit the floor, arrange the windows, organize the hierarchy";
            clicked += () =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Set Up Scene"), false, SimuLeanSceneSetup.SetUpActiveScene);
                menu.AddItem(new GUIContent("Fit Floor to Model"), false, () => EditorApplication.ExecuteMenuItem("Tools/SimuLean/Fit Floor to Model"));
                menu.AddItem(new GUIContent("Organize Hierarchy"), false, () => ModelHierarchy.OrganizeOpenScenes());
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Modelling Layout…"), false, SimuLeanLayout.Apply);
                menu.ShowAsContext();
            };
        }
    }
}
