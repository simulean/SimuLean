using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A component of an item's view told which item it shows when a source creates the view:
    /// <see cref="UnitySource"/> calls it with the item's id right after instantiating its item prefab. Views only.
    /// </summary>
    public interface IItemViewInit
    {
        void OnItemCreated(int itemId);
    }

    /// <summary>
    /// Look variety for item views, e.g. car bodies in several paint colours: the renderers below this
    /// object that use any of <see cref="materials"/> get one of them, chosen by a hash of the item id (weighted by
    /// <see cref="weights"/>), so a run always shows the same item in the same colour. Shared materials only (they keep
    /// GPU instancing / batching); other materials are untouched. Read-only for the model.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Material Variants")]
    public class MaterialVariants : MonoBehaviour, IItemViewInit
    {
        [Tooltip("The interchangeable materials (e.g. paint colours of the same texture layout).")]
        public List<Material> materials = new List<Material>();
        [Tooltip("Relative weight of each material (empty / missing = 1).")]
        public List<float> weights = new List<float>();
        [Tooltip("Changes the choice for every id.")]
        public int seed = 0;

        /// <summary>The variant of <paramref name="key"/> (deterministic: a hash of key and seed, weighted).</summary>
        public int Pick(int key)
        {
            int n = materials != null ? materials.Count : 0;
            if (n == 0) return -1;
            unchecked
            {
                uint h = (uint)key * 2654435761u ^ (uint)seed * 40503u;
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
                float total = 0f;
                for (int i = 0; i < n; i++) total += W(i);
                float u = (h & 0xffffff) / (float)0x1000000 * total;
                for (int i = 0; i < n; i++) { u -= W(i); if (u < 0f) return i; }
                return n - 1;
            }
        }

        private float W(int i) => weights != null && i < weights.Count ? Mathf.Max(0f, weights[i]) : 1f;

        /// <summary>Puts material <paramref name="index"/> on every renderer slot below this object that holds one of the variants.</summary>
        public void Apply(int index)
        {
            if (materials == null || index < 0 || index >= materials.Count || materials[index] == null) return;
            var target = materials[index];
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && mats[i] != target && materials.Contains(mats[i])) { mats[i] = target; changed = true; }
                if (changed) r.sharedMaterials = mats;
            }
        }

        public void OnItemCreated(int itemId) => Apply(Pick(itemId));
    }
}
