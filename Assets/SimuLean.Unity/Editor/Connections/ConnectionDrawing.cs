using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Draws the connections of the open scenes in the Scene view — the one drawer of them (the Connect tool draws
    /// with it too, adding hover and selection). Idle drawing follows <see cref="ConnectionModel.ShowWhenIdle"/>, the
    /// group filters and "only selected".
    /// </summary>
    [InitializeOnLoad]
    public static class ConnectionDrawing
    {
        static ConnectionDrawing()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView view)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (ToolManager.activeToolType == typeof(SimuLeanConnectTool)) return;   // the tool draws them itself
            if (!ConnectionModel.ShowWhenIdle) return;
            foreach (var c in ConnectionModel.All())
                if (ConnectionModel.IsShown(c)) Draw(c, false, false);
        }

        // ── geometry ─────────────────────────────────────────────────────────

        /// <summary>Where a connection ends on an object (<see cref="SceneConnections.Anchor"/>).</summary>
        public static Vector3 Anchor(Component c) => SceneConnections.Anchor(c);

        /// <summary>The two drawn ends of a connection, pulled in from the anchors so arrows stay visible.</summary>
        public static void Ends(Connection c, out Vector3 a, out Vector3 b)
        {
            a = Anchor(c.From);
            b = Anchor(c.To);
            // two connections between the same pair in opposite directions: offset them sideways
            if (c.Kind == ConnectionKind.Flow && c.To is SElement t && t.nextElements != null && t.nextElements.Contains(c.From as SElement))
            {
                Vector3 side = Vector3.Cross((b - a).normalized, Vector3.up) * HandleUtility.GetHandleSize((a + b) * 0.5f) * 0.06f;
                a += side; b += side;
            }
        }

        // ── drawing ──────────────────────────────────────────────────────────

        public static void Draw(Connection c, bool hovered, bool selected)
        {
            if (!c.IsValid) return;
            Ends(c, out var a, out var b);
            Color col = ConnectionModel.ColorOf(ConnectionModel.GroupOf(c.Kind));
            if (selected) col = Color.yellow;
            else if (hovered) col = Color.Lerp(col, Color.white, 0.5f);
            float width = selected || hovered ? 5f : 2.5f;

            Handles.color = col;
            bool dashed = ConnectionModel.GroupOf(c.Kind) == ConnectionGroup.Resources
                          || ConnectionModel.GroupOf(c.Kind) == ConnectionGroup.Locations
                          || ConnectionModel.GroupOf(c.Kind) == ConnectionGroup.Downtimes
                          || ConnectionModel.GroupOf(c.Kind) == ConnectionGroup.Logic;
            if (dashed) Handles.DrawDottedLine(a, b, 4f);
            else Handles.DrawAAPolyLine(width, a, b);
            if (dashed && (selected || hovered)) Handles.DrawAAPolyLine(width, a, b);

            if (ConnectionModel.IsDirected(c)) Arrow(a, b, col);
            if (selected || hovered)
            {
                var style = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = col } };
                Handles.Label(Vector3.Lerp(a, b, 0.5f), $"{ConnectionModel.Label(c.Kind)}: {c.From.name} → {c.To.name}", style);
            }
        }

        private static void Arrow(Vector3 a, Vector3 b, Color col)
        {
            Vector3 dir = b - a;
            if (dir.sqrMagnitude < 1e-6f) return;
            Vector3 at = Vector3.Lerp(a, b, 0.6f);
            float size = HandleUtility.GetHandleSize(at) * 0.12f;
            Handles.color = col;
            Handles.ConeHandleCap(0, at, Quaternion.LookRotation(dir), size, EventType.Repaint);
        }

        /// <summary>The marker of an object without a mesh of its own (lists, teams, nodes, downtimes) and its name.</summary>
        public static void DrawHandle(Component c, Color col, bool strong)
        {
            Vector3 p = Anchor(c);
            float size = HandleUtility.GetHandleSize(p) * (strong ? 0.13f : 0.09f);
            var cam = Camera.current;
            Vector3 normal = cam != null ? -cam.transform.forward : Vector3.up;
            Handles.color = new Color(col.r, col.g, col.b, strong ? 0.9f : 0.55f);
            Handles.DrawSolidDisc(p, normal, size);
            Handles.color = Color.black;
            Handles.DrawWireDisc(p, normal, size);
            Handles.Label(p + Vector3.up * size * 1.6f, c.name, EditorStyles.miniLabel);
        }

        /// <summary>A ring around an object (a valid drop target, the hovered object, the drag source).</summary>
        public static void Ring(Component c, Color col, float scale = 1f)
        {
            Vector3 p = Anchor(c);
            float r = HandleUtility.GetHandleSize(p) * 0.2f * scale;
            var sElement = c as SElement;
            var rend = sElement != null ? c.GetComponentInChildren<Renderer>() : null;
            if (rend != null) r = Mathf.Max(r, rend.bounds.extents.magnitude * 0.8f);
            var cam = Camera.current;
            Handles.color = col;
            Handles.DrawWireDisc(p, cam != null ? -cam.transform.forward : Vector3.up, r, 3f);
        }

        // ── picking (no colliders needed) ────────────────────────────────────

        private const float HandlePickPixels = 14f;

        /// <summary>The SimuLean object under the mouse: a drawn marker first, then a rendered object.</summary>
        public static Component PickEndpoint(Vector2 mouse, List<Component> endpoints)
        {
            Component best = null;
            float bestD = HandlePickPixels;
            foreach (var e in endpoints)
            {
                if (e == null || !ConnectionModel.NeedsHandle(e)) continue;
                float d = Vector2.Distance(HandleUtility.WorldToGUIPoint(Anchor(e)), mouse);
                if (d < bestD) { bestD = d; best = e; }
            }
            if (best != null) return best;
            return ConnectionModel.EndpointOf(HandleUtility.PickGameObject(mouse, false));
        }

        /// <summary>The connection whose line is closest to the mouse, within a few pixels.</summary>
        public static int PickConnection(Vector2 mouse, List<Connection> shown, float maxPixels = 6f)
        {
            int best = -1;
            float bestD = maxPixels;
            for (int i = 0; i < shown.Count; i++)
            {
                Ends(shown[i], out var a, out var b);
                Vector2 sa = HandleUtility.WorldToGUIPoint(a), sb = HandleUtility.WorldToGUIPoint(b);
                float d = HandleUtility.DistancePointLine(mouse, sa, sb);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
    }
}
