using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Overlays;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The SimuLean Connect tool (Scene view Tools overlay, <b>Shift+C</b>): one way to wire every connection of a
    /// model. While it is active it owns the clicks of the Scene view (no Unity selection fights it):
    /// <list type="bullet">
    /// <item>drag from one object to another to connect them — the kind is inferred (element → element flow, element →
    /// list push, list → element pull or combiner input, element ↔ team process / transport resources, element ↔ node
    /// location, node → node track — <b>Shift</b> for one-way —, team ↔ node home node, downtime ↔ element); a menu
    /// asks when more than one kind fits;</item>
    /// <item>click a line to select it, <b>Delete</b> removes it; right-click a line for delete / reverse / owner;</item>
    /// <item>click an object to select it (for the inspector); <b>Esc</b> cancels, a second Esc leaves the tool.</item>
    /// </list>
    /// The edits are the inspector fields (Undo-recorded); the overlay filters what is drawn and shows the legend.
    /// </summary>
    [EditorTool("SimuLean Connect")]
    public class SimuLeanConnectTool : EditorTool
    {
        private static GUIContent icon;

        private readonly List<Component> endpoints = new List<Component>();
        private readonly List<Connection> shown = new List<Connection>();
        private readonly HashSet<Component> validTargets = new HashSet<Component>();

        private Component hoverEndpoint, dragSource;
        private int hoverConnection = -1;
        private Connection? selected;
        private bool dragging;
        private Vector2 mouseDownPos;

        /// <summary>The last hint for the overlay (what a drop would do, or why it cannot).</summary>
        internal static string Status = "";

        public override GUIContent toolbarIcon
        {
            get
            {
                if (icon == null)
                {
                    var tex = EditorGUIUtility.IconContent("d_Linked").image ?? EditorGUIUtility.IconContent("Linked").image;
                    icon = new GUIContent("Connect", tex, "SimuLean Connect (Shift+C): drag between objects to connect them");
                }
                return icon;
            }
        }

        /// <summary>Activates the tool from a button elsewhere (inspector, SimuLean window): the Scene view gets the focus
        /// and says so, otherwise nothing seems to happen.</summary>
        public static void Activate()
        {
            ToolManager.SetActiveTool<SimuLeanConnectTool>();
            var view = SceneView.lastActiveSceneView;
            if (view == null) return;
            view.Focus();
            view.ShowNotification(new GUIContent("Connect: drag from one object to another"), 2.0);
            view.Repaint();
        }

        [Shortcut("SimuLean/Connect Tool", typeof(SceneView), KeyCode.C, ShortcutModifiers.Shift)]
        public static void Toggle()
        {
            if (ToolManager.activeToolType == typeof(SimuLeanConnectTool)) ToolManager.RestorePreviousPersistentTool();
            else ToolManager.SetActiveTool<SimuLeanConnectTool>();
        }

        public override void OnActivated()
        {
            Status = "Drag from one object to another to connect them.";
            selected = null;
            SceneView.RepaintAll();
        }

        public override void OnWillBeDeactivated()
        {
            dragging = false; dragSource = null; selected = null;
            Status = "";
            SceneView.RepaintAll();
        }

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView)) return;
            Event evt = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);

            if (evt.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(id);   // own the clicks: no rectangle / object selection
                Refresh();
            }

            switch (evt.type)
            {
                case EventType.MouseMove:
                    UpdateHover(evt.mousePosition);
                    window.Repaint();
                    break;

                case EventType.MouseDown when evt.button == 0 && !evt.alt:
                    UpdateHover(evt.mousePosition);
                    mouseDownPos = evt.mousePosition;
                    if (hoverEndpoint != null)
                    {
                        dragSource = hoverEndpoint;
                        dragging = false;
                        GUIUtility.hotControl = id;
                    }
                    else
                    {
                        selected = hoverConnection >= 0 ? shown[hoverConnection] : (Connection?)null;
                        Status = selected.HasValue
                            ? $"Selected: {Describe(selected.Value)} — Delete removes it, right-click for more."
                            : "Drag from one object to another to connect them.";
                    }
                    evt.Use();
                    break;

                case EventType.MouseDrag when GUIUtility.hotControl == id && dragSource != null:
                    if (!dragging && Vector2.Distance(evt.mousePosition, mouseDownPos) > 4f)
                    {
                        dragging = true;
                        selected = null;
                        CollectTargets();
                    }
                    if (dragging)
                    {
                        UpdateHover(evt.mousePosition);
                        Status = DropHint(evt.shift);
                    }
                    evt.Use();
                    window.Repaint();
                    break;

                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    if (dragging) Drop(evt.mousePosition, evt.shift);
                    else if (dragSource != null) Selection.activeGameObject = dragSource.gameObject;
                    dragging = false;
                    dragSource = null;
                    validTargets.Clear();
                    evt.Use();
                    window.Repaint();
                    break;

                case EventType.ContextClick:
                    UpdateHover(evt.mousePosition);
                    if (hoverConnection >= 0)
                    {
                        ConnectionMenu(shown[hoverConnection]);
                        evt.Use();
                    }
                    break;

                case EventType.KeyDown:
                    if ((evt.keyCode == KeyCode.Delete || evt.keyCode == KeyCode.Backspace) && selected.HasValue)
                    {
                        Remove(selected.Value);
                        evt.Use();
                    }
                    else if (evt.keyCode == KeyCode.Escape)
                    {
                        if (dragging || selected.HasValue)
                        {
                            dragging = false; dragSource = null; selected = null; validTargets.Clear();
                            GUIUtility.hotControl = 0;
                        }
                        else ToolManager.RestorePreviousPersistentTool();
                        evt.Use();
                    }
                    break;

                case EventType.Repaint:
                    Paint(evt.mousePosition);
                    break;
            }
        }

        // ── state ────────────────────────────────────────────────────────────

        private void Refresh()
        {
            endpoints.Clear();
            endpoints.AddRange(ConnectionModel.Endpoints());
            shown.Clear();
            foreach (var c in ConnectionModel.All())
                if (ConnectionModel.IsGroupShown(ConnectionModel.GroupOf(c.Kind))) shown.Add(c);
            if (selected.HasValue)
            {
                Connection? still = null;
                foreach (var c in shown) if (c.Same(selected.Value)) still = c;
                selected = still;
            }
        }

        private void UpdateHover(Vector2 mouse)
        {
            hoverEndpoint = ConnectionDrawing.PickEndpoint(mouse, endpoints);
            hoverConnection = dragging || hoverEndpoint != null ? -1 : ConnectionDrawing.PickConnection(mouse, shown);
            if (hoverConnection >= shown.Count) hoverConnection = -1;
        }

        private void CollectTargets()
        {
            validTargets.Clear();
            foreach (var e in endpoints)
                if (ConnectionModel.OptionsFor(dragSource, e, false, out _).Count > 0) validTargets.Add(e);
        }

        private string DropHint(bool oneWay)
        {
            if (hoverEndpoint == null || hoverEndpoint == dragSource)
                return $"From '{dragSource.name}' ({ConnectionModel.Kind(dragSource)}): drop on a highlighted object.";
            var options = ConnectionModel.OptionsFor(dragSource, hoverEndpoint, oneWay, out string why);
            if (options.Count == 0) return why;
            if (options.Count == 1) return $"Release: {options[0].Label} — '{dragSource.name}' → '{hoverEndpoint.name}'.";
            return $"Release to choose: {string.Join(" / ", options.ConvertAll(o => o.Label))}.";
        }

        // ── edits ────────────────────────────────────────────────────────────

        private void Drop(Vector2 mouse, bool oneWay)
        {
            var target = ConnectionDrawing.PickEndpoint(mouse, endpoints);
            var options = ConnectionModel.OptionsFor(dragSource, target, oneWay, out string why);
            if (options.Count == 0)
            {
                Status = why;
                SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(why), 2.0);
                return;
            }
            string from = dragSource.name, to = target.name;
            if (options.Count == 1)
            {
                options[0].Apply();
                Status = $"Connected: {options[0].Label} — '{from}' → '{to}'.";
                return;
            }
            var menu = new GenericMenu();
            foreach (var o in options)
            {
                var option = o;
                menu.AddItem(new GUIContent(option.Label), false, () =>
                {
                    option.Apply();
                    Status = $"Connected: {option.Label} — '{from}' → '{to}'.";
                });
            }
            menu.ShowAsContext();
        }

        private void Remove(Connection c)
        {
            Status = $"Removed: {Describe(c)}.";
            ConnectionModel.Remove(c);
            selected = null;
            hoverConnection = -1;
        }

        private void ConnectionMenu(Connection c)
        {
            selected = c;
            var menu = new GenericMenu();
            menu.AddDisabledItem(new GUIContent(Describe(c)));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Delete"), false, () => Remove(c));
            if (ConnectionModel.CanReverse(c)) menu.AddItem(new GUIContent("Reverse"), false, () => { ConnectionModel.Reverse(c); selected = null; });
            else menu.AddDisabledItem(new GUIContent("Reverse"));
            menu.AddItem(new GUIContent($"Select '{c.Owner.name}' (holds it)"), false, () => Selection.activeObject = ((Component)c.Owner).gameObject);
            if (c.Owner != (Object)c.From) menu.AddItem(new GUIContent($"Select '{c.From.name}'"), false, () => Selection.activeGameObject = c.From.gameObject);
            if (c.Owner != (Object)c.To) menu.AddItem(new GUIContent($"Select '{c.To.name}'"), false, () => Selection.activeGameObject = c.To.gameObject);
            menu.ShowAsContext();
        }

        private static string Describe(Connection c) => $"{ConnectionModel.Label(c.Kind)}: '{c.From.name}' → '{c.To.name}'";

        // ── drawing ──────────────────────────────────────────────────────────

        private void Paint(Vector2 mouse)
        {
            for (int i = 0; i < shown.Count; i++)
                ConnectionDrawing.Draw(shown[i], i == hoverConnection, selected.HasValue && shown[i].Same(selected.Value));

            foreach (var e in endpoints)
                if (e != null && ConnectionModel.NeedsHandle(e))
                    ConnectionDrawing.DrawHandle(e, ConnectionModel.HandleColor(e), e == hoverEndpoint || e == dragSource);

            if (dragging)
            {
                foreach (var t in validTargets) if (t != null) ConnectionDrawing.Ring(t, new Color(0.3f, 1f, 0.3f, 0.8f));
                ConnectionDrawing.Ring(dragSource, Color.yellow, 1.2f);
                Vector3 from = ConnectionDrawing.Anchor(dragSource);
                Vector3 to = hoverEndpoint != null && hoverEndpoint != dragSource
                    ? ConnectionDrawing.Anchor(hoverEndpoint)
                    : MouseOnPlane(mouse, from.y);
                bool ok = hoverEndpoint != null && validTargets.Contains(hoverEndpoint);
                Handles.color = hoverEndpoint == null || hoverEndpoint == dragSource ? Color.yellow : ok ? Color.green : Color.red;
                Handles.DrawAAPolyLine(4f, from, to);
            }
            else if (hoverEndpoint != null)
                ConnectionDrawing.Ring(hoverEndpoint, Color.white);
        }

        private static Vector3 MouseOnPlane(Vector2 mouse, float y)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(mouse);
            var plane = new Plane(Vector3.up, new Vector3(0f, y, 0f));
            return plane.Raycast(ray, out float d) ? ray.GetPoint(d) : ray.GetPoint(10f);
        }
    }

    /// <summary>The Connect tool's panel: legend and group filters, what is drawn when the tool is off, the hint.</summary>
    [Overlay(typeof(SceneView), "SimuLean Connections", true)]
    public class SimuLeanConnectOverlay : IMGUIOverlay, ITransientOverlay
    {
        public bool visible => ToolManager.activeToolType == typeof(SimuLeanConnectTool);

        public override void OnGUI()
        {
            GUILayout.Label("Show", EditorStyles.boldLabel);
            foreach (ConnectionGroup g in System.Enum.GetValues(typeof(ConnectionGroup)))
            {
                GUILayout.BeginHorizontal();
                var rect = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12));
                rect.y += 3;
                EditorGUI.DrawRect(rect, ConnectionModel.ColorOf(g));
                bool on = GUILayout.Toggle(ConnectionModel.IsGroupShown(g), " " + g);
                if (on != ConnectionModel.IsGroupShown(g)) ConnectionModel.SetGroupShown(g, on);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(4);
            GUILayout.Label("When the tool is off", EditorStyles.boldLabel);
            bool idle = GUILayout.Toggle(ConnectionModel.ShowWhenIdle, " Draw connections");
            if (idle != ConnectionModel.ShowWhenIdle) ConnectionModel.ShowWhenIdle = idle;
            using (new EditorGUI.DisabledScope(!idle))
            {
                bool only = GUILayout.Toggle(ConnectionModel.OnlySelected, " Only of the selection");
                if (only != ConnectionModel.OnlySelected) ConnectionModel.OnlySelected = only;
            }
            GUILayout.Space(4);
            var wrap = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            GUILayout.Label(SimuLeanConnectTool.Status, wrap, GUILayout.Width(220));
            GUILayout.Label("Drag: connect (Shift: one-way track) · click line + Delete: remove · right-click line: menu · Esc: leave",
                            wrap, GUILayout.Width(220));
        }
    }
}
