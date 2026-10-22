using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Shows an item's label in the scene — the block number of a ship block, an order number,
    /// any label — as world-space text over the item, turned to the camera. The text is the label's value
    /// (<c>#id</c> = the item's id, <c>#name</c> its name) in <see cref="format"/>; nothing is shown when the item has no
    /// such label. Bound to its item by <see cref="ItemViews"/>. View only.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Item Label View")]
    public class ItemLabelView : MonoBehaviour, IItemView
    {
        [Tooltip("The label shown (e.g. block); #id = the item's id, #name = its name.")]
        public string label = "block";
        [Tooltip("The text: {0} = the label's value (e.g. B{0}).")]
        public string format = "{0}";
        [Tooltip("Height of the text above the item's top (m).")]
        public float above = 0.5f;
        [Tooltip("Height of a character (m).")]
        [Min(0.01f)] public float size = 1.0f;
        public Color color = new Color(1f, 0.92f, 0.3f);
        [Tooltip("Turn the text to the camera every frame.")]
        public bool faceCamera = true;

        private TextMesh text;

        public string Text => text != null ? text.text : null;

        public void Bind(Item item)
        {
            string v = ItemViews.LabelText(item, label);
            if (string.IsNullOrEmpty(v)) { if (text != null) text.gameObject.SetActive(false); return; }
            if (text == null)
            {
                var go = new GameObject("Label");
                go.transform.SetParent(transform, false);
                text = go.AddComponent<TextMesh>();
                text.anchor = TextAnchor.LowerCenter;
                text.alignment = TextAlignment.Center;
                text.fontSize = 64;
                var font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (font != null) { text.font = font; go.GetComponent<MeshRenderer>().sharedMaterial = font.material; }
            }
            text.gameObject.SetActive(true);
            text.text = string.Format(format, v);
            text.color = color;
            text.characterSize = size / 10f * 64f / text.fontSize;
            Place();
        }

        private void Place()
        {
            if (text == null) return;
            var parent = text.transform.parent;
            text.transform.SetParent(null, true);          // the item's own bounds, without the text
            float top = ItemVisuals.TopAbovePivot(gameObject);
            text.transform.SetParent(parent, true);
            text.transform.position = transform.position + Vector3.up * (top + above);
        }

        private void LateUpdate()
        {
            if (text == null || !faceCamera || !text.gameObject.activeSelf) return;
            var cam = SimRecorder.ViewCamera;   // the take's camera while recording
            if (cam != null) text.transform.rotation = cam.transform.rotation;   // parallel to the view: upright on screen
        }
    }
}
