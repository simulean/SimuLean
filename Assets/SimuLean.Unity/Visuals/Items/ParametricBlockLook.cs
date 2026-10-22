using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The look of a ship block, sub-block or panel built from the item's labels: the model of
    /// its <c>kind</c> (flat / curved… — <see cref="variants"/>, the first is the default) scaled to its
    /// <c>length</c> × <c>breadth</c> × <c>depth</c> (m, within <see cref="minScale"/>…<see cref="maxScale"/> of the
    /// model's own size), so one item prefab fits every block of the breakdown. A block whose own labels lack them takes
    /// its sub-blocks' (the largest of each). The model is built as the child <c>Look</c> (length along the item's
    /// local X, breadth along Z, the base on the pivot). Bound to its item by <see cref="ItemViews"/>. View only.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Parametric Block Look")]
    public class ParametricBlockLook : MonoBehaviour, IItemView
    {
        [Serializable]
        public class Variant
        {
            [Tooltip("The kind labels this model stands for, comma separated (e.g. flat, panel, double bottom).")]
            public string kinds = "";
            public GameObject model;
        }

        public List<Variant> variants = new List<Variant>();
        public string kindLabel = "kind", lengthLabel = "length", breadthLabel = "breadth", depthLabel = "depth";
        [Tooltip("Size when the item has no dimensions (length, depth, breadth in m).")]
        public Vector3 defaultSize = new Vector3(12f, 3f, 16f);
        [Min(0.01f)] public float minScale = 0.2f;
        [Min(0.01f)] public float maxScale = 5f;

        private GameObject look;
        private GameObject lookModel;

        /// <summary>The look built (null before the first bind).</summary>
        public GameObject Look => look;

        public void Bind(Item item)
        {
            var variant = Pick(KindOf(item));
            if (variant?.model == null) return;
            Vector3 size = SizeOf(item);
            if (look == null || lookModel != variant.model)
            {
                if (look != null) Remove(look);
                look = Instantiate(variant.model, transform, false);
                look.name = "Look";
                look.transform.localPosition = Vector3.zero;
                look.transform.localRotation = Quaternion.identity;
                lookModel = variant.model;
            }
            look.transform.localScale = Vector3.one;
            Vector3 nominal = LocalSize(look);
            look.transform.localScale = new Vector3(Fit(size.x, nominal.x), Fit(size.y, nominal.y), Fit(size.z, nominal.z));
        }

        private float Fit(float want, float have) => have > 1e-4f ? Mathf.Clamp(want / have, minScale, maxScale) : 1f;

        private string KindOf(Item item)
        {
            string k = item?.GetLabelText(kindLabel);
            if (string.IsNullOrEmpty(k) && item?.GetSubItems() != null)
                foreach (var s in item.GetSubItems())
                {
                    k = s.GetLabelText(kindLabel);
                    if (!string.IsNullOrEmpty(k)) break;
                }
            return k ?? "";
        }

        private Vector3 SizeOf(Item item)
        {
            float l = ItemViews.Number(item, lengthLabel, 0), d = ItemViews.Number(item, depthLabel, 0), b = ItemViews.Number(item, breadthLabel, 0);
            if ((l <= 0 || d <= 0 || b <= 0) && item?.GetSubItems() != null)
                foreach (var s in item.GetSubItems())
                {
                    l = Mathf.Max(l, ItemViews.Number(s, lengthLabel, 0));
                    d = Mathf.Max(d, ItemViews.Number(s, depthLabel, 0));
                    b = Mathf.Max(b, ItemViews.Number(s, breadthLabel, 0));
                }
            return new Vector3(l > 0 ? l : defaultSize.x, d > 0 ? d : defaultSize.y, b > 0 ? b : defaultSize.z);
        }

        private Variant Pick(string kind)
        {
            foreach (var v in variants)
                foreach (var k in (v.kinds ?? "").Split(','))
                    if (k.Trim().Length > 0 && string.Equals(k.Trim(), kind, StringComparison.OrdinalIgnoreCase)) return v;
            return variants.Count > 0 ? variants[0] : null;
        }

        // the model's size in this item's frame (its meshes' bounds, at scale 1)
        private Vector3 LocalSize(GameObject go)
        {
            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                var m = transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var c = m.MultiplyPoint3x4(new Vector3(i % 2 == 0 ? b.min.x : b.max.x, (i / 2) % 2 == 0 ? b.min.y : b.max.y,
                                                           i < 4 ? b.min.z : b.max.z));
                    if (!any) { min = max = c; any = true; }
                    else { min = Vector3.Min(min, c); max = Vector3.Max(max, c); }
                }
            }
            return any ? max - min : Vector3.one;
        }

        private static void Remove(GameObject go)
        {
            if (Application.isPlaying) { go.SetActive(false); Destroy(go); }
            else DestroyImmediate(go);
        }
    }
}
