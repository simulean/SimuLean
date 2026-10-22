using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Renders an object's SimuLean inspectors (its <see cref="SElement"/> and tool components, as the Inspector draws
    /// them) in an off-screen window and saves them as a PNG — interface reviews and documentation through the MCP,
    /// where the docked Inspector cannot be captured. Editor only; changes nothing.
    /// </summary>
    public sealed class InspectorCapture : EditorWindow
    {
        private UnityEditor.Editor[] editors = new UnityEditor.Editor[0];
        private string title;

        /// <summary>Saves the inspectors of <paramref name="go"/> to <paramref name="path"/> (PNG); returns its size.</summary>
        public static string Capture(GameObject go, string path, float width = 460, float height = 1600)
        {
            var w = CreateInstance<InspectorCapture>();
            try
            {
                var components = go.GetComponents<Component>()
                    .Where(c => c != null && !(c is Transform) && (c.hideFlags & HideFlags.HideInInspector) == 0
                                && c.GetType().Namespace != null && c.GetType().Namespace.StartsWith("SimuLean"))
                    .ToArray();
                w.title = go.name;
                w.editors = components.Select(c => UnityEditor.Editor.CreateEditor(c)).ToArray();
                w.ShowUtility();
                w.position = new Rect(40, 40, width, height);
                return Grab(w, path);
            }
            finally
            {
                foreach (var e in w.editors) if (e != null) DestroyImmediate(e);
                w.Close();
            }
        }

        private void OnGUI()
        {
            EditorGUIUtility.wideMode = true;
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            foreach (var e in editors)
            {
                if (e == null) continue;
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(ObjectNames.NicifyVariableName(e.target.GetType().Name), EditorStyles.miniBoldLabel);
                e.OnInspectorGUI();
            }
        }

        private static string Grab(EditorWindow w, string path)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var parent = typeof(EditorWindow).GetField("m_Parent", any).GetValue(w);
            var repaint = parent.GetType().GetMethod("RepaintImmediately", any);
            repaint?.Invoke(parent, null);
            repaint?.Invoke(parent, null);   // the second pass has the layout of the first
            var rect = (Rect)parent.GetType().GetProperty("position").GetValue(parent);
            float ppp = EditorGUIUtility.pixelsPerPoint;
            int width = (int)(rect.width * ppp), height = (int)(rect.height * ppp);
            var rt = new RenderTexture(width, height, 0);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                parent.GetType().GetMethod("GrabPixels", any).Invoke(parent, new object[] { rt, new Rect(0, 0, width, height) });
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                var px = tex.GetPixels32();
                var flipped = new Color32[px.Length];
                for (int y = 0; y < height; y++) System.Array.Copy(px, y * width, flipped, (height - 1 - y) * width, width);
                tex.SetPixels32(flipped);
                tex.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllBytes(path, tex.EncodeToPNG());
                return width + "x" + height;
            }
            finally
            {
                RenderTexture.active = previous;
                rt.Release();
                DestroyImmediate(rt);
                DestroyImmediate(tex);
            }
        }
    }
}
