using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Sizes for placing item visuals without fixed numbers: an element's footprint (its mesh, in its own axes) and the
    /// size of an item's visual. Used by the element views that lay items out (queue, combiner).
    /// </summary>
    public static class ItemVisuals
    {
        /// <summary>
        /// The element's footprint (x = width, y = height, z = depth; in its own axes for a single mesh) and the centre of
        /// its top — the element's own mesh, else its largest horizontal part.
        /// </summary>
        public static void Footprint(Component element, out Vector3 size, out Vector3 topCentre)
        {
            var t = element.transform;
            var mf = element.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Bounds lb = mf.sharedMesh.bounds;
                size = Vector3.Scale(lb.size, t.lossyScale);
                topCentre = t.TransformPoint(new Vector3(lb.center.x, lb.max.y, lb.center.z));
                return;
            }
            // a model of several parts: the work surface is the top of its largest horizontal part (a table top, a pad),
            // not the highest point of the model (a column, a lamp)
            Renderer surface = null;
            float bestArea = -1f;
            foreach (var r in element.GetComponentsInChildren<Renderer>())
            {
                if (r.GetComponentInParent<SElement>() != element as SElement) continue;   // not a nested element's
                float area = r.bounds.size.x * r.bounds.size.z;
                if (area > bestArea) { bestArea = area; surface = r; }
            }
            if (surface != null)
            {
                Bounds sb = surface.bounds;
                size = sb.size;
                topCentre = new Vector3(sb.center.x, sb.max.y, sb.center.z);
                return;
            }
            size = Vector3.zero;
            topCentre = t.position;
        }

        /// <summary>
        /// Height of an item's pivot above the bottom of its visual: placing the pivot this high
        /// over a point puts the item's bottom on it, whatever the model's pivot — half the height for a centred pivot (the
        /// layouts' former assumption, so those items lie as before), 0 for a model with its pivot at the base.
        /// </summary>
        public static float PivotHeight(GameObject item)
        {
            // a person's pivot is at its feet: its bounds change with the pose it plays (sitting, lying), so measuring
            // them would lift it a little differently every frame
            if (item != null && item.GetComponent<PersonAnimator>() != null) return 0f;
            if (!Bounds(item, out Bounds b)) return 0f;
            return item.transform.position.y - b.min.y;
        }

        /// <summary>Height of the top of an item's visual above its pivot (where things rest on it).</summary>
        public static float TopAbovePivot(GameObject item)
        {
            if (!Bounds(item, out Bounds b)) return 0f;
            return b.max.y - item.transform.position.y;
        }

        /// <summary>
        /// Where a container item's load rests: its <c>LoadSocket</c> node (the library's pallet, tote and roll cage
        /// mark their deck / inside floor with one), or null. Breadth-first, so the container's own socket wins over the
        /// sockets of items already attached to it.
        /// </summary>
        public static Transform LoadSocket(GameObject item)
        {
            if (item == null) return null;
            var queue = new System.Collections.Generic.Queue<Transform>();
            queue.Enqueue(item.transform);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (t != item.transform && t.name == "LoadSocket") return t;
                foreach (Transform c in t) queue.Enqueue(c);
            }
            return null;
        }

        /// <summary>
        /// Gives <paramref name="item"/> a new look: an instance of <paramref name="prefab"/> where
        /// its visual is, which becomes the item's <c>vItem</c>; the former visual is destroyed. Views only.
        /// </summary>
        public static GameObject ReplaceLook(Item item, GameObject prefab, string name)
        {
            if (item == null || prefab == null) return item?.vItem as GameObject;
            var old = item.vItem as GameObject;
            var at = old != null ? old.transform : null;
            var look = Object.Instantiate(prefab, at != null ? at.position : Vector3.zero, at != null ? at.rotation : Quaternion.identity);
            look.SetActive(true);
            look.name = name;
            if (old != null)
            {
                if (old.transform.parent != null) look.transform.SetParent(old.transform.parent, true);
                if (Application.isPlaying) Object.Destroy(old); else Object.DestroyImmediate(old);
            }
            item.vItem = look;
            return look;
        }

        private static bool Bounds(GameObject item, out Bounds b)
        {
            b = default;
            if (item == null) return false;
            var rends = item.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return false;
            b = rends[0].bounds;
            for (int k = 1; k < rends.Length; k++) b.Encapsulate(rends[k].bounds);
            return true;
        }

        /// <summary>The size of an item's visual (its renderers' bounds), zero when it has none.</summary>
        public static Vector3 SizeOf(GameObject item)
        {
            if (item == null) return Vector3.zero;
            var rends = item.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return Vector3.zero;
            Bounds b = rends[0].bounds;
            for (int k = 1; k < rends.Length; k++) b.Encapsulate(rends[k].bounds);
            return b.size;
        }
    }
}
