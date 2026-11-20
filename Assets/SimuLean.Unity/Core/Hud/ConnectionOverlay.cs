using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Immediate-mode drawing of the model's connections and of the markers of objects without a mesh (nodes, lists,
    /// downtimes…) over a rendered picture: camera-facing bands with an arrow head and flat discs, drawn with
    /// <see cref="GL"/> after the scene (no depth test, so they stay visible over the models). Shared by the HUD's
    /// connection view (<see cref="SimRuntimeHud"/>) and the editor's <c>simulean_capture</c>. Read-only: draws, changes
    /// nothing.
    /// </summary>
    public static class ConnectionOverlay
    {
        /// <summary>Band width per metre of distance to the eye (about 5 px at 720 px and a 40° field of view).</summary>
        public const float WidthPerMetre = 0.0025f;

        private static Material material;

        /// <summary>The unlit, alpha-blended material of the overlay (no depth test, no culling); call <c>SetPass(0)</c>
        /// before <see cref="GL.Begin"/>.</summary>
        public static Material Material
        {
            get
            {
                if (material != null) return material;
                material = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                material.SetInt("_ZWrite", 0);
                material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                return material;
            }
        }

        /// <summary>A band from <paramref name="a"/> to <paramref name="b"/> facing <paramref name="eye"/> (width proportional
        /// to its distance) and, when <paramref name="arrow"/>, an arrow head at 60 % of its length. Inside
        /// <c>GL.Begin(GL.QUADS)</c>.</summary>
        public static void Ribbon(Vector3 eye, Vector3 a, Vector3 b, Color col, bool arrow)
        {
            Vector3 dir = b - a;
            if (dir.sqrMagnitude < 1e-6f) return;
            Vector3 mid = (a + b) * 0.5f;
            float w = Vector3.Distance(eye, mid) * WidthPerMetre;
            Vector3 side = Vector3.Cross(dir, eye - mid).normalized * w;
            GL.Color(col);
            GL.Vertex(a - side); GL.Vertex(a + side); GL.Vertex(b + side); GL.Vertex(b - side);
            if (!arrow) return;
            Vector3 tip = Vector3.Lerp(a, b, 0.6f), f = dir.normalized * w * 8f;
            GL.Vertex(tip + f); GL.Vertex(tip - f * 0.2f + side * 4f); GL.Vertex(tip - f * 0.2f); GL.Vertex(tip - f * 0.2f - side * 4f);
        }

        /// <summary>A disc at <paramref name="p"/> facing <paramref name="eye"/>, its radius proportional to its distance, with
        /// a dark rim. Inside <c>GL.Begin(GL.QUADS)</c>.</summary>
        public static void Disc(Vector3 eye, Vector3 p, Color col, float radiusPerMetre = 0.012f)
        {
            Vector3 toEye = eye - p;
            float r = toEye.magnitude * radiusPerMetre;
            if (r <= 0f) return;
            Vector3 n = toEye.normalized;
            Vector3 u = Vector3.Cross(n, Mathf.Abs(n.y) > 0.99f ? Vector3.forward : Vector3.up).normalized, v = Vector3.Cross(n, u);
            Fan(p, u, v, r * 1.25f, new Color(0f, 0f, 0f, 0.8f));
            Fan(p, u, v, r, col);
        }

        // a filled circle as degenerate quads (centre, two rim points, the second repeated)
        private static void Fan(Vector3 p, Vector3 u, Vector3 v, float r, Color col)
        {
            const int Segments = 16;
            GL.Color(col);
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / Segments, a1 = (i + 1) * Mathf.PI * 2f / Segments;
                Vector3 p0 = p + (u * Mathf.Cos(a0) + v * Mathf.Sin(a0)) * r, p1 = p + (u * Mathf.Cos(a1) + v * Mathf.Sin(a1)) * r;
                GL.Vertex(p); GL.Vertex(p0); GL.Vertex(p1); GL.Vertex(p1);
            }
        }
    }
}
