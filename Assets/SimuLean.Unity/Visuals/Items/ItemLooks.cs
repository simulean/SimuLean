using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Shows an item's <see cref="ItemLook"/> (a reaction's <c>SetItemLook</c>) on its visual: the colour tints
    /// its renderers through a <see cref="MaterialPropertyBlock"/> (<c>_BaseColor</c> / <c>_Color</c>; the materials stay
    /// shared), the scale multiplies the visual's own scale. Applied when the look changes (<see cref="Item.LookChanged"/>)
    /// and again on a new visual (<see cref="Item.VisualAttached"/>), both through <see cref="ItemViews"/>. Read-only: it
    /// reads the item and touches only its visual; the visuals of the items it carries (a combiner's parts, a pallet's load)
    /// and label texts keep their look.
    /// </summary>
    public static class ItemLooks
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColor = Shader.PropertyToID("_Color");
        private static MaterialPropertyBlock block;

        // a visual's own scale, before any look scaled it (weak: a destroyed visual leaves nothing behind)
        private sealed class OwnScale { public Vector3 Value; }
        private static readonly ConditionalWeakTable<GameObject, OwnScale> ownScales = new ConditionalWeakTable<GameObject, OwnScale>();

        /// <summary>Applies the item's look to its visual (nothing when it has none).</summary>
        public static void Apply(Item item)
        {
            if (item?.Look != null && item.vItem is GameObject go && go != null) Apply(item, go);
        }

        /// <summary>Applies <paramref name="item"/>'s look to <paramref name="go"/>.</summary>
        public static void Apply(Item item, GameObject go)
        {
            var look = item?.Look;
            if (look == null || go == null) return;
            if (look.Color != null) Tint(item, go, look.Color.Value);
            if (!double.IsNaN(look.Scale))
            {
                var own = ownScales.GetValue(go, g => new OwnScale { Value = g.transform.localScale });
                go.transform.localScale = own.Value * (float)look.Scale;
            }
        }

        /// <summary>The colour shown on a renderer (its property block's <c>_BaseColor</c>), or null when none is set.</summary>
        public static Color? TintOf(Renderer r)
        {
            if (r == null || !r.HasPropertyBlock()) return null;
            if (block == null) block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            return block.isEmpty ? (Color?)null : block.GetColor(BaseColor);
        }

        private static void Tint(Item item, GameObject go, LookColor c)
        {
            var color = new Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
            var carried = new HashSet<Transform>();
            Carried(item, carried, 0);
            if (block == null) block = new MaterialPropertyBlock();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r.GetComponent<TextMesh>() != null || UnderCarried(r.transform, go.transform, carried)) continue;
                r.GetPropertyBlock(block);
                block.SetColor(BaseColor, color);
                block.SetColor(LegacyColor, color);
                r.SetPropertyBlock(block);
            }
        }

        // the visuals of the items it carries (its sub-items, at any depth)
        private static void Carried(Item item, HashSet<Transform> into, int depth)
        {
            if (item?.subItems == null || depth > 16) return;
            foreach (var sub in item.subItems)
            {
                if (sub?.vItem is GameObject g && g != null) into.Add(g.transform);
                Carried(sub, into, depth + 1);
            }
        }

        private static bool UnderCarried(Transform t, Transform root, HashSet<Transform> carried)
        {
            if (carried.Count == 0) return false;
            for (; t != null && t != root; t = t.parent)
                if (carried.Contains(t)) return true;
            return false;
        }
    }
}
