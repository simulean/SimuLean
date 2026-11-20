using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The layout of a model on the floor, for the agent commands: an element's footprint (the floor
    /// area of its own renderers), the footprints that overlap, and what a picture of the model frames — the elements, the
    /// transport network's nodes, the teams' members (their edit-mode previews) and the objects without a mesh (lists,
    /// downtimes, reactions), never the floor or decoration. Read-only.
    /// </summary>
    internal static class ModelLayout
    {
        /// <summary>Two footprints overlapping by less than this (m, in either direction) only touch.</summary>
        public const float OverlapTolerance = 0.1f;

        // ── footprints ───────────────────────────────────────────────────────

        /// <summary>The world bounds of <paramref name="go"/>'s own renderers: its children's too, except those of another
        /// element under it, of a team's member previews, and lines / trails / particles. Null when it has none (a
        /// replenisher, a milk run without a model).</summary>
        public static Bounds? BoundsOf(GameObject go)
        {
            Bounds? b = null;
            var self = go.GetComponent<SElement>();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is LineRenderer || r is TrailRenderer || r is ParticleSystemRenderer) continue;
                if (self != null && r.GetComponentInParent<SElement>() != self) continue;   // a nested element's
                if (r.GetComponentInParent<SimuLeanFloor>() != null) continue;
                if (b == null) b = r.bounds; else { var x = b.Value; x.Encapsulate(r.bounds); b = x; }
            }
            return b;
        }

        /// <summary>An element's footprint: its size on the floor [width along X, depth along Z] in metres (0.1 m), or null.</summary>
        public static float[] SizeOf(GameObject go)
        {
            var b = BoundsOf(go);
            return b == null ? null : new[] { R1(b.Value.size.x), R1(b.Value.size.z) };
        }

        /// <summary>The footprints that overlap among <paramref name="elements"/> (an element and one nested in it are not
        /// compared), one plain sentence each; with <paramref name="involving"/>, only the pairs with one of those names.</summary>
        /// The teams' members (their edit-mode previews) are compared too (a team placed at the origin would stand on a
        /// Source unnoticed); a member is named "team member 'X_1' of 'X'" and counts as its team for <paramref name="involving"/>;
        /// two members of one team are not compared.</summary>
        public static List<string> Overlaps(IEnumerable<GameObject> elements, ICollection<string> involving = null)
        {
            TeamMemberPreviews.EnsureAll();
            var list = elements.Where(e => e != null)
                               .Select(e => (go: e, b: BoundsOf(e), team: (UnityResourceTeam)null))
                               .Concat(MemberPreviews())
                               .Where(x => x.b != null).ToList();
            var found = new List<string>();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var (a, ba, ta) = list[i];
                    var (c, bc, tc) = list[j];
                    if (a.transform.IsChildOf(c.transform) || c.transform.IsChildOf(a.transform)) continue;
                    if (ta != null && ta == tc) continue;
                    string ka = ta != null ? ta.name : a.name, kc = tc != null ? tc.name : c.name;
                    if (involving != null && !involving.Contains(ka) && !involving.Contains(kc)) continue;
                    float ox = Mathf.Min(ba.Value.max.x, bc.Value.max.x) - Mathf.Max(ba.Value.min.x, bc.Value.min.x);
                    float oz = Mathf.Min(ba.Value.max.z, bc.Value.max.z) - Mathf.Max(ba.Value.min.z, bc.Value.min.z);
                    if (ox <= OverlapTolerance || oz <= OverlapTolerance) continue;
                    found.Add($"{Label(a, ta)} ({Size(ba.Value)} m at {At(a)}) and {Label(c, tc)} ({Size(bc.Value)} m at {At(c)}) overlap on the " +
                              $"floor by {F(ox)} × {F(oz)} m: move one of them (its footprint is its size in simulean_model; a team " +
                              "member stands at its home node, else at the team's position).");
                }
            return found;
        }

        private static string Label(GameObject go, UnityResourceTeam team)
            => team == null ? $"'{go.name}'" : $"team member '{go.name.Replace(" (preview)", "")}' of '{team.name}'";

        // the members' edit-mode previews of every team in the open scenes, with their team
        private static IEnumerable<(GameObject go, Bounds? b, UnityResourceTeam team)> MemberPreviews()
        {
            foreach (var t in UnityCompat.FindAll<UnityResourceTeam>())
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                var preview = t.transform.Find(TeamMemberPreviews.Child);
                if (preview == null) continue;
                foreach (Transform m in preview) yield return (m.gameObject, BoundsOf(m.gameObject), t);
            }
        }

        /// <summary>Where a team placed without a position goes (at the origin its members would stand on an element):
        /// in front of the model (−Z, where the elements' fronts face), 3 m beyond the nearest footprint of the elements and
        /// teams, at the model's left edge; the origin when the scene has no element. <paramref name="except"/> = the team being
        /// placed (its own place and members left out).</summary>
        public static Vector3 FreeSpotForTeam(UnityResourceTeam except = null)
        {
            TeamMemberPreviews.EnsureAll();
            Bounds? all = null;
            void Add(Bounds? b) { if (b == null) return; if (all == null) all = b; else { var x = all.Value; x.Encapsulate(b.Value); all = x; } }
            foreach (var e in Elements()) Add(BoundsOf(e) ?? new Bounds(e.transform.position, Vector3.one));
            foreach (var m in MemberPreviews()) if (except == null || m.team != except) Add(m.b);
            foreach (var t in UnityCompat.FindAll<UnityResourceTeam>())
                if (t != null && t != except && t.gameObject.activeInHierarchy) Add(new Bounds(t.transform.position, Vector3.one));
            if (all == null) return Vector3.zero;
            return new Vector3(R1(all.Value.min.x + 1f), 0f, R1(all.Value.min.z - 3f));
        }

        /// <summary>The tools of the model's logic that have no footprint but a place in a picture (a capture draws them as
        /// markers): lists, downtimes and reactions. Their position means nothing to a run, so one placed without a position
        /// goes beside the model (<see cref="FreeSpotForTool"/>; at the origin they would sit on an element). Other tools
        /// keep the position given: a transport node, a sign or a floor zone mean their place; data, study and analysis tools
        /// are not drawn.</summary>
        public static bool IsMarkerTool(Type type)
            => typeof(UnitySimList).IsAssignableFrom(type) || typeof(UnityDowntime).IsAssignableFrom(type)
               || typeof(UnityReaction).IsAssignableFrom(type);

        /// <summary>Spacing of the tools placed beside the model (m).</summary>
        public const float ToolSpacing = 2f;

        /// <summary>Where a list, downtime or reaction placed without a position goes: behind the model (+Z, the
        /// teams go in front), 3 m beyond the far edge of the elements' footprints, in a row from the model's left edge,
        /// <see cref="ToolSpacing"/> apart — the first spot no other such tool, transport node or team stands on (within 1 m;
        /// a list would land on a node); from the origin when the scene has no element. <paramref name="except"/> =
        /// the tool being placed.</summary>
        public static Vector3 FreeSpotForTool(GameObject except = null)
        {
            Bounds? all = null;
            foreach (var e in Elements())
            {
                var b = BoundsOf(e) ?? new Bounds(e.transform.position, Vector3.one);
                if (all == null) all = b; else { var x = all.Value; x.Encapsulate(b); all = x; }
            }
            var taken = MarkerTools().Where(c => c.gameObject != except).Select(c => c.transform.position)
                .Concat(UnityCompat.FindAll<UnityTransportNode>().Where(n => n != null && n.gameObject.activeInHierarchy).Select(n => n.transform.position))
                .Concat(UnityCompat.FindAll<UnityResourceTeam>().Where(t => t != null && t.gameObject.activeInHierarchy).Select(t => t.transform.position))
                .ToList();
            float x0 = all == null ? 0f : R1(all.Value.min.x + 1f), z0 = all == null ? 0f : R1(all.Value.max.z + 3f);
            for (int i = 0; ; i++)
            {
                var spot = new Vector3(x0 + i * ToolSpacing, 0f, z0);
                if (!taken.Any(p => new Vector2(p.x - spot.x, p.z - spot.z).magnitude < 1f)) return spot;
            }
        }

        // the lists, downtimes and reactions of the open scenes
        private static IEnumerable<Component> MarkerTools()
            => UnityCompat.FindAll<UnitySimList>().Cast<Component>().Concat(UnityCompat.FindAll<UnityDowntime>())
                          .Concat(UnityCompat.FindAll<UnityReaction>())
                          .Where(c => c != null && c.gameObject.activeInHierarchy);

        /// <summary>The model's elements in the open scenes (or under <paramref name="root"/>).</summary>
        public static List<GameObject> Elements(GameObject root = null)
            => UnityCompat.FindAllInHierarchyOrder<SElement>()
                          .Where(e => e != null && e.gameObject.activeInHierarchy && (root == null || e.transform.IsChildOf(root.transform)))
                          .Select(e => e.gameObject).ToList();

        /// <summary>Adds to each element of a <c>simulean_model</c> answer (its <c>elements</c>, matched by name) where it
        /// stands, <c>at</c> [x, z], and its footprint, <c>size</c> [width, depth] in metres (none without a renderer).</summary>
        public static void AddFootprints(IDictionary<string, object> description, GameObject root = null)
        {
            if (!(description.TryGetValue("elements", out var list) && list is System.Collections.IEnumerable entries)) return;
            var byName = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (var go in Elements(root)) if (!byName.ContainsKey(go.name)) byName[go.name] = go;
            foreach (var entry in entries)
            {
                if (!(entry is IDictionary<string, object> e) || !(e.TryGetValue("name", out var n) && n is string name)) continue;
                if (!byName.TryGetValue(name, out var go)) continue;
                // after the name and type, before the (long) parameters
                var rest = e.Where(kv => kv.Key != "name" && kv.Key != "type").ToList();
                e.TryGetValue("type", out var type);
                e.Clear();
                e["name"] = name;
                if (type != null) e["type"] = type;
                e["at"] = new[] { R1(go.transform.position.x), R1(go.transform.position.z) };
                var size = SizeOf(go);
                if (size != null) e["size"] = size;
                foreach (var kv in rest) e[kv.Key] = kv.Value;
            }
        }

        private static string Size(Bounds b) => $"{F(b.size.x)} × {F(b.size.z)}";
        private static string At(GameObject go) => $"x {F(go.transform.position.x)}, z {F(go.transform.position.z)}";
        private static float R1(float v) => (float)Math.Round(v, 1);
        private static string F(float v) => R1(v).ToString("0.#", CultureInfo.InvariantCulture);

        // ── what a picture of the model frames ───────────────────────────────

        /// <summary>The content of a model picture: boxes (rendered objects) and points (objects without a mesh), the
        /// markers to draw and the counts per kind.</summary>
        public sealed class Content
        {
            public readonly List<Bounds> Boxes = new List<Bounds>();
            public readonly List<Vector3> Points = new List<Vector3>();
            /// <summary>Objects drawn as a disc (no mesh of their own) with their colour and name.</summary>
            public readonly List<(Vector3 at, Color color, string name)> Markers = new List<(Vector3, Color, string)>();
            public int Elements, Nodes, Teams, Members, Lists, Downtimes, Reactions;
            /// <summary>The whole model (a minimum size applies) rather than named objects.</summary>
            public bool WholeModel;

            public bool IsEmpty => Boxes.Count == 0 && Points.Count == 0;

            public string Describe()
            {
                var parts = new List<string> { $"{Elements} elements" };
                if (Nodes > 0) parts.Add($"{Nodes} transport nodes");
                if (Teams > 0) parts.Add($"{Teams} teams ({Members} members shown)");
                if (Lists > 0) parts.Add($"{Lists} lists");
                if (Downtimes > 0) parts.Add($"{Downtimes} downtimes");
                if (Reactions > 0) parts.Add($"{Reactions} reactions");
                return "the model (" + string.Join(", ", parts) + ")";
            }

            internal void Add(Bounds? b, Vector3 fallback)
            {
                if (b != null) Boxes.Add(b.Value); else Points.Add(fallback);
            }

            internal void Mark(Component c, string name)
            {
                Points.Add(c.transform.position);
                Markers.Add((c.transform.position, ConnectionModel.HandleColor(c), name));
            }
        }

        /// <summary>Every object of the model in the open scenes (or in <paramref name="scene"/>): the elements, the transport
        /// nodes, the teams with their members' previews, lists, downtimes and reactions. Not the floor, the floor plan,
        /// decoration placed with <c>simulean_place_model</c> or the analysis tools (charts, reports: they show results, not
        /// the layout).</summary>
        public static Content OfModel(Scene? scene = null)
        {
            bool In(Component c) => c != null && c.gameObject.activeInHierarchy && (scene == null || c.gameObject.scene == scene.Value);
            var content = new Content { WholeModel = true };
            foreach (var e in UnityCompat.FindAllInHierarchyOrder<SElement>().Where(In))
            {
                content.Elements++;
                var b = BoundsOf(e.gameObject);
                content.Add(b, e.transform.position);
                if (b == null) content.Markers.Add((e.transform.position, ConnectionModel.HandleColor(e), e.name));
            }
            foreach (var n in UnityCompat.FindAll<UnityTransportNode>().Where(In)) { content.Nodes++; content.Mark(n, n.name); }
            foreach (var t in UnityCompat.FindAll<UnityResourceTeam>().Where(In)) AddTeam(content, t);
            foreach (var l in UnityCompat.FindAll<UnitySimList>().Where(In)) { content.Lists++; content.Mark(l, l.name); }
            foreach (var d in UnityCompat.FindAll<UnityDowntime>().Where(In)) { content.Downtimes++; content.Mark(d, d.name); }
            foreach (var r in UnityCompat.FindAll<UnityReaction>().Where(In)) { content.Reactions++; content.Mark(r, r.name); }
            return content;
        }

        /// <summary>The named objects: an element (or any object) by its renderers, else its position; a team with its
        /// members; an object without a mesh as a marker.</summary>
        public static Content OfObjects(IEnumerable<GameObject> objects)
        {
            var content = new Content();
            foreach (var go in objects)
            {
                var team = go.GetComponent<UnityResourceTeam>();
                if (team != null) { AddTeam(content, team); continue; }
                if (go.GetComponent<SElement>() != null) content.Elements++;
                var b = BoundsOf(go);
                content.Add(b, go.transform.position);
                var handle = ConnectionModel.EndpointOf(go);
                if (b == null && handle != null) content.Markers.Add((go.transform.position, ConnectionModel.HandleColor(handle), go.name));
            }
            return content;
        }

        // a team: its members' previews (TeamMemberPreviews) and, without any, a marker at the team; its home node
        private static void AddTeam(Content content, UnityResourceTeam t)
        {
            content.Teams++;
            var preview = t.transform.Find(TeamMemberPreviews.Child);
            int shown = 0;
            if (preview != null)
                foreach (Transform m in preview)
                {
                    var b = BoundsOf(m.gameObject);
                    if (b == null) continue;
                    content.Boxes.Add(b.Value);
                    shown++;
                }
            content.Members += shown;
            if (shown == 0) content.Mark(t, t.name);
            if (t.homeNode != null) content.Points.Add(t.homeNode.transform.position);
        }

        /// <summary>The connections a picture draws: <paramref name="groups"/> = group names comma-separated (flow, lists,
        /// resources, locations, network, downtimes, logic), all or none.</summary>
        public static HashSet<ConnectionGroup> Groups(string groups)
        {
            var set = new HashSet<ConnectionGroup>();
            string text = (groups ?? "").Trim();
            if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return set;
            if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (ConnectionGroup g in Enum.GetValues(typeof(ConnectionGroup))) set.Add(g);
                return set;
            }
            foreach (string part in text.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse(part.Trim(), true, out ConnectionGroup g) || !Enum.IsDefined(typeof(ConnectionGroup), g))
                    throw new ArgumentException($"connections: '{part.Trim()}' is not a group (flow, lists, resources, locations, network, " +
                                                "downtimes, logic), all or none.");
                set.Add(g);
            }
            return set;
        }
    }
}
