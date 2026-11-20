using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The SimuLean Place tool: moving and turning an element in one tool, on the floor. Drag a selected
    /// object to slide it over the ground (its height kept), drag the ring round it to turn it about the vertical; both
    /// snap — <see cref="MoveStep"/> metres and <see cref="AngleStep"/> degrees — unless <b>Ctrl</b> is held or snapping
    /// is off (<see cref="Snap"/>, per user). A click on another object selects it as usual; right-click opens the
    /// element's menu (turn by 90°, connect, duplicate, delete, fit the floor). With <see cref="AutoActivate"/> (per user,
    /// on) the tool takes over from Unity's Move / Rotate / Transform tools while SimuLean objects are selected, and
    /// gives them back when the selection is something else. Edits are undoable.
    /// </summary>
    [EditorTool("SimuLean Place")]
    public class SimuLeanPlaceTool : EditorTool
    {
        public const float MoveStep = 0.5f;
        public const float AngleStep = 15f;
        private const string SnapPref = "SimuLean.PlaceSnap";
        private const string AutoPref = "SimuLean.PlaceAuto";

        /// <summary>Snap moves and turns (per user, on).</summary>
        public static bool Snap
        {
            get => EditorPrefs.GetBool(SnapPref, true);
            set => EditorPrefs.SetBool(SnapPref, value);
        }

        /// <summary>Use this tool instead of Unity's move / rotate tools while SimuLean objects are selected (per user, on).</summary>
        public static bool AutoActivate
        {
            get => EditorPrefs.GetBool(AutoPref, true);
            set => EditorPrefs.SetBool(AutoPref, value);
        }

        private static GUIContent icon;

        public override GUIContent toolbarIcon
        {
            get
            {
                if (icon == null)
                {
                    var tex = EditorGUIUtility.IconContent("d_MoveTool").image ?? EditorGUIUtility.IconContent("MoveTool").image;
                    icon = new GUIContent("Place", tex, "SimuLean Place (Shift+P): drag an element over the floor, turn it with its ring");
                }
                return icon;
            }
        }

        [Shortcut("SimuLean/Place Tool", typeof(SceneView), KeyCode.P, ShortcutModifiers.Shift)]
        public static void Activate() => ToolManager.SetActiveTool<SimuLeanPlaceTool>();

        // ── dragging ─────────────────────────────────────────────────────────

        private readonly List<Transform> moving = new List<Transform>();
        private readonly List<Vector3> startPositions = new List<Vector3>();
        private Plane dragPlane;
        private Vector3 dragStart;
        private int dragControl;

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView)) return;
            Transform[] selected = Selection.GetTransforms(SelectionMode.TopLevel | SelectionMode.Editable);
            if (selected.Length == 0) return;
            Event e = Event.current;
            bool snap = Snap && !e.control;

            Vector3 pivot = Tools.handlePosition;
            float radius = Radius(selected);

            // turn: the ring round the selection
            EditorGUI.BeginChangeCheck();
            Quaternion before = Tools.handleRotation;
            Handles.color = new Color(1f, 0.75f, 0.2f, 0.9f);
            Quaternion after = Handles.Disc(before, pivot, Vector3.up, radius, false, snap ? AngleStep : 0f);
            if (EditorGUI.EndChangeCheck())
            {
                float yaw = Mathf.DeltaAngle(before.eulerAngles.y, after.eulerAngles.y);
                Turn(selected, pivot, yaw, "Turn");
            }

            // move: drag the object itself over its ground plane
            int id = GUIUtility.GetControlID(FocusType.Passive);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when e.button == 0 && !e.alt && GUIUtility.hotControl == 0:
                {
                    GameObject hit = HandleUtility.PickGameObject(e.mousePosition, false);
                    if (hit == null) break;
                    GameObject owner = ElementSelection.OwnerOf(hit);
                    if (System.Array.IndexOf(selected, owner.transform) < 0 && System.Array.IndexOf(selected, hit.transform) < 0) break;
                    dragPlane = new Plane(Vector3.up, pivot);
                    if (!Raycast(e.mousePosition, out dragStart)) break;
                    moving.Clear(); startPositions.Clear();
                    foreach (Transform t in selected) { moving.Add(t); startPositions.Add(t.position); }
                    Undo.RecordObjects(moving.ToArray(), "Place");
                    dragControl = id;
                    GUIUtility.hotControl = id;
                    e.Use();
                    break;
                }
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                {
                    if (!Raycast(e.mousePosition, out Vector3 now)) break;
                    Vector3 delta = now - dragStart;
                    delta.y = 0f;
                    if (snap && moving.Count > 0)
                    {
                        // the first object lands on the grid; the others keep their offsets to it
                        Vector3 lead = startPositions[0] + delta;
                        delta = new Vector3(SnapTo(lead.x, MoveStep), lead.y, SnapTo(lead.z, MoveStep)) - startPositions[0];
                    }
                    for (int i = 0; i < moving.Count; i++)
                        if (moving[i] != null) moving[i].position = startPositions[i] + delta;
                    e.Use();
                    break;
                }
                case EventType.MouseUp when GUIUtility.hotControl == id && e.button == 0:
                    GUIUtility.hotControl = 0;
                    moving.Clear();
                    e.Use();
                    break;
                case EventType.ContextClick:
                {
                    GameObject hit = HandleUtility.PickGameObject(e.mousePosition, false);
                    if (hit == null || System.Array.IndexOf(selected, ElementSelection.OwnerOf(hit).transform) < 0) break;
                    ShowMenu(selected, pivot);
                    e.Use();
                    break;
                }
                // no default control: a click on anything else is Unity's (select, rectangle selection)
            }
        }

        private bool Raycast(Vector2 mouse, out Vector3 point)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(mouse);
            if (dragPlane.Raycast(ray, out float d)) { point = ray.GetPoint(d); return true; }
            point = default;
            return false;
        }

        private static float SnapTo(float v, float step) => Mathf.Round(v / step) * step;

        // A ring a little larger than the selection's footprint (at least 1 m).
        private static float Radius(Transform[] selected)
        {
            Bounds? all = null;
            foreach (Transform t in selected)
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>())
                {
                    if (all == null) all = r.bounds; else { Bounds b = all.Value; b.Encapsulate(r.bounds); all = b; }
                }
            if (all == null) return 1f;
            Vector3 s = all.Value.extents;
            return Mathf.Max(1f, Mathf.Sqrt(s.x * s.x + s.z * s.z) * 1.1f);
        }

        /// <summary>Turns <paramref name="selected"/> by <paramref name="yaw"/> degrees about the vertical through
        /// <paramref name="pivot"/> (undoable).</summary>
        public static void Turn(Transform[] selected, Vector3 pivot, float yaw, string undo)
        {
            if (Mathf.Approximately(yaw, 0f)) return;
            Undo.RecordObjects(selected, undo);
            Quaternion q = Quaternion.AngleAxis(yaw, Vector3.up);
            foreach (Transform t in selected)
            {
                t.position = pivot + q * (t.position - pivot);
                t.rotation = q * t.rotation;
            }
        }

        private static void ShowMenu(Transform[] selected, Vector3 pivot)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Turn 90° Left"), false, () => Turn(selected, pivot, -90f, "Turn 90°"));
            menu.AddItem(new GUIContent("Turn 90° Right"), false, () => Turn(selected, pivot, 90f, "Turn 90°"));
            menu.AddItem(new GUIContent("Turn 180°"), false, () => Turn(selected, pivot, 180f, "Turn 180°"));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Connect…"), false, SimuLeanConnectTool.Activate);
            menu.AddItem(new GUIContent("Duplicate"), false, () => EditorApplication.ExecuteMenuItem("Edit/Duplicate"));
            menu.AddItem(new GUIContent("Delete"), false, () => EditorApplication.ExecuteMenuItem("Edit/Delete"));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Snap to Grid"), Snap, () => Snap = !Snap);
            menu.AddItem(new GUIContent("Fit Floor to Model"), false, () => EditorApplication.ExecuteMenuItem("Tools/SimuLean/Fit Floor to Model"));
            menu.ShowAsContext();
        }

        // ── automatic activation ─────────────────────────────────────────────

        [InitializeOnLoadMethod]
        private static void Hook() => ElementSelection.Settled += OnSelectionChanged;

        private static bool activatedBySelection;

        private static void OnSelectionChanged()
        {
            if (!AutoActivate || EditorApplication.isPlayingOrWillChangePlaymode) return;
            bool ours = Selection.gameObjects.Length > 0;
            foreach (GameObject go in Selection.gameObjects)
                if (!go.scene.IsValid() || ElementSelection.OwnerOf(go) != go || !IsSimuLean(go)) { ours = false; break; }

            if (ours && IsUnityTransformTool())
            {
                ToolManager.SetActiveTool<SimuLeanPlaceTool>();
                activatedBySelection = true;
            }
            else if (!ours && activatedBySelection && ToolManager.activeToolType == typeof(SimuLeanPlaceTool))
            {
                activatedBySelection = false;
                Tools.current = Tool.Move;
            }
        }

        private static bool IsUnityTransformTool()
        {
            if (ToolManager.activeToolType == typeof(SimuLeanPlaceTool)) return false;
            return Tools.current == Tool.Move || Tools.current == Tool.Rotate || Tools.current == Tool.Transform;
        }

        private static bool IsSimuLean(GameObject go)
        {
            foreach (MonoBehaviour c in go.GetComponents<MonoBehaviour>())
                if (c != null && (c is SElement || c.GetType().IsDefined(typeof(SimuLeanElementAttribute), true) ||
                                  c.GetType().IsDefined(typeof(SimuLeanToolAttribute), true)))
                    return true;
            return false;
        }
    }
}
