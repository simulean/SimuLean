using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>A view on an item's visual that shows the item's data (its labels): bound to the engine
    /// <see cref="Item"/> each time the item gets a visual (<see cref="Item.VisualAttached"/>). View only.</summary>
    public interface IItemView
    {
        void Bind(Item item);
    }

    /// <summary>
    /// Binds the item views (<see cref="IItemView"/>: <see cref="ItemLabelView"/>, <see cref="ParametricBlockLook"/>,
    /// <see cref="LookByLabel"/>…) of an item's visual to the item when the visual is assigned — by a source creating
    /// it, a station's output look, a combiner's new item, an erection dock's ship. Installed by the scene's
    /// clock; headless runs have no visuals and are not affected.
    /// </summary>
    public static class ItemViews
    {
        private static bool installed;

        public static void Install()
        {
            if (installed) return;
            Item.VisualAttached += OnVisual;
            installed = true;
        }

        private static void OnVisual(Item item)
        {
            if (item.vItem is GameObject go && go != null) Bind(item, go);
        }

        /// <summary>Binds every item view on <paramref name="go"/> (and its children) to <paramref name="item"/>.</summary>
        public static void Bind(Item item, GameObject go)
        {
            // a view may replace a look and so destroy the views under it (a look by label): skip those
            foreach (var v in go.GetComponentsInChildren<IItemView>(true))
                if (v is Object o && o != null) v.Bind(item);
        }

        /// <summary>A label of the item as text: the label's value, <c>#id</c> the item's id, <c>#name</c> its name.</summary>
        public static string LabelText(Item item, string label)
        {
            if (item == null || string.IsNullOrEmpty(label)) return null;
            if (label == "#id") return item.GetId().ToString();
            if (label == "#name") return item.Name;
            return item.GetLabelText(label);
        }

        /// <summary>A numeric label, else <paramref name="fallback"/>.</summary>
        public static float Number(Item item, string label, float fallback)
        {
            var v = item?.GetLabelValue(label);
            return v.HasValue && v.Value > 0 ? (float)v.Value : fallback;
        }
    }
}
