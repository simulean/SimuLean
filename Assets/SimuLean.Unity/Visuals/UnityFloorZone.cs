using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A coloured area on the floor with its name: a waiting area, a diagnostic unit, the
    /// corridor — the zones a clinician recognises, read from above. A translucent slab of <see cref="size"/> with a darker
    /// border and the <see cref="label"/> lying at its front edge; built when enabled and never saved (only the fields
    /// are), so a scene stays light. Decoration only: no element, nothing in the model.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Visualization, "Floor Zone",
        Description = "A coloured, labelled area on the floor (a waiting area, a unit, a corridor): decoration, read from above.", Order = 41)]
    [AddComponentMenu("SimuLean/Floor Zone")]
    [ExecuteAlways]
    public sealed class UnityFloorZone : MonoBehaviour
    {
        [Tooltip("Width (X) and depth (Z) in metres.")]
        public Vector2 size = new Vector2(6f, 4f);
        [Tooltip("Fill colour (its alpha is the fill's).")]
        public Color color = new Color(0.36f, 0.66f, 0.80f, 0.25f);
        [Tooltip("The zone's name, written on the floor at its front edge (empty = none).")]
        public string label = "Zone";
        [Tooltip("Height of the letters (m).")]
        [Min(0.05f)] public float letterHeight = 0.45f;

        public const string VisualChild = "FloorZoneVisual";

        /// <summary>The built visual (the <c>FloorZoneVisual</c> child), or null.</summary>
        public Transform Visual => transform.Find(VisualChild);

        private void OnEnable() => Rebuild();
        private void OnDisable() => FloorMarker.Remove(transform, VisualChild);

#if UNITY_EDITOR
        private void OnValidate()
        {
            UnityEditor.EditorApplication.delayCall += () => { if (this != null && isActiveAndEnabled) Rebuild(); };
        }
#endif

        /// <summary>Rebuilds the slab, border and label from the fields.</summary>
        [SimuLeanAction("Rebuild the zone after its corners or size changed")]
        public void Rebuild()
        {
            FloorMarker.Remove(transform, VisualChild);
            FloorMarker.Build(transform, VisualChild, size, color, label, letterHeight, FontStyle.Bold);
        }
    }
}
