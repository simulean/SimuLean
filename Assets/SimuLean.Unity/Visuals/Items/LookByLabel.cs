using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// An item's look chosen by one of its labels (look by stage): a label value → prefab map, so one station's
    /// output look turns a plate kit into a panel and a panel into a sub-block, or a kind label picks a model. The chosen
    /// prefab is built as the child <c>Look</c>; its own item views (a parametric block look, a label) are bound too.
    /// No match = <see cref="fallback"/> (none = nothing shown). Bound to its item by <see cref="ItemViews"/>. View only.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Look By Label")]
    public class LookByLabel : MonoBehaviour, IItemView
    {
        [Serializable]
        public class Entry
        {
            public string value = "";
            public GameObject prefab;
        }

        [Tooltip("The label whose value chooses the look (#id / #name allowed).")]
        public string label = "kind";
        public List<Entry> looks = new List<Entry>();
        public GameObject fallback;

        private GameObject look;
        private GameObject lookPrefab;

        public GameObject Look => look;

        public void Bind(Item item)
        {
            string v = ItemViews.LabelText(item, label) ?? "";
            GameObject prefab = fallback;
            foreach (var e in looks)
                if (e != null && e.prefab != null && string.Equals(e.value, v, StringComparison.OrdinalIgnoreCase))
                {
                    prefab = e.prefab;
                    break;
                }
            if (prefab != lookPrefab)
            {
                if (look != null)
                {
                    if (Application.isPlaying) { look.SetActive(false); Destroy(look); }
                    else DestroyImmediate(look);
                }
                look = null;
                lookPrefab = prefab;
                if (prefab != null)
                {
                    look = Instantiate(prefab, transform, false);
                    look.name = "Look";
                    look.transform.localPosition = Vector3.zero;
                    look.transform.localRotation = Quaternion.identity;
                }
            }
            if (look != null) ItemViews.Bind(item, look);
        }
    }
}
