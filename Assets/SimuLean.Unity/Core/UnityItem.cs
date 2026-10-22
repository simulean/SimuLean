using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    // Core types (Item, ...) resolve from the enclosing SimuLean namespace.
    public class UnityItem : MonoBehaviour
    {
        [Header("Modelo")]
        public Item item;  // the engine item (set from code; shown for debugging)

        [Header("Etiquetas (Labels)")]
        [SerializeField]
        private List<LabelEntry> labels = new List<LabelEntry>();

        [System.Serializable]
        public class LabelEntry
        {
            public string key;
            public string value;
        }

        void OnValidate()
        {
            if (item != null)
            {
                UpdateLabelsFromItem();
            }
        }

        [ContextMenu("Actualizar etiquetas")]
        public void UpdateLabelsFromItem()
        {
            labels.Clear();
            foreach (string key in item.Properties.Names)
                labels.Add(new LabelEntry { key = key, value = item.Properties.GetText(key) });
        }
    }
}
