using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The model of the connections between SimuLean objects in the scene (elements, lists, resource teams, transport
    /// nodes, downtimes): what exists, what can connect two objects, and the edits (Undo-recorded; they write the same
    /// fields the inspectors show). Used by the Connect tool, the passive drawing and the toolbox.
    /// </summary>
    public static class ConnectionModel
    {
        // ── groups, colours, labels (SceneConnections) ───────────────────────

        public static ConnectionGroup GroupOf(ConnectionKind k) => SceneConnections.GroupOf(k);
        public static Color ColorOf(ConnectionGroup g) => SceneConnections.ColorOf(g);
        public static string Label(ConnectionKind k) => SceneConnections.Label(k);

        // ── visibility filter (shared by the tool, the overlay and the passive drawing) ──

        private const string PrefShow = "SimuLean_ShowConnections";
        private const string PrefGroup = "SimuLean_ConnectionGroup_";
        private const string PrefOnlySelected = "SimuLean_ConnectionsOnlySelected";

        /// <summary>Connections drawn in the Scene view when the tool is not active.</summary>
        public static bool ShowWhenIdle
        {
            get => EditorPrefs.GetBool(PrefShow, true);
            set { EditorPrefs.SetBool(PrefShow, value); SceneView.RepaintAll(); }
        }

        public static bool OnlySelected
        {
            get => EditorPrefs.GetBool(PrefOnlySelected, false);
            set { EditorPrefs.SetBool(PrefOnlySelected, value); SceneView.RepaintAll(); }
        }

        public static bool IsGroupShown(ConnectionGroup g) => EditorPrefs.GetBool(PrefGroup + g, g != ConnectionGroup.Network);

        public static void SetGroupShown(ConnectionGroup g, bool on)
        {
            EditorPrefs.SetBool(PrefGroup + g, on);
            SceneView.RepaintAll();
        }

        /// <summary>The connections to draw now: the shown groups, every one or those touching the selection.</summary>
        public static bool IsShown(Connection c)
        {
            if (!IsGroupShown(GroupOf(c.Kind))) return false;
            if (c.From is SElement fe && fe.GetComponent<SElementConnectionGizmo>() is SElementConnectionGizmo g1 && !g1.showConnections) return false;
            if (!OnlySelected) return true;
            foreach (var go in Selection.gameObjects)
                if (go == c.From.gameObject || go == c.To.gameObject) return true;
            return false;
        }

        // ── endpoints ────────────────────────────────────────────────────────

        /// <summary>The SimuLean object a scene object stands for (its own component or a parent's), or null.</summary>
        public static Component EndpointOf(GameObject go)
        {
            if (go == null) return null;
            return (Component)go.GetComponentInParent<SElement>()
                   ?? (Component)go.GetComponentInParent<UnitySimList>()
                   ?? (Component)go.GetComponentInParent<UnityResourceTeam>()
                   ?? (Component)go.GetComponentInParent<UnityTransportNode>()
                   ?? go.GetComponentInParent<UnityDowntime>();
        }

        /// <summary>Every SimuLean object of the open scenes.</summary>
        public static List<Component> Endpoints()
        {
            var list = new List<Component>();
            list.AddRange(UnityCompat.FindAll<SElement>());
            list.AddRange(UnityCompat.FindAll<UnitySimList>());
            list.AddRange(UnityCompat.FindAll<UnityResourceTeam>());
            list.AddRange(UnityCompat.FindAll<UnityTransportNode>());
            list.AddRange(UnityCompat.FindAll<UnityDowntime>());
            return list;
        }

        /// <summary>Objects without a mesh of their own get a drawn handle (lists, teams, nodes, downtimes).</summary>
        public static bool NeedsHandle(Component c) => !(c is SElement) || c.GetComponentInChildren<Renderer>() == null;

        public static Color HandleColor(Component c)
        {
            if (c is UnitySimList) return ColorOf(ConnectionGroup.Lists);
            if (c is UnityResourceTeam) return ColorOf(ConnectionGroup.Resources);
            if (c is UnityTransportNode) return ColorOf(ConnectionGroup.Locations);
            if (c is UnityDowntime) return ColorOf(ConnectionGroup.Downtimes);
            if (c is UnityReaction) return ColorOf(ConnectionGroup.Logic);
            return ColorOf(ConnectionGroup.Flow);
        }

        // ── what exists (SceneConnections) ───────────────────────────────────

        public static List<Connection> All() => SceneConnections.All();
        public static bool IsDirected(Connection c) => SceneConnections.IsDirected(c);

        // ── what can connect two objects ─────────────────────────────────────

        /// <summary>A way to connect two objects: the kind, a menu label, and the edit.</summary>
        public sealed class Option
        {
            public ConnectionKind Kind;
            public string Label;
            public System.Action Apply;
        }

        /// <summary>
        /// The ways <paramref name="from"/> can connect to <paramref name="to"/> (dragged from → to). Undirected kinds
        /// (resources, locations, downtimes) work in both drag directions. <paramref name="oneWay"/> makes a new network
        /// track one-way. Empty when nothing fits; <paramref name="why"/> then says why (for the overlay).
        /// </summary>
        public static List<Option> OptionsFor(Component from, Component to, bool oneWay, out string why)
        {
            var options = new List<Option>();
            why = null;
            if (from == null || to == null) { why = "Drop on a SimuLean object."; return options; }
            if (from == to) { why = "An object cannot connect to itself."; return options; }
            if (from.gameObject.scene != to.gameObject.scene) { why = "The objects are in different scenes."; return options; }
            Directed(from, to, oneWay, options, ref why);
            Undirected(from, to, options, ref why);
            Undirected(to, from, options, ref why);
            if (options.Count == 0 && why == null) why = $"{Kind(from)} → {Kind(to)}: nothing to connect.";
            return options;
        }

        private static void Directed(Component from, Component to, bool oneWay, List<Option> options, ref string why)
        {
            if (from is SElement a && to is SElement b)
            {
                if (!a.HasOutput) { why = $"'{a.name}' has no output."; return; }
                if (!b.TakesConnections) { why = $"'{b.name}' has no input."; return; }
                if (a.nextElements != null && a.nextElements.Contains(b)) { why = $"'{a.name}' already sends to '{b.name}'."; return; }
                options.Add(new Option { Kind = ConnectionKind.Flow, Label = Label(ConnectionKind.Flow), Apply = () =>
                {
                    Undo.RecordObject(a, "Connect");
                    if (a.nextElements == null) a.nextElements = new List<SElement>();
                    a.nextElements.Add(b);
                    if (a.sendTo == SElement.SendMode.List) a.sendTo = SElement.SendMode.Elements;
                    Done(a);
                }});
            }
            else if (from is SElement e && to is UnitySimList list)
            {
                if (!e.HasOutput) { why = $"'{e.name}' has no output."; return; }
                if (e.PushList == list) { why = $"'{e.name}' already sends to '{list.name}'."; return; }
                options.Add(new Option { Kind = ConnectionKind.PushList, Label = Label(ConnectionKind.PushList), Apply = () =>
                {
                    Undo.RecordObject(e, "Connect");
                    e.sendTo = SElement.SendMode.List;
                    e.pushToList = list;
                    Done(e);
                }});
            }
            else if (from is UnitySimList src && to is SElement dst)
            {
                if (dst is UnityCombiner comb)
                {
                    int inputs = 1 + (comb.requirements != null ? comb.requirements.Length : 0);
                    for (int k = 0; k < inputs; k++)
                    {
                        int input = k;
                        options.Add(new Option
                        {
                            Kind = ConnectionKind.CombinerListInput,
                            Label = input == 0 ? "Combiner main input from list" : $"Combiner component input {input} from list",
                            Apply = () =>
                            {
                                Undo.RecordObject(comb, "Connect");
                                comb.listInputs.RemoveAll(li => li != null && li.input == input);
                                comb.listInputs.Add(new UnityCombiner.ListInput { input = input, list = src });
                                Done(comb);
                            },
                        });
                    }
                }
                else if (!dst.HasInput) why = $"'{dst.name}' has no input.";
                else if (dst.PullList == src) why = $"'{dst.name}' already receives from '{src.name}'.";
                else
                    options.Add(new Option { Kind = ConnectionKind.PullList, Label = Label(ConnectionKind.PullList), Apply = () =>
                    {
                        Undo.RecordObject(dst, "Connect");
                        dst.receiveFrom = SElement.ReceiveMode.List;
                        dst.pullFromList = src;
                        Done(dst);
                    }});
            }
            else if (from is UnityTransportNode na && to is UnityTransportNode nb)
            {
                UnityTransportNetwork net = NetworkOf(na) ?? NetworkOf(nb);
                if (net == null) { why = "These nodes belong to no transport network."; return; }
                if (NetworkOf(nb) != null && NetworkOf(nb) != net) { why = "The nodes belong to different networks."; return; }
                foreach (var ed in net.edges)
                    if (ed != null && ((ed.nodeA == na && ed.nodeB == nb) || (ed.nodeA == nb && ed.nodeB == na))) { why = "These nodes are already connected."; return; }
                options.Add(new Option { Kind = ConnectionKind.NetworkEdge, Label = oneWay ? "Network track (one-way)" : Label(ConnectionKind.NetworkEdge), Apply = () =>
                {
                    Undo.RecordObject(net, "Connect");
                    net.edges.Add(new UnityTransportNetwork.EdgeDefinition
                    {
                        nodeA = na, nodeB = nb, bidirectional = !oneWay && net.newEdgeBidirectional, trackType = net.newEdgeType,
                    });
                    Done(net);
                }});
            }
        }

        // element ↔ team, element ↔ node, team ↔ node, downtime ↔ element: the drag direction does not matter
        private static void Undirected(Component a, Component b, List<Option> options, ref string why)
        {
            if (a is SElement e && b is UnityResourceTeam team)
            {
                if (e.processBy.team == team || e.transportBy.team == team) why = $"'{e.name}' already uses '{team.name}'.";
                if (e is IProcessResourceUser && e.processBy.team != team)
                    options.Add(new Option { Kind = ConnectionKind.ProcessTeam, Label = Label(ConnectionKind.ProcessTeam), Apply = () =>
                    {
                        Undo.RecordObject(e, "Connect");
                        e.processBy.team = team;
                        Done(e);
                    }});
                if (e.HasOutput && e.transportBy.team != team)
                    options.Add(new Option { Kind = ConnectionKind.TransportTeam, Label = Label(ConnectionKind.TransportTeam), Apply = () =>
                    {
                        Undo.RecordObject(e, "Connect");
                        e.transportBy.team = team;
                        Done(e);
                    }});
            }
            else if (a is SElement placed && b is UnityTransportNode at && placed.location == at)
                why = $"'{placed.name}' is already at '{at.name}'.";
            else if (a is UnityResourceTeam based && b is UnityTransportNode basedAt && based.homeNode == basedAt)
                why = $"'{basedAt.name}' is already the home node of '{based.name}'.";
            else if (a is SElement el && b is UnityTransportNode node)
                options.Add(new Option { Kind = ConnectionKind.Location, Label = Label(ConnectionKind.Location), Apply = () =>
                {
                    Undo.RecordObject(el, "Connect");
                    el.location = node;
                    Done(el);
                }});
            else if (a is UnityResourceTeam t && b is UnityTransportNode home)
                options.Add(new Option { Kind = ConnectionKind.HomeNode, Label = Label(ConnectionKind.HomeNode), Apply = () =>
                {
                    Undo.RecordObject(t, "Connect");
                    t.homeNode = home;
                    Done(t);
                }});
            else if (a is SElement listened && b is UnityReaction reaction)
            {
                if (reaction.sources.Contains(listened)) { why = $"'{reaction.name}' already listens to '{listened.name}'."; return; }
                options.Add(new Option { Kind = ConnectionKind.ReactionSource, Label = Label(ConnectionKind.ReactionSource), Apply = () =>
                {
                    Undo.RecordObject(reaction, "Connect");
                    reaction.sources.Add(listened);
                    Done(reaction);
                }});
            }
            else if (a is UnityDowntime d && b is SElement target)
            {
                if (d.targets != null && d.targets.Contains(target)) { why = $"'{d.name}' already stops '{target.name}'."; return; }
                options.Add(new Option { Kind = ConnectionKind.DowntimeTarget, Label = Label(ConnectionKind.DowntimeTarget), Apply = () =>
                {
                    Undo.RecordObject(d, "Connect");
                    d.targets.Add(target);
                    if (d.targetKeys != null && d.targetKeys.Count > 0) d.targetKeys.Add("");   // keys stay aligned (empty = name)
                    Done(d);
                }});
            }
        }

        // ── edits of an existing connection ──────────────────────────────────

        public static void Remove(Connection c)
        {
            if (!c.IsValid) return;
            Undo.RecordObject(c.Owner, "Disconnect");
            switch (c.Kind)
            {
                case ConnectionKind.Flow: ((SElement)c.Owner).nextElements.RemoveAt(c.Index); break;
                case ConnectionKind.PushList: { var e = (SElement)c.Owner; e.pushToList = null; e.sendTo = SElement.SendMode.Elements; break; }
                case ConnectionKind.PullList: { var e = (SElement)c.Owner; e.pullFromList = null; e.receiveFrom = SElement.ReceiveMode.Connections; break; }
                case ConnectionKind.CombinerListInput: ((UnityCombiner)c.Owner).listInputs.RemoveAt(c.Index); break;
                case ConnectionKind.ProcessTeam: ((SElement)c.Owner).processBy.team = null; break;
                case ConnectionKind.TransportTeam: ((SElement)c.Owner).transportBy.team = null; break;
                case ConnectionKind.Location: ((SElement)c.Owner).location = null; break;
                case ConnectionKind.HomeNode: ((UnityResourceTeam)c.Owner).homeNode = null; break;
                case ConnectionKind.NetworkEdge: ((UnityTransportNetwork)c.Owner).edges.RemoveAt(c.Index); break;
                case ConnectionKind.ReactionSource: ((UnityReaction)c.Owner).sources.RemoveAt(c.Index); break;
                case ConnectionKind.ReactionTarget:
                {
                    var a = ((UnityReaction)c.Owner).actions[c.Index];
                    a.target = null; a.team = null;
                    break;
                }
                case ConnectionKind.DowntimeTarget:
                {
                    var d = (UnityDowntime)c.Owner;
                    d.targets.RemoveAt(c.Index);
                    if (d.targetKeys != null && c.Index < d.targetKeys.Count) d.targetKeys.RemoveAt(c.Index);
                    break;
                }
            }
            Done(c.Owner);
        }

        public static bool CanReverse(Connection c)
        {
            if (c.Kind == ConnectionKind.NetworkEdge) return true;
            return c.Kind == ConnectionKind.Flow && c.To is SElement b && b.HasOutput && c.From is SElement a && a.TakesConnections
                   && (b.nextElements == null || !b.nextElements.Contains(a));
        }

        public static void Reverse(Connection c)
        {
            if (!CanReverse(c)) return;
            if (c.Kind == ConnectionKind.NetworkEdge)
            {
                var net = (UnityTransportNetwork)c.Owner;
                Undo.RecordObject(net, "Reverse");
                var ed = net.edges[c.Index];
                (ed.nodeA, ed.nodeB) = (ed.nodeB, ed.nodeA);
                Done(net);
                return;
            }
            var a = (SElement)c.From; var b = (SElement)c.To;
            Undo.RecordObjects(new Object[] { a, b }, "Reverse");
            a.nextElements.Remove(b);
            if (b.nextElements == null) b.nextElements = new List<SElement>();
            b.nextElements.Add(a);
            Done(a); Done(b);
        }

        // ── helpers ──────────────────────────────────────────────────────────

        public static string Kind(Component c)
        {
            if (c is UnitySimList) return "list";
            if (c is UnityResourceTeam) return "resource team";
            if (c is UnityTransportNode) return "network node";
            if (c is UnityDowntime) return "downtime";
            return "element";
        }

        public static UnityTransportNetwork NetworkOf(UnityTransportNode node)
        {
            if (node == null) return null;
            var parent = node.GetComponentInParent<UnityTransportNetwork>();
            if (parent != null) return parent;
            foreach (var net in UnityCompat.FindAll<UnityTransportNetwork>())
                if (net.nodes.Contains(node)) return net;
            var all = UnityCompat.FindAll<UnityTransportNetwork>();
            return all.Length == 1 ? all[0] : null;   // one network in the scene: a loose node joins it
        }

        private static void Done(Object owner)
        {
            EditorUtility.SetDirty(owner);
            if (owner is Component c && c.gameObject.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(c.gameObject.scene);
            SceneView.RepaintAll();
        }
    }
}
