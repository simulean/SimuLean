using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SimuLean.Transport;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Scene-view editor for <see cref="UnityTransportNetwork"/> (unified
    /// proposal §7 / backlog Tarea 2-B & 2-C):
    ///
    ///  - **Add Nodes** mode: Shift+Click places a node on the ground
    ///    (raycast against <c>groundMask</c>, falling back to the network's
    ///    Y plane).
    ///  - Connecting nodes is done with the SimuLean Connect tool (Shift+C): drag
    ///    from a node to another → an edge with the network's defaults.
    ///  - **Select** mode: click an edge midpoint disc to select it; edit its
    ///    direction, type and length in the Inspector; Delete key removes it.
    ///  - **Generate MST**: connects all anchor transforms (creating nodes
    ///    where needed) with a minimum spanning tree as a starting skeleton.
    ///
    /// Every mutation is Undo-registered.
    /// </summary>
    [CustomEditor(typeof(UnityTransportNetwork))]
    public class UnityTransportNetworkEditor : UnityEditor.Editor
    {
        private enum EditMode { Off, AddNodes }

        private static EditMode mode = EditMode.Off;
        private int selectedEdge = -1;

        private UnityTransportNetwork Net => (UnityTransportNetwork)target;

        // ── inspector ────────────────────────────────────────────────────────

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene editing", EditorStyles.boldLabel);

            EditMode newMode = (EditMode)GUILayout.Toolbar((int)mode,
                new[] { "Off", "Add Nodes (Shift+Click)" });
            if (newMode != mode)
            {
                mode = newMode;
                SceneView.RepaintAll();
            }
            if (GUILayout.Button(new GUIContent("Connect Nodes… (Shift+C)", "Opens the SimuLean Connect tool: drag from a node to another (Shift: one-way).")))
                SimuLeanConnectTool.Activate();

            // ── selected edge panel ──
            if (selectedEdge >= 0 && selectedEdge < Net.edges.Count)
            {
                var e = Net.edges[selectedEdge];
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(
                    $"Selected edge: {SafeId(e.nodeA)} {(e.bidirectional ? "↔" : "→")} {SafeId(e.nodeB)}",
                    EditorStyles.boldLabel);

                EditorGUI.BeginChangeCheck();
                bool bidi = EditorGUILayout.Toggle("Bidirectional", e.bidirectional);
                TrackType type = (TrackType)EditorGUILayout.EnumPopup("Track type", e.trackType);
                float len = EditorGUILayout.FloatField(
                    new GUIContent("Manual length (m)", "< 0 = auto (euclidean)"), e.manualLength);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(Net, "Edit Edge");
                    e.bidirectional = bidi;
                    e.trackType = type;
                    e.manualLength = len;
                    MarkDirty();
                }

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Reverse direction"))
                {
                    Undo.RecordObject(Net, "Reverse Edge");
                    var tmp = e.nodeA; e.nodeA = e.nodeB; e.nodeB = tmp;
                    MarkDirty();
                }
                if (GUILayout.Button("Delete edge"))
                    DeleteSelectedEdge();
                EditorGUILayout.EndHorizontal();
            }

            // ── tools ──
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);

            if (GUILayout.Button("Collect Child Nodes"))
            {
                Undo.RecordObject(Net, "Collect Nodes");
                Net.CollectChildNodes();
                MarkDirty();
            }

            if (GUILayout.Button("Place Elements On Closest Node"))
                Net.PlaceElementsOnClosestNode();   // logs its own results; writes via serialized fields

            if (GUILayout.Button(new GUIContent("Generate MST From Anchor Points",
                "Creates nodes at anchors that lack one and connects everything with a minimum spanning tree.")))
                GenerateMst();

            if (GUILayout.Button("Remove Edges With Missing Nodes"))
            {
                Undo.RecordObject(Net, "Clean Edges");
                int removed = Net.edges.RemoveAll(e2 => e2.nodeA == null || e2.nodeB == null);
                selectedEdge = -1;
                Debug.Log($"[{Net.name}] Removed {removed} invalid edge(s).");
                MarkDirty();
            }
        }

        // ── scene view ───────────────────────────────────────────────────────

        private void OnSceneGUI()
        {
            Event evt = Event.current;

            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Delete
                && selectedEdge >= 0)
            {
                DeleteSelectedEdge();
                evt.Use();
                return;
            }

            switch (mode)
            {
                case EditMode.AddNodes: HandleAddNodes(evt); break;
            }

            DrawEdgeHandles();
        }

        private void HandleAddNodes(Event evt)
        {
            // keep clicks from selecting other objects while in this mode
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            if (evt.type != EventType.MouseDown || evt.button != 0 || !evt.shift)
                return;

            Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
            Vector3 position;
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, 10000f, Net.groundMask))
            {
                position = hit.point;
            }
            else
            {
                // fall back to the network's Y plane
                Plane plane = new Plane(Vector3.up, new Vector3(0, Net.transform.position.y, 0));
                float enter;
                if (!plane.Raycast(ray, out enter))
                    return;
                position = ray.GetPoint(enter);
            }

            CreateNode(position);
            evt.Use();
        }

        private void CreateNode(Vector3 position)
        {
            var go = new GameObject(UniqueNodeName());
            Undo.RegisterCreatedObjectUndo(go, "Add Transport Node");
            go.transform.SetParent(Net.transform, worldPositionStays: true);
            go.transform.position = position;
            var node = go.AddComponent<UnityTransportNode>();

            Undo.RecordObject(Net, "Add Transport Node");
            Net.nodes.Add(node);
            MarkDirty();
        }

        private string UniqueNodeName()
        {
            int i = Net.nodes.Count;
            string name;
            do { name = $"Node_{i++}"; }
            while (Net.transform.Find(name) != null);
            return name;
        }

        private void DrawEdgeHandles()
        {
            for (int i = 0; i < Net.edges.Count; i++)
            {
                var e = Net.edges[i];
                if (e.nodeA == null || e.nodeB == null)
                    continue;

                Vector3 mid = (e.nodeA.transform.position + e.nodeB.transform.position) * 0.5f;
                float size = HandleUtility.GetHandleSize(mid) * 0.09f;

                Handles.color = i == selectedEdge ? Color.yellow : Color.white;
                if (Handles.Button(mid, Quaternion.identity, size, size * 1.5f,
                                   Handles.DotHandleCap))
                {
                    selectedEdge = i;
                    Repaint();
                }

                if (i == selectedEdge)
                {
                    float length = e.manualLength >= 0
                        ? e.manualLength
                        : Vector3.Distance(e.nodeA.transform.position, e.nodeB.transform.position);
                    Handles.Label(mid + Vector3.up * 0.4f,
                        $"{length:F1} m{(e.manualLength >= 0 ? " (manual)" : "")} · {e.trackType}");
                }
            }
        }

        private void DeleteSelectedEdge()
        {
            if (selectedEdge < 0 || selectedEdge >= Net.edges.Count)
                return;
            Undo.RecordObject(Net, "Delete Edge");
            Net.edges.RemoveAt(selectedEdge);
            selectedEdge = -1;
            MarkDirty();
        }

        // ── MST generation (Tarea 2-C, Prim) ─────────────────────────────────

        private void GenerateMst()
        {
            if (Net.anchorPoints == null || Net.anchorPoints.Count < 2)
            {
                Debug.LogWarning($"[{Net.name}] Need at least 2 anchor points for MST generation.");
                return;
            }

            Undo.RecordObject(Net, "Generate MST");

            // 1. ensure every anchor has a node (reuse one within 0.5 m)
            var anchorNodes = new List<UnityTransportNode>();
            foreach (Transform anchor in Net.anchorPoints)
            {
                if (anchor == null)
                    continue;

                UnityTransportNode nearest = null;
                float best = 0.5f * 0.5f;
                foreach (UnityTransportNode n in Net.nodes)
                {
                    if (n == null) continue;
                    float d = (n.transform.position - anchor.position).sqrMagnitude;
                    if (d < best) { best = d; nearest = n; }
                }

                if (nearest == null)
                {
                    CreateNode(anchor.position);
                    nearest = Net.nodes[Net.nodes.Count - 1];
                    nearest.gameObject.name = $"Node_{anchor.name}";
                }
                if (!anchorNodes.Contains(nearest))
                    anchorNodes.Add(nearest);
            }

            if (anchorNodes.Count < 2)
                return;

            // 2. Prim's MST over euclidean distances
            var inTree = new List<UnityTransportNode> { anchorNodes[0] };
            var outTree = new List<UnityTransportNode>(anchorNodes.GetRange(1, anchorNodes.Count - 1));
            int added = 0;

            while (outTree.Count > 0)
            {
                UnityTransportNode bestIn = null, bestOut = null;
                float bestDist = float.PositiveInfinity;

                foreach (var a in inTree)
                    foreach (var b in outTree)
                    {
                        float d = (a.transform.position - b.transform.position).sqrMagnitude;
                        if (d < bestDist) { bestDist = d; bestIn = a; bestOut = b; }
                    }

                bool exists = false;
                foreach (var e in Net.edges)
                    if ((e.nodeA == bestIn && e.nodeB == bestOut) ||
                        (e.nodeA == bestOut && e.nodeB == bestIn))
                    { exists = true; break; }

                if (!exists)
                {
                    Net.edges.Add(new UnityTransportNetwork.EdgeDefinition
                    {
                        nodeA = bestIn,
                        nodeB = bestOut,
                        bidirectional = Net.newEdgeBidirectional,
                        trackType = Net.newEdgeType
                    });
                    added++;
                }

                inTree.Add(bestOut);
                outTree.Remove(bestOut);
            }

            Debug.Log($"[{Net.name}] MST generation: {added} edge(s) added over {anchorNodes.Count} anchor node(s). Adjust manually as needed.");
            MarkDirty();
        }

        // ── misc ─────────────────────────────────────────────────────────────

        private static string SafeId(UnityTransportNode n) => n != null ? n.NodeId : "<missing>";

        private void MarkDirty()
        {
            EditorUtility.SetDirty(Net);
            Net.InvalidateBuild();
            SceneView.RepaintAll();
        }
    }
}
