using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Floor arrows that follow the model's connections (healthcare §6 "pathway arrows"): chevrons every
    /// <see cref="spacing"/> metres along each connection (an element's Next Elements) between the elements under
    /// <see cref="root"/> (empty = the whole scene) — the pathway a visitor can follow on the floor, from the entrance to
    /// the exit. Built when enabled and on <i>Rebuild</i>, never saved (only the fields are); decoration only.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Visualization, "Pathway Arrows",
        Description = "Floor chevrons along the model's connections: the patients' pathway, drawn from the model.", Order = 42)]
    [AddComponentMenu("SimuLean/Pathway Arrows")]
    [ExecuteAlways]
    public sealed class UnityPathwayArrows : MonoBehaviour
    {
        [Tooltip("The elements whose connections are drawn (and their children); empty = every element of the scene.")]
        public Transform root;
        [Tooltip("Distance between chevrons (m).")]
        [Min(0.2f)] public float spacing = 1.2f;
        [Tooltip("Size of a chevron (m).")]
        [Min(0.05f)] public float size = 0.45f;
        [Tooltip("Free floor around each element where no chevron is drawn (m).")]
        [Min(0f)] public float clearance = 1.2f;
        [Tooltip("Height above the floor (m).")]
        public float height = 0.01f;
        public Color color = new Color(0.10f, 0.55f, 0.55f, 0.85f);

        public const string VisualChild = "PathwayArrowsVisual";
        private const float LaneOffset = 0.35f;

        /// <summary>The chevrons built now.</summary>
        public int ChevronCount { get; private set; }

        private void OnEnable() => Rebuild();
        private void OnDisable() => FloorMarker.Remove(transform, VisualChild);

#if UNITY_EDITOR
        private void OnValidate()
        {
            UnityEditor.EditorApplication.delayCall += () => { if (this != null && isActiveAndEnabled) Rebuild(); };
        }
#endif

        /// <summary>The connections drawn: (from, to) pairs of the elements under the root.</summary>
        public List<(SElement from, SElement to)> Connections()
        {
            var list = new List<(SElement, SElement)>();
            var elements = root != null ? new List<SElement>(root.GetComponentsInChildren<SElement>()) : new List<SElement>(UnityCompat.FindAll<SElement>());
            var inside = new HashSet<SElement>(elements);
            foreach (var e in elements)
            {
                if (e.nextElements == null || e.sendTo != SElement.SendMode.Elements) continue;
                foreach (var n in e.nextElements)
                    if (n != null && inside.Contains(n) && n != e) list.Add((e, n));
            }
            return list;
        }

        /// <summary>Rebuilds the chevrons from the connections.</summary>
        [SimuLeanAction("Rebuild the arrows along the path")]
        public void Rebuild()
        {
            FloorMarker.Remove(transform, VisualChild);
            var visual = new GameObject(VisualChild) { hideFlags = HideFlags.DontSave };
            visual.transform.SetParent(transform, false);
            visual.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            if (transform.lossyScale != Vector3.one) visual.transform.localScale = new Vector3(1f / transform.lossyScale.x, 1f / transform.lossyScale.y, 1f / transform.lossyScale.z);
            int n = 0;
            var network = PatientsNetwork();
            var drawn = new HashSet<string>();
            foreach (var (from, to) in Connections())
            {
                var path = PathOf(from, to, network);
                if (path == null) continue;
                // one set of chevrons per walked route (several connections may share it)
                if (path.Count > 2 && !drawn.Add(string.Join(";", path))) continue;
                float total = 0f;
                for (int i = 1; i < path.Count; i++) total += Vector3.Distance(path[i - 1], path[i]);
                if (total < 2f * clearance + 0.1f) continue;
                float walked = 0f;
                for (int i = 1; i < path.Count; i++)
                {
                    Vector3 a = path[i - 1], b = path[i];
                    Vector3 d = b - a;
                    float length = d.magnitude;
                    if (length < 1e-4f) continue;
                    Vector3 dir = d / length;
                    // a walked route keeps to its right (a two-way corridor shows two lanes, not crossed chevrons)
                    if (path.Count > 2) { Vector3 right = Vector3.Cross(Vector3.up, dir).normalized * LaneOffset; a += right; }
                    // chevrons every spacing along the whole path, clear of its two ends
                    float first = Mathf.Ceil((walked - clearance) / spacing) * spacing + clearance - walked;
                    for (float s = Mathf.Max(0f, first); s <= length + 1e-4f && walked + s <= total - clearance + 1e-4f; s += spacing)
                    {
                        FloorMarker.Chevron(visual.transform, visual.transform.InverseTransformPoint(a + dir * s), visual.transform.InverseTransformDirection(dir), size, color);
                        n++;
                    }
                    walked += length;
                }
            }
            ChevronCount = n;
        }

        // The way drawn for a connection: the walked route on the network when the items walk (from the origin's location
        // to the destination's; none when they share it — e.g. two steps at home), else the straight line.
        private List<Vector3> PathOf(SElement from, SElement to, SimuLean.Transport.TransportNetwork network)
        {
            var path = new List<Vector3>();
            if (from.walkBy != null && from.walkBy.IsUsed && from.walkBy.route == WalkRoute.Network && network != null
                && from.location != null && to.location != null)
            {
                if (from.location.NodeId == to.location.NodeId) return null;
                var route = network.GetRoute(from.location.NodeId, to.location.NodeId);
                if (route != null)
                {
                    foreach (var node in route.Nodes) path.Add(new Vector3((float)node.X, height, (float)node.Z));
                    return path;
                }
            }
            Vector3 a = from.transform.position, b = to.transform.position;
            a.y = b.y = height;
            path.Add(a); path.Add(b);
            return path;
        }

        // the scene's transport network as the model builds it (fresh: the scene may have changed since the last run)
        private static SimuLean.Transport.TransportNetwork PatientsNetwork()
        {
            var net = UnityCompat.FindAny<UnityTransportNetwork>();
            if (net == null) return null;
            try { return SimuLean.Serialization.TransportStorageConfigCodec.BuildNetwork(net.ToConfig()); }
            catch (System.Exception) { return null; }
        }
    }
}
