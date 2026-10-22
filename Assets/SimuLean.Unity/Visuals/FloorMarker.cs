using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A translucent slab on the floor with a darker border and an optional caption at its front edge: the
    /// one drawing of the <see cref="StationModel"/>'s virtual look and the <see cref="UnityFloorZone"/>. Sized in metres
    /// whatever the parent's scale; every object it makes is <c>DontSave</c> (rebuilt from the owner's fields).
    /// </summary>
    public static class FloorMarker
    {
        private static Material material;

        /// <summary>Builds the marker as a child <paramref name="name"/> of <paramref name="parent"/>; null when no shader.</summary>
        public static GameObject Build(Transform parent, string name, Vector2 size, Color fill, string caption,
                                       float letterHeight, FontStyle style)
        {
            if (!EnsureMaterial()) return null;
            var root = new GameObject(name) { hideFlags = HideFlags.DontSave };
            root.transform.SetParent(parent, false);
            // always lying on the floor, turned with the parent's heading only: an element tilted about X / Z (a Z-up
            // model's rotation left on the element) would stand it on edge, and the items on its top edge
            root.transform.rotation = Quaternion.Euler(0f, parent.eulerAngles.y, 0f);
            Vector3 s = parent.lossyScale;
            float ix = 1f / Mathf.Max(1e-4f, Mathf.Abs(s.x)), iy = 1f / Mathf.Max(1e-4f, Mathf.Abs(s.y)), iz = 1f / Mathf.Max(1e-4f, Mathf.Abs(s.z));
            float sx = Mathf.Max(0.1f, size.x) * ix, sz = Mathf.Max(0.1f, size.y) * iz, bx = 0.05f * ix, bz = 0.05f * iz;   // 5 cm border
            Color edge = new Color(fill.r * 0.85f, fill.g * 0.85f, fill.b * 0.85f, Mathf.Clamp01(fill.a * 3.5f));
            Slab(root.transform, "Fill", Vector3.zero, new Vector3(sx, 0.006f * iy, sz), fill);
            Slab(root.transform, "Edge_N", new Vector3(0, 0, (sz - bz) * 0.5f), new Vector3(sx, 0.009f * iy, bz), edge);
            Slab(root.transform, "Edge_S", new Vector3(0, 0, -(sz - bz) * 0.5f), new Vector3(sx, 0.009f * iy, bz), edge);
            Slab(root.transform, "Edge_E", new Vector3((sx - bx) * 0.5f, 0, 0), new Vector3(bx, 0.009f * iy, sz), edge);
            Slab(root.transform, "Edge_W", new Vector3(-(sx - bx) * 0.5f, 0, 0), new Vector3(bx, 0.009f * iy, sz), edge);
            if (string.IsNullOrEmpty(caption)) return root;
            var go = new GameObject("Caption") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(0, 0.014f * iy, -sz * 0.5f + bz * 2f);
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var tm = go.AddComponent<TextMesh>();
            var font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            tm.text = caption;
            tm.fontSize = 64;
            tm.characterSize = letterHeight / 6.4f * ix;   // ~ the letter height in metres at 64 px
            tm.anchor = TextAnchor.LowerCenter;
            tm.fontStyle = style;
            tm.color = new Color(edge.r * 0.6f, edge.g * 0.6f, edge.b * 0.6f, 1f);
            return root;
        }

        /// <summary>Removes the children called <paramref name="name"/> (by iteration: no re-parenting, allowed while the
        /// owner is being deactivated).</summary>
        public static void Remove(Transform parent, string name)
        {
            var old = new System.Collections.Generic.List<GameObject>();
            foreach (Transform child in parent) if (child.name == name) old.Add(child.gameObject);
            foreach (var g in old) if (Application.isPlaying) Object.Destroy(g); else Object.DestroyImmediate(g);
        }

        private static bool EnsureMaterial()
        {
            if (material != null) return true;
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) return false;
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        /// <summary>A floor chevron (two flat bars in a V) at <paramref name="at"/> (local to <paramref name="parent"/>, metres)
        /// pointing along <paramref name="direction"/>; <c>DontSave</c>. The pathway arrows draw with it.</summary>
        public static void Chevron(Transform parent, Vector3 at, Vector3 direction, float size, Color color)
        {
            if (!EnsureMaterial()) return;
            direction.y = 0;
            if (direction.sqrMagnitude < 1e-6f) return;
            var root = new GameObject("Chevron") { hideFlags = HideFlags.DontSave };
            root.transform.SetParent(parent, false);
            root.transform.localPosition = at;
            root.transform.localRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            float arm = size * 0.62f, w = size * 0.16f;
            for (int side = -1; side <= 1; side += 2)
            {
                var holder = new GameObject("Arm") { hideFlags = HideFlags.DontSave };
                holder.transform.SetParent(root.transform, false);
                holder.transform.localRotation = Quaternion.Euler(0f, side * 135f, 0f);   // the two arms point back from the tip
                Slab(holder.transform, "Bar", new Vector3(0f, 0f, arm * 0.5f), new Vector3(w, 0.008f, arm), color);
            }
        }

        private static void Slab(Transform parent, string name, Vector3 at, Vector3 scale, Color c)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.hideFlags = HideFlags.DontSave;
            var col = go.GetComponent<Collider>();
            if (Application.isPlaying) Object.Destroy(col); else Object.DestroyImmediate(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at + Vector3.up * scale.y * 0.5f;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", c);
            r.SetPropertyBlock(block);
        }
    }
}
