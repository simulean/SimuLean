using System.Collections.Generic;
using SimuLean.Headless;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Draws the items that walk by themselves (engine <see cref="WalkLink"/>): each walking item follows its
    /// leg — the route's network nodes (or a walk grid's path) from where it left to the destination's location — by the
    /// simulation time, on the floor, as a resource member does along its leg; an item waiting at a door stands at it. A
    /// person (<see cref="PersonAnimator"/>) plays its walk. Driven by the clock after each advance; read only.
    /// </summary>
    public static class ItemWalkView
    {
        private sealed class Track
        {
            public SimuLean.Resources.TravelLeg Leg;
            public readonly List<Vector3> Points = new List<Vector3>();
            public readonly List<float> Cum = new List<float>();
            public float Length;
            public float Shown;     // distance drawn so far
            public GameObject DestroyAtEnd;   // taken by a sink: destroyed when the drawing reaches the end
        }

        private const float CatchUpSpeed = 1.5f, CatchUpSeconds = 0.25f;

        private static readonly Dictionary<Item, Track> tracks = new Dictionary<Item, Track>();
        private static readonly HashSet<Item> live = new HashSet<Item>();
        private static readonly List<Item> gone = new List<Item>();
        // the walks that ended since the last draw (WalkLink.Arrived): one drawn behind the model, or not at all (shorter
        // than a frame), hands the rest of its way to the person, who walks it instead of straight through the walls
        private static readonly HashSet<WalkLink> listening = new HashSet<WalkLink>();
        private static readonly List<ItemWalk> arrived = new List<ItemWalk>();
        private static readonly List<Vector3> rest = new List<Vector3>();

        /// <summary>True when an item was walking at the last draw (a <see cref="SimSpeedProfile"/> slows down for it).</summary>
        public static bool AnyWalking { get; private set; }

        /// <summary>Places the walking items of <paramref name="model"/> at <paramref name="now"/>.</summary>
        public static void Draw(HeadlessModelFactory model, double now)
        {
            AnyWalking = false;
            live.Clear();
            if (model != null)
                foreach (var link in model.WalkLinks)
                {
                    if (listening.Add(link)) link.Arrived += arrived.Add;
                    foreach (var w in link.Walks)
                    {
                        if (!(w.Item.vItem is GameObject g) || g == null) continue;
                        live.Add(w.Item);
                        if (!tracks.TryGetValue(w.Item, out var t) || t.Leg != w.Leg)
                        {
                            // an item a label hid at its element (a patient at home while the study is corrected) was not
                            // where its view stood: it sets off from the element's location, not through the walls
                            bool hidden = UnityElementLabel.HidesItemsOf(w.From?.vElement as SElement);
                            var exit = g.GetComponent<PersonAnimator>()?.TakeExitVia();
                            tracks[w.Item] = t = Build(model, w.Leg, g.transform.position, hidden ? null : exit, hidden);
                        }
                        // the drawn position follows the model's, catching up within ~0.25 s (the frame a walk starts in
                        // may already be part-way through it: no jump)
                        float s = (float)w.Leg.Fraction(now) * t.Length;
                        float dt = Time.deltaTime;
                        t.Shown = Mathf.MoveTowards(t.Shown, s, Mathf.Max(CatchUpSpeed * dt, (s - t.Shown) * Mathf.Min(1f, dt / CatchUpSeconds)));
                        Vector3 p = At(t, t.Shown, out Vector3 dir);
                        g.transform.position = p;
                        if (!w.Waiting && dir.sqrMagnitude > 1e-6f) g.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                        var person = g.GetComponent<PersonAnimator>();
                        if (person != null) { person.pose = PersonAnimator.Pose.Stand; person.DriveWalk(!w.Waiting); }
                        if (!w.Waiting) AnyWalking = true;
                    }
                }
            foreach (var w in arrived)
            {
                if (!(w.Item.vItem is GameObject g) || g == null) continue;          // taken by a sink at once
                var person = g.GetComponent<PersonAnimator>();
                if (person == null) continue;
                if (!tracks.TryGetValue(w.Item, out var t) || t.Leg != w.Leg)   // never drawn: all of its way
                {
                    if (model == null) continue;
                    bool hidden = UnityElementLabel.HidesItemsOf(w.From?.vElement as SElement);
                    var exit = person.TakeExitVia();
                    t = Build(model, w.Leg, g.transform.position, hidden ? null : exit, hidden);
                }
                if (t.DestroyAtEnd != null || t.Shown >= t.Length - 1e-3f) continue;   // a sink finishes it below / arrived
                rest.Clear();
                for (int i = 1; i < t.Points.Count; i++) if (t.Cum[i] > t.Shown) rest.Add(t.Points[i]);
                person.FinishWalk(rest);
            }
            arrived.Clear();
            gone.Clear();
            foreach (var kv in tracks)
            {
                if (live.Contains(kv.Key)) continue;
                var t = kv.Value;
                // a walk ended in the model while its drawing was behind: one that leaves the scene at the end (a sink)
                // finishes its way to the door first
                if (t.DestroyAtEnd != null && t.Shown < t.Length - 1e-3f)
                {
                    t.Shown = Mathf.MoveTowards(t.Shown, t.Length, Mathf.Max(CatchUpSpeed, (t.Length - t.Shown) / CatchUpSeconds) * Time.deltaTime);
                    t.DestroyAtEnd.transform.position = At(t, t.Shown, out Vector3 dir);
                    if (dir.sqrMagnitude > 1e-6f) t.DestroyAtEnd.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                    t.DestroyAtEnd.GetComponent<PersonAnimator>()?.DriveWalk(true);
                    AnyWalking = true;
                    continue;
                }
                if (t.DestroyAtEnd != null) Object.Destroy(t.DestroyAtEnd);
                gone.Add(kv.Key);
            }
            foreach (var k in gone) tracks.Remove(k);
        }

        /// <summary>An item a sink takes (it leaves the scene): destroyed now, or — when its walk is still drawn behind the
        /// model — once it reached the end of its way (the exit door). True when the view takes care of it.</summary>
        public static bool DestroyWhenArrived(Item item, GameObject view)
        {
            if (item == null || view == null || !tracks.TryGetValue(item, out var t) || t.Shown >= t.Length - 1e-3f) return false;
            t.DestroyAtEnd = view;
            return true;
        }

        /// <summary>Forgets the walks (a new run).</summary>
        public static void Clear()
        {
            foreach (var t in tracks.Values)   // still on their way out
                if (t.DestroyAtEnd != null)
                {
                    if (Application.isPlaying) Object.Destroy(t.DestroyAtEnd);
                    else Object.DestroyImmediate(t.DestroyAtEnd);
                }
            tracks.Clear();
            foreach (var link in listening) link.Arrived -= arrived.Add;
            listening.Clear();
            arrived.Clear();
            AnyWalking = false;
        }

        // the polyline: where the item stood (on the floor of the first point; the first point itself when it was hidden),
        // the route's nodes or the grid path
        private static Track Build(HeadlessModelFactory model, SimuLean.Resources.TravelLeg leg, Vector3 from, Vector3? exit = null,
                                   bool fromRoute = false)
        {
            var t = new Track { Leg = leg };
            var pts = new List<Vector3>();
            if (leg.Path != null && leg.Path.Count > 0)
                foreach (var wp in leg.Path) pts.Add(new Vector3((float)wp.X, (float)wp.Y, (float)wp.Z));
            else if (leg.Route != null && model.Network != null)
                foreach (string id in leg.Route)
                {
                    var n = model.Network.GetNode(id);
                    if (n != null) pts.Add(new Vector3((float)n.X, (float)n.Y, (float)n.Z));
                }
            if (pts.Count == 0 && model.Network != null)
            {
                var a = model.Network.GetNode(leg.From); var b = model.Network.GetNode(leg.To);
                if (a != null) pts.Add(new Vector3((float)a.X, (float)a.Y, (float)a.Z));
                if (b != null) pts.Add(new Vector3((float)b.X, (float)b.Y, (float)b.Z));
            }
            if (pts.Count > 0 && fromRoute) from = pts[0];
            if (pts.Count > 0) from.y = pts[0].y;               // off the seat / the couch, onto the floor
            // starting beyond the first node (it stood past its element's location, towards the way out): no going back
            while (pts.Count >= 2 && Flat(from - pts[1]) < Flat(pts[0] - pts[1])) pts.RemoveAt(0);
            t.Points.Add(from);
            if (exit.HasValue) t.Points.Add(new Vector3(exit.Value.x, from.y, exit.Value.z));   // out of a box through its door
            foreach (var p in pts) if ((p - t.Points[t.Points.Count - 1]).sqrMagnitude > 1e-6f) t.Points.Add(p);
            t.Cum.Add(0f);
            for (int i = 1; i < t.Points.Count; i++) t.Cum.Add(t.Cum[i - 1] + Vector3.Distance(t.Points[i - 1], t.Points[i]));
            t.Length = t.Cum[t.Cum.Count - 1];
            return t;
        }

        private static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        private static Vector3 At(Track t, float s, out Vector3 dir)
        {
            dir = Vector3.zero;
            if (t.Points.Count == 1) return t.Points[0];
            for (int i = 1; i < t.Points.Count; i++)
            {
                if (s > t.Cum[i] && i < t.Points.Count - 1) continue;
                float seg = t.Cum[i] - t.Cum[i - 1];
                float f = seg > 1e-6f ? Mathf.Clamp01((s - t.Cum[i - 1]) / seg) : 1f;
                dir = t.Points[i] - t.Points[i - 1]; dir.y = 0;
                return Vector3.Lerp(t.Points[i - 1], t.Points[i], f);
            }
            return t.Points[t.Points.Count - 1];
        }
    }
}
