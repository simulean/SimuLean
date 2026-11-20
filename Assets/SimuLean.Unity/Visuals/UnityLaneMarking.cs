using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The floor marking of an accumulation lane (the Accumulation Lane preset): a painted outline of the
    /// lane, <see cref="width"/> wide and as long as the lane, with a chevron at its exit; like the conveyor's rollers it
    /// is a child of the <see cref="UnityLengthLimitedQueue"/> (one per lane, in order) and follows the lane's length and
    /// spacing when they are edited (<see cref="FitTo"/>). The outline runs along its own −Z to the exit at its origin; the lane turns it onto its own −X (flow along +X).
    /// Visual only: one generated mesh, no collider.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class UnityLaneMarking : MonoBehaviour
    {
        [Tooltip("Lane width (m).")]
        [Min(0.2f)] public float width = 1.4f;
        [Tooltip("Lane length (m); set by the lane.")]
        [Min(0.1f)] public float length = 6f;
        [Tooltip("Width of the painted lines (m).")]
        [Min(0.01f)] public float lineWidth = 0.08f;

        private Mesh mesh;

        private void OnEnable() => Rebuild();
        private void OnValidate() => Rebuild();

        /// <summary>Fits the marking to a lane of <paramref name="laneLength"/>, <paramref name="across"/> towards the lane's
        /// front (−Z): the outline, drawn along its own −Z, is turned onto the lane's −X (the element frame: flow along +X).</summary>
        public void FitTo(float laneLength, float across)
        {
            length = Mathf.Max(0.1f, laneLength);
            transform.localPosition = new Vector3(0f, 0.006f, -across);
            transform.localRotation = ItemVisuals.AlongFlow;
            Rebuild();
        }

        /// <summary>Rebuilds the outline mesh for the current size.</summary>
        public void Rebuild()
        {
            var filter = GetComponent<MeshFilter>();
            if (filter == null) return;
            if (mesh == null) { mesh = new Mesh { name = "LaneMarking", hideFlags = HideFlags.DontSave }; }
            mesh.Clear();
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();
            float w = width * 0.5f, l = length, lw = lineWidth;
            Rect(v, t, -w, -w + lw, -l, 0f);          // left side
            Rect(v, t, w - lw, w, -l, 0f);            // right side
            Rect(v, t, -w, w, -l, -l + lw);           // entry line
            // the exit chevron, a little inside the lane
            float a = Mathf.Min(w * 0.6f, 0.4f), z0 = -0.15f;
            Quad(v, t, new Vector3(-a, 0, z0 - a), new Vector3(-a + lw, 0, z0 - a), new Vector3(lw * 0.5f, 0, z0), new Vector3(0, 0, z0 + lw));
            Quad(v, t, new Vector3(a - lw, 0, z0 - a), new Vector3(a, 0, z0 - a), new Vector3(0, 0, z0 + lw), new Vector3(-lw * 0.5f, 0, z0));
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            filter.sharedMesh = mesh;
        }

        private static void Rect(System.Collections.Generic.List<Vector3> v, System.Collections.Generic.List<int> t,
                                 float x0, float x1, float z0, float z1)
            => Quad(v, t, new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), new Vector3(x0, 0, z1));

        // A quad facing up (corners counter-clockwise seen from below = clockwise from above).
        private static void Quad(System.Collections.Generic.List<Vector3> v, System.Collections.Generic.List<int> t,
                                 Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(i); t.Add(i + 2); t.Add(i + 1);
            t.Add(i); t.Add(i + 3); t.Add(i + 2);
        }

        private void OnDestroy()
        {
            if (mesh != null) { if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh); }
        }
    }
}
