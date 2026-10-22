using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A text in the scene: with a <see cref="target"/> it shows what the element holds —
    /// "12 patients waiting" — and can hide the element's items (patients waiting at home drawn as a number instead of a
    /// crowd); without one it is a zone label ("Diagnostic unit", "Waiting room"). The count is the element's content from its
    /// statistics (the Link layer, the single source of item flow). <b>Read-only</b>: it toggles the items' renderers and
    /// nothing else — no event, no item moved — so the run is unchanged.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Visualization, "Element Label",
        Description = "A text over an element (its content: {n}, its name: {name}) or a zone label; can hide the element's items " +
                      "(e.g. patients waiting at home shown as a number).", Order = 40)]
    [AddComponentMenu("SimuLean/Element Label")]
    public sealed class UnityElementLabel : MonoBehaviour
    {
        public enum Facing { Camera, Floor }

        [Tooltip("The element whose content is shown (empty = the SElement on this GameObject or above it; none = a zone label).")]
        public SElement target;
        [Tooltip("The text. {n} = items in the element now, {name} = its name. A zone label is the text itself.")]
        [TextArea(1, 3)] public string text = "{n} waiting";
        [Tooltip("Hide the element's items (they still flow; only their drawing is switched off).")]
        public bool hideItems;
        [Tooltip("Camera: parallel to the view (upright on screen). Floor: lying on the floor, read from above.")]
        public Facing facing = Facing.Camera;
        [Tooltip("Height of the text above this object (m).")]
        public float height = 2.2f;
        [Tooltip("Size of the characters (m per font unit).")]
        [Min(0.001f)] public float characterSize = 0.06f;
        public Color color = new Color(0.08f, 0.22f, 0.32f);

        private TextMesh mesh;
        private SElement source;
        private readonly HashSet<GameObject> hidden = new HashSet<GameObject>();
        private readonly HashSet<GameObject> present = new HashSet<GameObject>();
        private readonly List<GameObject> gone = new List<GameObject>();

        // How many labels hide each item: an item moving between two hidden elements stays hidden whatever the order in
        // which the two labels update.
        private static readonly Dictionary<GameObject, int> hiders = new Dictionary<GameObject, int>();

        // the elements whose items a label hides: their layout is skipped (nothing drawn to place; review2 #22)
        private static readonly HashSet<SElement> hiding = new HashSet<SElement>();

        /// <summary>True when a label hides the items of <paramref name="element"/> in Play (layouts need not place them).</summary>
        public static bool HidesItemsOf(SElement element) => element != null && hiding.Contains(element);

        // the element's flow when the items were last hidden: nothing changed, nothing to do this frame
        private double lastIn = -1, lastOut = -1;

        /// <summary>The element shown (the target, else the SElement on this object or a parent), or null for a zone label.</summary>
        public SElement Source => target != null ? target : GetComponentInParent<SElement>();

        private void OnEnable()
        {
            source = Source;
            var go = new GameObject("LabelText") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * height;
            mesh = go.AddComponent<TextMesh>();
            var font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            mesh.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            mesh.fontSize = 64;
            mesh.characterSize = characterSize;
            mesh.anchor = TextAnchor.LowerCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.fontStyle = FontStyle.Bold;
            mesh.color = color;
            if (facing == Facing.Floor) go.transform.rotation = Quaternion.Euler(90f, transform.eulerAngles.y, 0f);
            Refresh();
        }

        private void OnDisable()
        {
            if (source != null) hiding.Remove(source);
            foreach (var g in hidden) if (g != null) Hide(g, false); else hiders.Remove(g);
            hidden.Clear();
            lastIn = lastOut = -1;
            if (mesh != null) DestroyImmediate(mesh.gameObject);
            mesh = null;
        }

        private void LateUpdate() => Refresh();

        /// <summary>The label's text now (the template with {n} and {name} filled in).</summary>
        public string CurrentText
        {
            get
            {
                SElement s = source != null ? source : Source;   // resolved now outside Play (OnEnable caches it)
                Element el = s != null ? s.GetElement() : null;
                int n = el != null ? (int)System.Math.Round(el.GetStatsContentCurrent()) : 0;
                string name = s != null ? s.name : "";
                return (text ?? "").Replace("{n}", n.ToString()).Replace("{name}", name);
            }
        }

        private void Refresh()
        {
            if (mesh == null) return;
            mesh.text = CurrentText;
            if (facing == Facing.Camera)
            {
                var cam = SimRecorder.ViewCamera;   // the take's camera while recording
                // parallel to the view, not turned to the camera's position: a label below a high camera spun round (its
                // direction to the camera nearly vertical) and every label leaned off the screen's centre
                if (cam != null) mesh.transform.rotation = cam.transform.rotation;
            }
            if (hideItems && Application.isPlaying) HideItems();
        }

        // The items an element holds, for the element types that keep them visible.
        private static IEnumerable<Item> HeldItems(Element el)
        {
            switch (el)
            {
                case ItemsQueue q: return q.GetItems();
                case MultiServer m: return m.GetItems();
                case GateQueue g: return g.GetItems();
                case LengthLimitedQueue l: return l.Items;
                case Delay d: return d.Items;
                case SimuLean.Healthcare.Agenda a: return a.Items;
                default: return null;
            }
        }

        private void HideItems()
        {
            Element el = source != null ? source.GetElement() : null;
            if (source != null) hiding.Add(source);
            // react to the flow only: the held items change when an item enters or leaves (was every item every frame)
            double inCount = el != null ? el.GetStatsInputCount() : -2, outCount = el != null ? el.GetStatsOutputCount() : -2;
            if (inCount == lastIn && outCount == lastOut) return;
            lastIn = inCount; lastOut = outCount;
            var items = el != null ? HeldItems(el) : null;
            present.Clear();
            if (items != null)
                foreach (Item it in items)
                    if (it.vItem is GameObject g && g != null)
                    {
                        present.Add(g);
                        if (hidden.Add(g)) Hide(g, true);
                    }
            gone.Clear();
            foreach (var g in hidden) if (!present.Contains(g)) gone.Add(g);
            // an item destroyed while hidden (a restart clears the views) leaves the shared count too (review2 #29)
            foreach (var g in gone) { hidden.Remove(g); if (g != null) Hide(g, false); else hiders.Remove(g); }
        }

        private static void Hide(GameObject g, bool hide)
        {
            hiders.TryGetValue(g, out int n);
            n = hide ? n + 1 : n - 1;
            if (n <= 0) hiders.Remove(g); else hiders[g] = n;
            bool show = n <= 0;
            foreach (var r in g.GetComponentsInChildren<Renderer>(true)) r.enabled = show;
        }
    }
}
