using System;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// <b>Optional</b> floor-plan underlay: a CAD drawing rendered to a single-channel line texture by
    /// <c>Tools/FloorPlan/make_floor_texture.py</c>, laid on the floor under the model as one unlit, alpha-blended
    /// quad (shader <c>SimuLean/FloorPlanUnderlay</c>: line colour with alpha = texture R × opacity).
    ///
    /// <para><b>Placement.</b> The texture covers the drawing rectangle <c>rect_m</c> of <see cref="placementJson"/>
    /// (or <see cref="drawingRect"/>), in drawing metres. <see cref="drawingOrigin"/> is the drawing point placed at
    /// this GameObject's origin; drawing +X maps to local +X and drawing +Y to local +Z, so move / rotate the
    /// GameObject to align the plan with the model.</para>
    ///
    /// <para>Purely visual: built at run time, no collider (layer <i>Ignore Raycast</i>), no simulation events.
    /// <see cref="toggleKey"/> (default P) shows / hides it. Without a texture it disables itself.
    /// See Tools/FloorPlan/README.md and docs/architecture.md §4.7.2.</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Floor Plan Underlay")]
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Visualization, "Floor Plan Underlay", Description = "A CAD drawing / image under the model", Order = 1)]
    public class FloorPlanUnderlay : MonoBehaviour
    {
        [Tooltip("Line texture (R = line coverage) written by Tools/FloorPlan/make_floor_texture.py.")]
        public Texture2D texture;
        [Tooltip("Placement JSON written next to the texture (rect_m). Empty = use Drawing Rect.")]
        public TextAsset placementJson;
        [Tooltip("Drawing rectangle covered by the texture (drawing metres): x, y = min corner; width, height. Used without a JSON.")]
        public Rect drawingRect = new Rect(0, 0, 100, 100);
        [Tooltip("Drawing point (metres) placed at this GameObject's origin.")]
        public Vector2 drawingOrigin = Vector2.zero;

        [Header("Look")]
        [Tooltip("Shader SimuLean/FloorPlanUnderlay (serialized so player builds include it).")]
        public Shader shader;
        public Color lineColor = new Color(0.13f, 0.16f, 0.22f, 1f);
        [Range(0, 1)] public float opacity = 0.6f;
        [Tooltip("Height above the GameObject origin (avoids z-fighting with the floor).")]
        public float height = 0.004f;

        [Header("Runtime")]
        public bool visibleAtStart = true;
        [Tooltip("Key that shows / hides the plan (None = no key).")]
        public KeyCode toggleKey = KeyCode.P;

        /// <summary>The quad was built (texture and shader available).</summary>
        public bool Available { get; private set; }

        private MeshRenderer quad;
        private Material material;
        private Mesh mesh;
        private bool visible = true;

        [Serializable] private class Placement { public float[] rect_m; public string source; }

        public bool Visible
        {
            get => Available && visible;
            set { visible = value; if (quad != null) quad.enabled = value; }
        }

        private void Reset()
        {
            if (shader == null) shader = Shader.Find("SimuLean/FloorPlanUnderlay");
        }

        private void Awake()
        {
            visible = visibleAtStart;
            Build();
        }

        private void Update()
        {
            if (Available && toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey)) Visible = !visible;
        }

        private void OnDestroy()
        {
            if (material != null) Destroy(material);
            if (mesh != null) Destroy(mesh);
        }

        /// <summary>Drawing rectangle (metres) covered by the texture: from the JSON when valid, else <see cref="drawingRect"/>.</summary>
        public Rect GetDrawingRect()
        {
            if (placementJson != null)
            {
                try
                {
                    var p = JsonUtility.FromJson<Placement>(placementJson.text);
                    if (p?.rect_m != null && p.rect_m.Length == 4)
                        return Rect.MinMaxRect(p.rect_m[0], p.rect_m[1], p.rect_m[2], p.rect_m[3]);
                    Debug.LogWarning($"[FloorPlanUnderlay] {placementJson.name}: no rect_m — using Drawing Rect.");
                }
                catch (Exception ex) { Debug.LogWarning($"[FloorPlanUnderlay] bad placement JSON: {ex.Message}"); }
            }
            return drawingRect;
        }

        private void Build()
        {
            if (shader == null) shader = Shader.Find("SimuLean/FloorPlanUnderlay");
            if (texture == null || shader == null)
            {
                Debug.Log("[FloorPlanUnderlay] no texture or shader assigned — floor plan disabled.");
                enabled = false;
                return;
            }
            Rect r = GetDrawingRect();
            float x0 = r.xMin - drawingOrigin.x, z0 = r.yMin - drawingOrigin.y;
            float x1 = r.xMax - drawingOrigin.x, z1 = r.yMax - drawingOrigin.y;

            mesh = new Mesh { name = "FloorPlanQuad", hideFlags = HideFlags.DontSave };
            mesh.vertices = new[] { new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), new Vector3(x0, 0, z1) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };   // image row 0 = max y = uv v 1
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();

            var go = new GameObject("FloorPlanQuad") { layer = 2 };   // Ignore Raycast; no collider (picking unaffected)
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0, height, 0);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            quad = go.AddComponent<MeshRenderer>();
            material = new Material(shader) { name = "FloorPlanUnderlay (runtime)", hideFlags = HideFlags.DontSave };
            material.mainTexture = texture;
            material.color = lineColor;
            material.SetFloat("_Opacity", opacity);
            quad.sharedMaterial = material;
            quad.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            quad.receiveShadows = false;
            quad.enabled = visible;
            Available = true;
        }

        /// <summary>Applies colour / opacity changes made at run time.</summary>
        public void RefreshLook()
        {
            if (material == null) return;
            material.color = lineColor;
            material.SetFloat("_Opacity", opacity);
        }

        private void OnValidate() => RefreshLook();

        private void OnDrawGizmosSelected()
        {
            Rect r = GetDrawingRect();
            var m = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.9f);
            var c = new Vector3(r.center.x - drawingOrigin.x, height, r.center.y - drawingOrigin.y);
            Gizmos.DrawWireCube(c, new Vector3(r.width, 0.01f, r.height));
            Gizmos.DrawLine(Vector3.zero, new Vector3(1, 0, 0));   // local origin marker: +X
            Gizmos.DrawLine(Vector3.zero, new Vector3(0, 0, 1));   //                      +Z (drawing +Y)
            Gizmos.matrix = m;
        }
    }
}
