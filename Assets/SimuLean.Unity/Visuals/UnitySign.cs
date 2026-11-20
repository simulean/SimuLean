using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>How a <see cref="UnitySign"/> is held.</summary>
    public enum SignMount
    {
        /// <summary>Hangs on two rods from a ceiling-height bracket (<see cref="UnitySign.mountHeight"/>).</summary>
        Hanging,
        /// <summary>Stands on a floor post.</summary>
        Post,
        /// <summary>Flat on a wall face (local −Z = out of the wall, towards the reader).</summary>
        Wall,
    }

    /// <summary>Main labels (areas, stations) and smaller secondary ones (sub-areas, points).</summary>
    public enum SignImportance { Primary, Secondary }

    /// <summary>
    /// A readable sign board for a model scene: a coloured board with the text on both faces (the back one
    /// not mirrored), held by a ceiling bracket with rods, a floor post or flat on a wall. The board leans back by
    /// <see cref="tilt"/> so views from above read it. <see cref="Build"/> (re)generates the children from the fields; the
    /// size of the board follows the text. <see cref="SignLayout"/> moves a set of signs so that none intersects the scene's
    /// geometry or another sign. View only (no model element).
    /// <para>Frame: the sign's position is the centre of the board; local −Z is the front face (its text is read from
    /// −Z, looking along +Z), +Y up.</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Sign")]
    [SimuLeanTool(SimuLeanToolCategories.Visualization, "Sign", Description = "Readable label board: hanging, on a post or on a wall; layout pass avoids geometry and other signs", Order = 40)]
    public class UnitySign : MonoBehaviour
    {
        [TextArea] public string text = "Sign";
        [Tooltip("Widest board (m): longer text is wrapped at its spaces into lines (0 = one line).")]
        [Min(0f)] public float maxWidth = 0f;
        public SignMount mount = SignMount.Hanging;
        public SignImportance importance = SignImportance.Primary;
        [Tooltip("Height of a text line (m); Secondary signs use 60 % of it.")]
        [Min(0.02f)] public float textHeight = 0.35f;
        [Tooltip("Hanging: height of the bracket the rods hang from (the ceiling / a beam).")]
        public float mountHeight = 6f;
        [Tooltip("Lean of the board (degrees, top away from the reader) so views from above read it; 0 for wall signs.")]
        [Range(0f, 45f)] public float tilt = 20f;
        [Tooltip("Text on the back face too (free-standing signs).")]
        public bool doubleSided = true;
        public Color boardColor = new Color(1f, 0.85f, 0.2f);
        public Color textColor = new Color(0.1f, 0.12f, 0.16f);
        [Tooltip("Optional shared materials (empty = made in code).")]
        public Material boardMaterial, mountMaterial;
        [Tooltip("Optional text material (empty = the depth-tested sign text material, shader SimuLean/Sign Text).")]
        public Material textMaterial;

        private const string GeneratedName = "SignParts";
        private static Material defaultBoard, defaultMount, defaultText;
        private static Font defaultFont;

        /// <summary>The font of the signs (the built-in legacy runtime font).</summary>
        public static Font DefaultFont
        {
            get
            {
                if (defaultFont == null) defaultFont = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return defaultFont;
            }
        }

        /// <summary>A text material for <see cref="TextMesh"/> that is depth-tested and back-face culled (shader
        /// <c>SimuLean/Sign Text</c>; the built-in text shader draws text over everything); null without the shader.</summary>
        public static Material DefaultTextMaterial()
        {
            if (defaultText != null) return defaultText;
            var shader = Shader.Find("SimuLean/Sign Text");
            var font = DefaultFont;
            if (shader == null || font == null) return null;
            defaultText = new Material(shader) { name = "SignText", mainTexture = font.material != null ? font.material.mainTexture : null };
            return defaultText;
        }

        /// <summary>Gives a <see cref="TextMesh"/> the sign font and the depth-tested text material.</summary>
        public static void UseSignText(TextMesh tm, Material material = null)
        {
            if (DefaultFont != null) tm.font = DefaultFont;
            var m = material != null ? material : DefaultTextMaterial();
            var r = tm.GetComponent<MeshRenderer>();
            if (m != null && r != null) r.sharedMaterial = m;
        }

        /// <summary>The text height after the importance factor.</summary>
        public float EffectiveTextHeight => textHeight * (importance == SignImportance.Secondary ? 0.6f : 1f);

        /// <summary>Board size (width, height) for the text: from the measured text when available, else an estimate.</summary>
        /// <summary>The text as drawn: wrapped at spaces so the board stays within <see cref="maxWidth"/>.</summary>
        public string DisplayText
        {
            get
            {
                string t = text ?? "";
                if (maxWidth <= 0f || t.Contains("\n")) return t;
                float h = EffectiveTextHeight;
                int maxChars = Mathf.Max(4, Mathf.FloorToInt((maxWidth - h * 0.8f) / (h * 0.56f)));
                if (t.Length <= maxChars) return t;
                var lines = new List<string>();
                string line = "";
                foreach (var w in t.Split(' '))
                {
                    if (line.Length > 0 && line.Length + 1 + w.Length > maxChars) { lines.Add(line); line = w; }
                    else line = line.Length == 0 ? w : line + " " + w;
                }
                if (line.Length > 0) lines.Add(line);
                return string.Join("\n", lines);
            }
        }

        public Vector2 BoardSize
        {
            get
            {
                float h = EffectiveTextHeight;
                string[] lines = DisplayText.Split('\n');
                int longest = 1;
                foreach (var l in lines) longest = Mathf.Max(longest, l.Length);
                return new Vector2(Mathf.Max(h * 2f, longest * h * 0.56f + h * 0.8f), lines.Length * h * 1.15f + h * 0.6f);
            }
        }

        /// <summary>World bounds of the board (with the tilt), for layout checks.</summary>
        public Bounds BoardBounds() => BoundsAt(transform.position, transform.rotation);

        /// <summary>World bounds the board would have at <paramref name="position"/> / <paramref name="rotation"/>.</summary>
        public Bounds BoundsAt(Vector3 position, Quaternion rotation)
        {
            Vector2 s = BoardSize;
            var b = new Bounds(position, Vector3.zero);
            Quaternion r = rotation * Quaternion.Euler(tilt, 0f, 0f);
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) == 0 ? -s.x / 2 : s.x / 2, (i & 2) == 0 ? -s.y / 2 : s.y / 2, (i & 4) == 0 ? -0.03f : 0.03f);
                b.Encapsulate(position + r * c);
            }
            return b;
        }

        /// <summary>World bounds of the holder (rods / post), for layout checks; empty bounds for a wall sign.</summary>
        public Bounds MountBounds() => MountBoundsAt(transform.position, transform.rotation);

        public Bounds MountBoundsAt(Vector3 position, Quaternion rotation)
        {
            Vector2 s = BoardSize;
            switch (mount)
            {
                case SignMount.Hanging:
                {
                    float top = Mathf.Max(position.y + s.y / 2f, mountHeight);
                    var b = new Bounds(new Vector3(position.x, (position.y + s.y / 2f + top) / 2f, position.z), Vector3.zero);
                    Vector3 half = rotation * new Vector3(s.x * 0.4f, 0f, 0f);
                    b.Encapsulate(new Vector3(position.x, top, position.z) + half);
                    b.Encapsulate(new Vector3(position.x, top, position.z) - half);
                    b.Encapsulate(position + half + Vector3.up * (s.y / 2f));
                    b.Encapsulate(position - half + Vector3.up * (s.y / 2f));
                    return b;
                }
                case SignMount.Post:
                    return new Bounds(new Vector3(position.x, (position.y - s.y / 2f) / 2f, position.z),
                                      new Vector3(0.08f, Mathf.Max(0f, position.y - s.y / 2f), 0.08f));
                default:
                    return new Bounds(position, Vector3.zero);
            }
        }

        [ContextMenu("Rebuild Sign")]
        public void Build()
        {
            var old = transform.Find(GeneratedName);
            if (old != null) { if (Application.isPlaying) Destroy(old.gameObject); else DestroyImmediate(old.gameObject); }
            var root = new GameObject(GeneratedName).transform;
            root.SetParent(transform, false);
            Vector2 s = BoardSize;
            float h = EffectiveTextHeight;

            var board = new GameObject("Board").transform;
            board.SetParent(root, false);
            board.localRotation = Quaternion.Euler(tilt, 0f, 0f);
            Box(board, "Panel", Vector3.zero, new Vector3(s.x, s.y, 0.03f), boardMaterial != null ? boardMaterial : DefaultBoard(boardColor));
            Text(board, "Text", new Vector3(0f, 0f, -0.017f), Quaternion.identity, h);
            if (doubleSided && mount != SignMount.Wall)
                Text(board, "TextBack", new Vector3(0f, 0f, 0.017f), Quaternion.Euler(0f, 180f, 0f), h);

            Material m = mountMaterial != null ? mountMaterial : DefaultMount();
            if (mount == SignMount.Hanging)
            {
                float top = Mathf.Max(transform.position.y + s.y / 2f + 0.05f, mountHeight);
                float len = top - (transform.position.y + s.y / 2f);
                foreach (float x in new[] { -s.x * 0.4f, s.x * 0.4f })
                    Box(root, "Rod", new Vector3(x, s.y / 2f + len / 2f, 0f), new Vector3(0.02f, len, 0.02f), m);
                Box(root, "Bracket", new Vector3(0f, top - transform.position.y, 0f), new Vector3(s.x * 0.9f, 0.05f, 0.05f), m);
            }
            else if (mount == SignMount.Post)
            {
                float len = Mathf.Max(0.05f, transform.position.y - s.y / 2f);
                Box(root, "Post", new Vector3(0f, -s.y / 2f - len / 2f, 0.04f), new Vector3(0.06f, len, 0.06f), m);
                Box(root, "Foot", new Vector3(0f, -transform.position.y + 0.01f, 0.04f), new Vector3(0.4f, 0.02f, 0.4f), m);
            }
        }

        private void Text(Transform parent, string name, Vector3 pos, Quaternion rot, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            var tm = go.AddComponent<TextMesh>();
            tm.text = DisplayText;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontSize = 64;
            tm.characterSize = height * 0.16f;
            tm.color = textColor;
            UseSignText(tm, textMaterial);
        }

        private static void Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = name;
            var col = g.GetComponent<Collider>();
            if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos;
            g.transform.localScale = size;
            g.GetComponent<Renderer>().sharedMaterial = mat;
        }

        // the project renders with the Universal Render Pipeline; a project without it keeps the built-in Standard
        private static Shader LitShader() => Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

        private static Material DefaultBoard(Color c)
        {
            if (defaultBoard == null) defaultBoard = new Material(LitShader()) { color = c, enableInstancing = true, name = "SignBoard" };
            return defaultBoard;
        }

        private static Material DefaultMount()
        {
            if (defaultMount == null) defaultMount = new Material(LitShader()) { color = new Color(0.3f, 0.31f, 0.33f), enableInstancing = true, name = "SignMount" };
            return defaultMount;
        }
    }

    /// <summary>
    /// Layout pass for <see cref="UnitySign"/>s: in order of importance, each sign is moved to the first
    /// candidate position — its own, then raised, then shifted sideways along its board and towards / away from its reader —
    /// where its board and holder clear every obstacle (world bounds of the scene's renderers, e.g. walls, racks, vehicles)
    /// and every sign placed before (with a margin, and never right above / below one in plan). A sign without a free
    /// candidate keeps its position and is reported. Wall signs move along their wall (sideways, up).
    /// </summary>
    public static class SignLayout
    {
        public struct Options
        {
            public float margin, signGap, maxRaise, raiseStep, maxShift, shiftStep;
            /// <summary>Camera positions (e.g. the scene's presets): when given, a sign prefers a place where at least one of
            /// them sees its board unobstructed, facing it, large enough (<see cref="minAngularSize"/> radians of text height).</summary>
            public IList<Vector3> viewpoints;
            public float minAngularSize;
            public static Options Default => new Options { margin = 0.08f, signGap = 0.3f, maxRaise = 3.5f, raiseStep = 0.25f, maxShift = 4f, shiftStep = 0.5f, minAngularSize = 0.006f };
        }

        /// <summary>Obstacle bounds of the renderers under <paramref name="roots"/>: everything except signs and flat floor
        /// pieces (height below <paramref name="minHeight"/>) and ground-sized objects.</summary>
        public static List<Bounds> CollectObstacles(IEnumerable<Transform> roots, float minHeight = 0.12f, float maxSize = 80f)
        {
            var list = new List<Bounds>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>())
                {
                    if (r.GetComponentInParent<UnitySign>() != null || r is ParticleSystemRenderer) continue;
                    if (r.GetComponent<TextMesh>() != null) continue;
                    Bounds b = r.bounds;
                    if (b.size.y < minHeight) continue;
                    if (b.size.x > maxSize && b.size.z > maxSize) continue;
                    list.Add(b);
                }
            }
            return list;
        }

        /// <summary>Signs of the last <see cref="Resolve"/> placed clear of everything but seen by none of the viewpoints.</summary>
        public static readonly List<UnitySign> unreadable = new List<UnitySign>();

        /// <summary>Places the signs (Primary first, then in list order); returns the ones that found no free position.</summary>
        public static List<UnitySign> Resolve(IList<UnitySign> signs, IList<Bounds> obstacles, Options? options = null)
        {
            var o = options ?? Options.Default;
            var placed = new List<UnitySign>();
            var failed = new List<UnitySign>();
            unreadable.Clear();
            var order = new List<UnitySign>(signs);
            order.RemoveAll(x => x == null);
            order.Sort((a, b) => a.importance.CompareTo(b.importance));
            foreach (var s in order)
            {
                Vector3 p0 = s.transform.position;
                Quaternion rot = s.transform.rotation;
                bool ok = false;
                Vector3? firstFree = null;
                foreach (var c in Candidates(s, p0, rot, o))
                {
                    if (!Free(s, c, rot, obstacles, placed, o)) continue;
                    if (firstFree == null) firstFree = c;
                    if (o.viewpoints != null && o.viewpoints.Count > 0 && !Readable(s, c, rot, obstacles, o)) continue;
                    s.transform.position = c;
                    ok = true;
                    break;
                }
                if (!ok && firstFree.HasValue) { s.transform.position = firstFree.Value; unreadable.Add(s); ok = true; }
                if (!ok) failed.Add(s);
                s.Build();
                placed.Add(s);
            }
            return failed;
        }

        private static IEnumerable<Vector3> Candidates(UnitySign s, Vector3 p0, Quaternion rot, Options o)
        {
            Vector3 right = rot * Vector3.right, fwd = rot * Vector3.forward;
            fwd.y = 0f; fwd.Normalize();
            float maxUp = s.mount == SignMount.Hanging ? Mathf.Max(0f, s.mountHeight - 0.3f - s.BoardSize.y / 2f - p0.y) : o.maxRaise;
            maxUp = Mathf.Min(maxUp, o.maxRaise);
            var shifts = new List<Vector3> { Vector3.zero };
            for (float d = o.shiftStep; d <= o.maxShift + 1e-3f; d += o.shiftStep)
            {
                shifts.Add(right * d); shifts.Add(-right * d);
                if (s.mount != SignMount.Wall) { shifts.Add(-fwd * d); shifts.Add(fwd * d); }
            }
            foreach (var sh in shifts)
            {
                for (float up = 0f; up <= maxUp + 1e-3f; up += o.raiseStep) yield return p0 + sh + Vector3.up * up;
            }
        }

        private static bool Free(UnitySign s, Vector3 p, Quaternion rot, IList<Bounds> obstacles, List<UnitySign> placed, Options o)
        {
            Bounds board = s.BoundsAt(p, rot);
            board.Expand(o.margin * 2f);
            Bounds holder = s.MountBoundsAt(p, rot);
            bool hasHolder = holder.size.sqrMagnitude > 1e-8f;
            foreach (var b in obstacles)
            {
                if (s.mount == SignMount.Wall && WallBehind(b, p, rot)) continue;
                if (b.Intersects(board)) return false;
                if (hasHolder && b.Intersects(holder)) return false;
            }
            foreach (var other in placed)
            {
                Bounds ob = other.BoardBounds();
                ob.Expand(o.signGap * 2f);
                if (ob.Intersects(board)) return false;
                // not right above / below another sign in plan (they would hide each other from above)
                Bounds a2 = board, b2 = other.BoardBounds();
                a2.center = new Vector3(a2.center.x, 0f, a2.center.z); a2.size = new Vector3(a2.size.x, 1f, a2.size.z);
                b2.center = new Vector3(b2.center.x, 0f, b2.center.z); b2.size = new Vector3(b2.size.x + o.signGap, 1f, b2.size.z + o.signGap);
                if (a2.Intersects(b2)) return false;
            }
            return true;
        }

        /// <summary>True when one of the viewpoints sees the board's centre unobstructed, from its front (either side of a
        /// double-sided sign), with a text height of at least the minimum angular size.</summary>
        public static bool Readable(UnitySign s, Vector3 p, Quaternion rot, IList<Bounds> obstacles, Options o)
        {
            if (o.viewpoints == null) return true;
            Vector3 front = rot * Quaternion.Euler(s.tilt, 0f, 0f) * Vector3.back;
            foreach (var v in o.viewpoints)
            {
                Vector3 d = v - p;
                float dist = d.magnitude;
                if (dist < 0.5f || s.EffectiveTextHeight / dist < o.minAngularSize) continue;
                float facing = Vector3.Dot(front, d / dist);
                if (s.doubleSided && s.mount != SignMount.Wall) facing = Mathf.Abs(facing);
                if (facing < 0.35f) continue;
                var ray = new Ray(p, d / dist);
                bool blocked = false;
                foreach (var b in obstacles)
                    if (b.IntersectRay(ray, out float t) && t > 0.3f && t < dist - 0.5f) { blocked = true; break; }
                if (!blocked) return true;
            }
            return false;
        }

        // A wall sign may touch the wall it is mounted on: the obstacle right behind its board.
        private static bool WallBehind(Bounds b, Vector3 p, Quaternion rot)
        {
            Vector3 back = rot * Vector3.forward;
            for (float d = 0.02f; d <= 0.8f; d += 0.04f)
                if (b.Contains(p + back * d)) return true;
            return false;
        }
    }
}
