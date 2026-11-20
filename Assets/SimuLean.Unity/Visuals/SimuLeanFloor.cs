using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The floor of a modelling scene: a horizontal quad with a grid of 1 m lines and a heavier line every
    /// <see cref="gridCell"/> metres, laid on the world's metres whatever its size and position (the texture's tiling and
    /// offset follow the quad, per renderer). Created by <i>Set Up Scene</i> (100 × 100 m) and resized to the model by
    /// <i>Tools ▸ SimuLean ▸ Fit Floor to Model</i>; decoration only — no collider, nothing reads it.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshRenderer))]
    public class SimuLeanFloor : MonoBehaviour
    {
        [Tooltip("Metres between the heavier grid lines (one tile of the grid texture); the light lines are every metre.")]
        [Min(1f)] public float gridCell = 5f;

        private MaterialPropertyBlock block;
        private Vector3 appliedScale, appliedPosition;

        private void OnEnable() => Apply();
        private void OnValidate() => Apply();

        private void Update()
        {
            if (transform.lossyScale != appliedScale || transform.position != appliedPosition) Apply();
        }

        /// <summary>Size of the floor on the ground (x, z) in metres: the quad is 1 × 1 m, laid flat.</summary>
        public Vector2 Size => new Vector2(transform.lossyScale.x, transform.lossyScale.y);

        private void Apply()
        {
            var r = GetComponent<MeshRenderer>();
            if (r == null) return;
            appliedScale = transform.lossyScale;
            appliedPosition = transform.position;
            Vector2 size = Size;
            float cell = Mathf.Max(1f, gridCell);
            // the quad's UV (0,0) is its corner at −x, −z: offsetting by that corner puts the lines on world multiples
            float x0 = transform.position.x - size.x * 0.5f, z0 = transform.position.z - size.y * 0.5f;
            block ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            block.SetVector("_MainTex_ST", new Vector4(size.x / cell, size.y / cell, Frac(x0 / cell), Frac(z0 / cell)));
            r.SetPropertyBlock(block);
        }

        private static float Frac(float v) => v - Mathf.Floor(v);
    }
}
