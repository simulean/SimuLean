using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>Every kind of connection between SimuLean objects in a scene.</summary>
    public enum ConnectionKind
    {
        Flow,               // element → element (Next Elements)
        PushList,           // element → list (Send To = List)
        PullList,           // list → element (Receive From = List)
        CombinerListInput,  // list → combiner input
        ProcessTeam,        // element ↔ team (process resources)
        TransportTeam,      // element ↔ team (transport resources)
        Location,           // element ↔ network node
        NetworkEdge,        // node → node (a track of a transport network)
        HomeNode,           // team ↔ network node
        DowntimeTarget,     // downtime ↔ element
        ReactionSource,     // element → reaction (it listens to the element)
        ReactionTarget,     // reaction → element / team (an action stops, resumes or sets it)
    }

    /// <summary>The groups the tool shows and filters (one colour each).</summary>
    public enum ConnectionGroup { Flow, Lists, Resources, Locations, Network, Downtimes, Logic }

    /// <summary>One existing connection: its kind, its two ends and, for list-held ones, the index in the owner's list.</summary>
    public readonly struct Connection
    {
        public readonly ConnectionKind Kind;
        public readonly Component From, To;
        /// <summary>The object whose fields hold the connection (what Undo records).</summary>
        public readonly Object Owner;
        public readonly int Index;

        public Connection(ConnectionKind kind, Component from, Component to, Object owner, int index = -1)
        {
            Kind = kind; From = from; To = to; Owner = owner; Index = index;
        }

        public bool IsValid => From != null && To != null && Owner != null;
        public bool Same(Connection o) => Kind == o.Kind && From == o.From && To == o.To && Owner == o.Owner && Index == o.Index;
    }

    /// <summary>
    /// The connections between the SimuLean objects of the open scenes (elements, lists, resource teams, transport
    /// nodes, downtimes), read from the fields the inspectors edit: what exists, their groups, colours and labels.
    /// Read-only — shared by the editor's Connect tool / drawing and the run-time connection view of the HUD.
    /// </summary>
    public static class SceneConnections
    {
        // ── groups, colours, labels ──────────────────────────────────────────

        public static ConnectionGroup GroupOf(ConnectionKind k)
        {
            switch (k)
            {
                case ConnectionKind.Flow: return ConnectionGroup.Flow;
                case ConnectionKind.PushList: case ConnectionKind.PullList: case ConnectionKind.CombinerListInput: return ConnectionGroup.Lists;
                case ConnectionKind.ProcessTeam: case ConnectionKind.TransportTeam: return ConnectionGroup.Resources;
                case ConnectionKind.Location: case ConnectionKind.HomeNode: return ConnectionGroup.Locations;
                case ConnectionKind.NetworkEdge: return ConnectionGroup.Network;
                case ConnectionKind.ReactionSource: case ConnectionKind.ReactionTarget: return ConnectionGroup.Logic;
                default: return ConnectionGroup.Downtimes;
            }
        }

        public static Color ColorOf(ConnectionGroup g)
        {
            switch (g)
            {
                case ConnectionGroup.Flow: return new Color(0.2f, 0.8f, 1f);
                case ConnectionGroup.Lists: return new Color(0.78f, 0.5f, 1f);
                case ConnectionGroup.Resources: return new Color(1f, 0.62f, 0.18f);
                case ConnectionGroup.Locations: return new Color(0.75f, 0.75f, 0.75f);
                case ConnectionGroup.Network: return new Color(0.55f, 1f, 0.55f);
                case ConnectionGroup.Logic: return new Color(0.80f, 0.55f, 0.95f);
                default: return new Color(1f, 0.38f, 0.38f);
            }
        }

        public static string Label(ConnectionKind k)
        {
            switch (k)
            {
                case ConnectionKind.Flow: return "Flow (next element)";
                case ConnectionKind.PushList: return "Send to list";
                case ConnectionKind.PullList: return "Receive from list";
                case ConnectionKind.CombinerListInput: return "Combiner input from list";
                case ConnectionKind.ProcessTeam: return "Process resources";
                case ConnectionKind.TransportTeam: return "Transport resources";
                case ConnectionKind.Location: return "Location (network node)";
                case ConnectionKind.NetworkEdge: return "Network track";
                case ConnectionKind.HomeNode: return "Home node";
                case ConnectionKind.ReactionSource: return "Reaction listens to";
                case ConnectionKind.ReactionTarget: return "Reaction acts on";
                default: return "Downtime target";
            }
        }

        // ── what exists ──────────────────────────────────────────────────────

        public static List<Connection> All()
        {
            var all = new List<Connection>();
            foreach (var e in UnityCompat.FindAll<SElement>())
            {
                if (e.nextElements != null)
                    for (int i = 0; i < e.nextElements.Count; i++)
                        if (e.nextElements[i] != null) all.Add(new Connection(ConnectionKind.Flow, e, e.nextElements[i], e, i));
                if (e.PushList != null) all.Add(new Connection(ConnectionKind.PushList, e, e.PushList, e));
                if (e.PullList != null) all.Add(new Connection(ConnectionKind.PullList, e.PullList, e, e));
                if (e is UnityCombiner comb && comb.listInputs != null)
                    for (int i = 0; i < comb.listInputs.Count; i++)
                        if (comb.listInputs[i]?.list != null)
                            all.Add(new Connection(ConnectionKind.CombinerListInput, comb.listInputs[i].list, comb, comb, i));
                if (e is IProcessResourceUser && e.processBy != null && e.processBy.team != null)
                    all.Add(new Connection(ConnectionKind.ProcessTeam, e, e.processBy.team, e));
                if (e.transportBy != null && e.transportBy.team != null)
                    all.Add(new Connection(ConnectionKind.TransportTeam, e, e.transportBy.team, e));
                if (e.location != null) all.Add(new Connection(ConnectionKind.Location, e, e.location, e));
            }
            foreach (var t in UnityCompat.FindAll<UnityResourceTeam>())
                if (t.homeNode != null) all.Add(new Connection(ConnectionKind.HomeNode, t, t.homeNode, t));
            foreach (var net in UnityCompat.FindAll<UnityTransportNetwork>())
                for (int i = 0; i < net.edges.Count; i++)
                {
                    var ed = net.edges[i];
                    if (ed?.nodeA != null && ed.nodeB != null) all.Add(new Connection(ConnectionKind.NetworkEdge, ed.nodeA, ed.nodeB, net, i));
                }
            foreach (var d in UnityCompat.FindAll<UnityDowntime>())
                if (d.targets != null)
                    for (int i = 0; i < d.targets.Count; i++)
                        if (d.targets[i] != null) all.Add(new Connection(ConnectionKind.DowntimeTarget, d, d.targets[i], d, i));
            foreach (var r in UnityCompat.FindAll<UnityReaction>())
            {
                if (r.sources != null && !r.IsTimeSignal)
                    for (int i = 0; i < r.sources.Count; i++)
                        if (r.sources[i] != null) all.Add(new Connection(ConnectionKind.ReactionSource, r.sources[i], r, r, i));
                if (r.actions != null)
                    for (int i = 0; i < r.actions.Count; i++)
                    {
                        var a = r.actions[i];
                        Component to = a == null ? null : a.team != null && a.onTeam ? a.team : (Component)a.target;
                        if (to != null) all.Add(new Connection(ConnectionKind.ReactionTarget, r, to, r, i));
                    }
            }
            return all;
        }

        /// <summary>True when the connection draws an arrow (it has a direction that matters).</summary>
        public static bool IsDirected(Connection c)
        {
            switch (c.Kind)
            {
                case ConnectionKind.Flow: case ConnectionKind.PushList: case ConnectionKind.PullList: case ConnectionKind.CombinerListInput: return true;
                case ConnectionKind.ReactionSource: case ConnectionKind.ReactionTarget: return true;
                case ConnectionKind.NetworkEdge: return c.Owner is UnityTransportNetwork n && c.Index < n.edges.Count && !n.edges[c.Index].bidirectional;
                default: return false;
            }
        }

        /// <summary>Where a connection ends on an object: the centre of its visible bounds, or its position.</summary>
        public static Vector3 Anchor(Component c)
        {
            if (c is SElement)
            {
                var r = c.GetComponentInChildren<Renderer>();
                if (r != null) return r.bounds.center;
            }
            return c.transform.position;
        }
    }
}
