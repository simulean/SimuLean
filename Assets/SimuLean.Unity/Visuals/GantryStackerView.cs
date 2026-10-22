using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Visual-only animation of a <see cref="UnityStacker"/> by a gantry (the <c>GantryStacker</c> model of the
    /// library: <c>Bridge</c> travels along its parent's local Z, <c>Trolley</c> along the bridge = local X, <c>Head</c>
    /// up / down; pivot of <c>Head</c> = suction-cup lips). Driven by the element's events, never changes the simulation.
    ///
    /// <para><b>Detailed</b> (see <see cref="SimVisualDetail"/>): one pick-and-place cycle per
    /// <see cref="Stacker.CycleStarted"/>: travel to the pick point, head down, pick, up, travel to the stack, down onto
    /// the stack top, release, up. The animation runs on <b>simulation time</b>: it lasts the element's cycle time
    /// (or <see cref="fallbackCycle"/> when placement is instantaneous), clamped to [<see cref="minCycle"/>,
    /// <see cref="maxCycle"/>], shortened while cycles queue up, frozen while the element's work is paused (stops), and
    /// cycles beyond <see cref="maxLag"/> snap. A stack that completes first flushes its pending moves.</para>
    /// <para><b>Fast</b>: the gantry parks, the items snap to their slots as the element places them.</para>
    /// <para>Without gantry nodes (no <c>Head</c>) the item itself flies the same path (lift, travel, lower).</para>
    /// <para>The model's mechanics are a <see cref="GantryRig"/> (added to <see cref="gantryRoot"/> if missing), the same a
    /// crane resource drives. In <b>crane mode</b> (the stacker has a pick location: its cycles are done by a crane
    /// resource) this view stands aside and the crane's <see cref="CraneResourceView"/> moves the gantry.</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Gantry Stacker View")]
    public class GantryStackerView : MonoBehaviour, IVisualDetailView, IStackerAnimator
    {
        [Tooltip("The stacker element (empty = on this object or a parent).")]
        public UnityStacker stacker;

        [Tooltip("Root of the gantry model (its GantryRig; Bridge / Trolley / Head are found under it by name).")]
        public Transform gantryRoot;
        private GantryRig rig;

        [Tooltip("Cycle duration (sim time) used when the element places instantaneously.")]
        [Min(0.1f)] public float fallbackCycle = 8f;
        [Tooltip("Shortest / longest animated cycle (sim time).")]
        [Min(0.05f)] public float minCycle = 2f;
        [Min(0.1f)] public float maxCycle = 60f;
        [Tooltip("Cycles waiting beyond this are applied at once (snap).")]
        [Min(0)] public int maxLag = 3;
        [Tooltip("Lowest the head may go below its rest height (m).")]
        [Min(0f)] public float maxDrop = 2.5f;
        [Tooltip("Without gantry: lift height above the higher of pick / place (m).")]
        [Min(0f)] public float clearance = 0.5f;

        // phase ends of a cycle: to pick, down, up, to place, down, release + up
        private static readonly float[] PhaseEnd = { 0.20f, 0.33f, 0.50f, 0.70f, 0.83f, 1.00f };

        private sealed class Move { public Item item; public int position, level; public double duration; }

        private readonly List<Move> queue = new List<Move>();
        private Move current;
        private float t, dur;
        private Vector2 fromPlan; private float fromY;
        private Vector2 plan; private float cupY;            // head plan position (frame x, z) and cup height (frame y)
        private float restCup;
        private bool HasHead => rig != null && rig.HasHead;

        /// <summary>The gantry's mechanics (shared with a crane resource in crane mode).</summary>
        public GantryRig Rig => rig;

        // Crane mode: a crane resource does the cycles (and drives this gantry).
        private bool CraneMode => subscribed != null && subscribed.PickLocation != null;
        private Stacker subscribed;
        private double lastNow = double.NaN;
        private bool detailed = true, started;

        /// <summary>True while the detailed animation runs (cycles are animated).</summary>
        public bool Animating => detailed && started && isActiveAndEnabled && !CraneMode;

        public bool IsAnimating(Item item)
        {
            if (!Animating || item == null) return false;
            if (current != null && current.item == item) return true;
            foreach (var m in queue) if (m.item == item) return true;
            return false;
        }

        /// <summary>Moves animated / snapped since start-up (diagnostics).</summary>
        public int CyclesAnimated { get; private set; }
        public int CyclesSnapped { get; private set; }

        private Transform Frame => rig != null && rig.HasHead ? rig.Frame : (gantryRoot != null ? gantryRoot : transform);

        private void Start()
        {
            if (stacker == null) stacker = GetComponentInParent<UnityStacker>();
            if (gantryRoot == null) gantryRoot = transform;
            rig = gantryRoot.GetComponentInChildren<GantryRig>();
            if (rig == null && FindDeep(gantryRoot, "Head") != null) rig = gantryRoot.gameObject.AddComponent<GantryRig>();
            if (HasHead)
            {
                restCup = rig.RestCup;
                plan = rig.Plan;
                cupY = restCup;
            }
            started = true;
            if (stacker != null) stacker.Animator = this;
            SimVisualDetail.Register(this);
        }

        private void OnDestroy()
        {
            SimVisualDetail.Unregister(this);
            Subscribe(null);
            if (stacker != null && ReferenceEquals(stacker.Animator, this)) stacker.Animator = null;
        }

        private static Transform FindDeep(Transform root, string n)
        {
            if (root == null) return null;
            if (root.name == n) return root;
            foreach (Transform c in root)
            {
                var r = FindDeep(c, n);
                if (r != null) return r;
            }
            return null;
        }

        private void Subscribe(Stacker s)
        {
            if (subscribed == s) return;
            if (subscribed != null) subscribed.CycleStarted -= OnCycleStarted;
            subscribed = s;
            if (subscribed != null) subscribed.CycleStarted += OnCycleStarted;
            queue.Clear(); current = null; lastNow = double.NaN;
        }

        // ── IVisualDetailView ────────────────────────────────────────────────

        public void SetDetailed(bool on)
        {
            if (!started) return;
            if (!on) { FlushAll(); if (!CraneMode) Park(); }
            detailed = on;
            enabled = on;
        }

        public void FastRefresh() { }

        // ── IStackerAnimator ─────────────────────────────────────────────────

        public void Flush(int position)
        {
            if (current != null && current.position == position) { Finish(current); current = null; }
            for (int i = 0; i < queue.Count; i++)
                if (queue[i].position == position) { Finish(queue[i]); queue.RemoveAt(i--); }
        }

        private void FlushAll()
        {
            if (current != null) { Finish(current); current = null; }
            foreach (var m in queue) Finish(m);
            queue.Clear();
        }

        // ── animation ────────────────────────────────────────────────────────

        private void OnCycleStarted(Stacker s, StackerCycle c)
        {
            if (!Animating) return;
            queue.Add(new Move { item = c.Item, position = c.Position, level = c.Level, duration = c.Duration });
        }

        private void Update()
        {
            if (stacker == null) return;
            Subscribe(stacker.Stacker);
            var clock = UnitySimClock.Instance;
            if (subscribed == null || clock == null) return;

            double now = clock.clock.GetSimulationTime();
            float dt = double.IsNaN(lastNow) || subscribed.IsWorkPaused ? 0f : (float)System.Math.Max(0, now - lastNow);
            lastNow = now;

            while (queue.Count > maxLag + 1) { Finish(queue[0]); queue.RemoveAt(0); CyclesSnapped++; }
            if (current == null && queue.Count > 0)
            {
                current = queue[0];
                queue.RemoveAt(0);
                t = 0f;
                float d = current.duration > 0 ? (float)current.duration : fallbackCycle;
                dur = Mathf.Clamp(d, minCycle, Mathf.Max(minCycle, maxCycle)) / (1 + queue.Count);
                fromPlan = plan; fromY = cupY;
                CyclesAnimated++;
            }
            if (current == null) return;

            t += dt / Mathf.Max(0.01f, dur);
            if (t >= 1f) { Finish(current); current = null; return; }
            Pose(current, t);
        }

        private void Pose(Move m, float u)
        {
            var go = m.item?.vItem as GameObject;
            Transform f = Frame;
            float h = stacker.VisualHeight(m.item);

            Vector3 pickTopW = stacker.PickBaseWorld + stacker.transform.up * h;
            Vector3 placeTopW = stacker.SlotWorldPosition(m.position, m.level, m.item) + stacker.transform.up * (h - stacker.PivotOffset(m.item));
            Vector3 pk = f.InverseTransformPoint(pickTopW), pl = f.InverseTransformPoint(placeTopW);
            Vector2 pickP = new Vector2(pk.x, pk.z), placeP = new Vector2(pl.x, pl.z);
            float upY = HasHead ? restCup : Mathf.Max(pk.y, pl.y) + clearance;
            float lowest = HasHead ? restCup - maxDrop : float.MinValue;
            float pickY = Mathf.Max(lowest, Mathf.Min(upY, pk.y));
            float placeY = Mathf.Max(lowest, Mathf.Min(upY, pl.y));

            int phase = 0;
            while (phase < PhaseEnd.Length - 1 && u >= PhaseEnd[phase]) phase++;
            float a = phase == 0 ? 0f : PhaseEnd[phase - 1];
            float s = Ease((u - a) / Mathf.Max(1e-4f, PhaseEnd[phase] - a));
            switch (phase)
            {
                case 0: plan = Vector2.Lerp(fromPlan, pickP, s); cupY = Mathf.Lerp(fromY, upY, s); break;
                case 1: plan = pickP; cupY = Mathf.Lerp(upY, pickY, s); break;
                case 2: plan = pickP; cupY = Mathf.Lerp(pickY, upY, s); break;
                case 3: plan = Vector2.Lerp(pickP, placeP, s); cupY = upY; break;
                case 4: plan = placeP; cupY = Mathf.Lerp(upY, placeY, s); break;
                default: plan = placeP; cupY = Mathf.Lerp(placeY, upY, s); break;
            }
            ApplyHead();

            if (go == null) return;
            if (phase >= 2 && phase <= 4)
            {
                // carried: item top at the cups
                Vector3 cupW = f.TransformPoint(new Vector3(plan.x, cupY, plan.y));
                go.transform.position = cupW - stacker.transform.up * (h - stacker.PivotOffset(m.item));
                go.transform.rotation = Quaternion.Slerp(stacker.PickRotation, stacker.PositionRotation(m.position), phase == 3 ? s : (phase > 3 ? 1f : 0f));
            }
            else if (phase == 5)
                PlaceItem(m);
        }

        private void Finish(Move m)
        {
            PlaceItem(m);
            if (m == current)
            {
                Vector3 p = Frame.InverseTransformPoint(stacker.StackBaseWorld(m.position));
                plan = new Vector2(p.x, p.z);
                if (HasHead) cupY = restCup;
                ApplyHead();
            }
        }

        private void PlaceItem(Move m)
        {
            if (m.item?.vItem is GameObject go && go != null)
                go.transform.SetPositionAndRotation(stacker.SlotWorldPosition(m.position, m.level, m.item), stacker.PositionRotation(m.position));
        }

        private void ApplyHead()
        {
            if (!HasHead) return;
            rig.SetHead(plan, cupY);
        }

        private void Park()
        {
            if (!HasHead) return;
            rig.Park();
            plan = rig.Plan; cupY = restCup;
        }

        private static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
    }
}
