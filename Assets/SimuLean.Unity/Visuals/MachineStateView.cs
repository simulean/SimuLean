using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The state view of a library station model (paint booth, CNC, palletizer, reception desk…): the stack light /
    /// status panel shows the element's state, and the model's named parts move while it works. Read-only: it only
    /// reads <see cref="Element.CurrentState"/> of the <see cref="SElement"/> it sits on (or under) and moves
    /// transforms of the model, so runs are unchanged.
    ///
    /// <para><b>Lamps</b> (<c>Beacon</c> › <c>Lamp_Green</c> / <c>Lamp_Amber</c> / <c>Lamp_Red</c>, one renderer per lamp
    /// and LOD): green while the element works (PROCESSING, SETUP), red while it is down (STOPPED, BREAKDOWN,
    /// SCHEDULED_DOWN, OFF_SHIFT), amber otherwise (IDLE, RECEIVING, BLOCKED, WAITING_FOR_RESOURCE). Lit by a
    /// <see cref="MaterialPropertyBlock"/> (the atlas material stays shared); before a run the baked look shows all
    /// lamps lit.</para>
    ///
    /// <para><b>Motions</b>, found by node name (pivots and axes in each model's script docstring,
    /// <c>Tools/Blender/assets</c>): <c>Spindle</c> / <c>Turntable</c> spin, <c>Head</c> / <c>Table</c> /
    /// <c>Tilt</c> / <c>Screwdriver</c> cycle, <c>Lights</c> flash while working; doors (<c>Door_L/R</c> slide,
    /// <c>Door_In/Out</c> roll up) and the <c>Curtain</c> are open while the element waits and closed while it
    /// works; the palletizer's <c>Slew</c> › <c>Lift</c> › <c>Reach</c> run a pick-and-place cycle. Motions run on
    /// screen time while the simulation clock advances.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("SimuLean/Visuals/Machine State View")]
    public class MachineStateView : MonoBehaviour
    {
        [Tooltip("Emission of the lit lamp.")]
        public float litIntensity = 2.5f;
        [Tooltip("Emission of the lamps that are off.")]
        public float offIntensity = 0.05f;
        [Tooltip("Speed of the working motions (1 = the models' nominal cycle).")]
        public float motionSpeed = 1f;

        public enum Lamp { None, Green, Amber, Red }

        private SElement element;
        private readonly List<Renderer>[] lamps = { new List<Renderer>(), new List<Renderer>(), new List<Renderer>() };
        private readonly List<Motion> motions = new List<Motion>();
        private MaterialPropertyBlock block;
        private Lamp shown = Lamp.None;
        private double lastSimTime = double.NaN;
        private float phase;
        private bool bound;

        /// <summary>The lamp for an element state name (null = no state yet).</summary>
        public static Lamp LampFor(string state)
        {
            switch (state)
            {
                case null: return Lamp.None;
                case "PROCESSING": case "SETUP": return Lamp.Green;
                case "STOPPED": case "BREAKDOWN": case "SCHEDULED_DOWN": case "OFF_SHIFT": return Lamp.Red;
                default: return Lamp.Amber;
            }
        }

        /// <summary>True for the states in which the station's parts move.</summary>
        public static bool Working(string state) => state == "PROCESSING" || state == "SETUP";

        private void OnEnable() => bound = false;

        private void OnDisable()
        {
            foreach (var m in motions) m.Reset();
            ShowLamp(Lamp.None);
        }

        private void Bind()
        {
            bound = true;
            element = GetComponentInParent<SElement>();
            foreach (var l in lamps) l.Clear();
            motions.Clear();
            var nodes = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (!nodes.ContainsKey(t.name)) nodes[t.name] = t;
            string[] lampNodes = { "Lamp_Green", "Lamp_Amber", "Lamp_Red" };
            for (int i = 0; i < 3; i++)
                if (nodes.TryGetValue(lampNodes[i], out var n))
                    lamps[i].AddRange(n.GetComponentsInChildren<Renderer>(true));
            Motion.Build(nodes, motions);
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            if (!bound) Bind();
            var e = element != null ? element.GetElement() : null;
            string state = e?.CurrentState.Name;
            var lamp = LampFor(state);
            if (lamp != shown) ShowLamp(lamp);

            // motions advance while the simulation clock does (paused clock = still machine)
            var clock = UnitySimClock.Instance;
            double now = clock != null && clock.clock != null ? clock.clock.GetSimulationTime() : double.NaN;
            bool advancing = !double.IsNaN(now) && !double.IsNaN(lastSimTime) && now > lastSimTime;
            lastSimTime = now;
            bool working = Working(state);
            float dt = advancing ? Time.deltaTime * motionSpeed : 0f;
            if (working) phase += dt;
            foreach (var m in motions) m.Update(working, phase, dt);
        }

        private void ShowLamp(Lamp lamp)
        {
            shown = lamp;
            if (block == null) block = new MaterialPropertyBlock();
            for (int i = 0; i < 3; i++)
            {
                bool on = lamp == (Lamp)(i + 1);
                foreach (var r in lamps[i])
                {
                    if (r == null) continue;
                    if (lamp == Lamp.None) { r.SetPropertyBlock(null); continue; }
                    r.GetPropertyBlock(block);
                    block.SetColor("_EmissionColor", Color.white * (on ? litIntensity : offIntensity));
                    block.SetColor("_Color", on ? Color.white : new Color(0.35f, 0.35f, 0.35f));
                    r.SetPropertyBlock(block);
                }
            }
        }

        // ------------------------------------------------------------------ motions

        private abstract class Motion
        {
            protected readonly Transform t;
            protected readonly Vector3 pos0, scale0;
            protected readonly Quaternion rot0;
            protected float open = 1f;          // doors: 1 = open, eased towards the target

            protected Motion(Transform t) { this.t = t; pos0 = t.localPosition; rot0 = t.localRotation; scale0 = t.localScale; }

            public abstract void Update(bool working, float phase, float dt);
            public void Reset() { if (t == null) return; t.localPosition = pos0; t.localRotation = rot0; t.localScale = scale0; }

            protected float Ease(bool openWanted, float dt, float seconds = 1.2f)
            {
                open = Mathf.MoveTowards(open, openWanted ? 1f : 0f, dt / seconds);
                return Mathf.SmoothStep(0f, 1f, open);
            }

            public static void Build(Dictionary<string, Transform> n, List<Motion> list)
            {
                void Add(string name, System.Func<Transform, Motion> make) { if (n.TryGetValue(name, out var t)) list.Add(make(t)); }
                Add("Spindle", t => new Spin(t, 1440f));
                Add("Turntable", t => new Spin(t, 25f));
                Add("Head", t => new Cycle(t, Vector3.up * -0.12f, 2.0f));
                Add("Table", t => new Cycle(t, Vector3.right * 0.15f, 3.0f, true));
                Add("Screwdriver", t => new Cycle(t, Vector3.up * -0.30f, 4.0f));
                Add("Tilt", t => new Swing(t, Vector3.right, 30f, 8.0f));
                Add("Lights", t => new Flash(t, 1.5f));
                Add("Door_L", t => new Slide(t, Vector3.right * -0.38f));
                Add("Door_R", t => new Slide(t, Vector3.right * 0.38f));
                Add("Door_In", t => new Roll(t, 1));
                Add("Door_Out", t => new Roll(t, 1));
                Add("Curtain", t => new Roll(t, 0));
                if (n.TryGetValue("Slew", out var slew))
                    list.Add(new PickPlace(slew, n.TryGetValue("Lift", out var lift) ? lift : null));
            }
        }

        private class Spin : Motion
        {
            private readonly float degPerSec;
            public Spin(Transform t, float degPerSec) : base(t) { this.degPerSec = degPerSec; }
            public override void Update(bool working, float phase, float dt)
            {
                if (working) t.localRotation *= Quaternion.AngleAxis(degPerSec * dt, Vector3.up);
            }
        }

        // a to-and-fro along `offset` (one period), back at rest when idle; `both` swings to both sides
        private class Cycle : Motion
        {
            private readonly Vector3 offset; private readonly float period; private readonly bool both;
            public Cycle(Transform t, Vector3 offset, float period, bool both = false) : base(t)
            { this.offset = offset; this.period = period; this.both = both; }
            public override void Update(bool working, float phase, float dt)
            {
                float a = 2f * Mathf.PI * phase / period;
                float k = working ? (both ? Mathf.Sin(a) : 0.5f - 0.5f * Mathf.Cos(a)) : 0f;
                t.localPosition = Vector3.Lerp(t.localPosition, pos0 + offset * k, working ? 1f : Mathf.Min(1f, dt * 4f));
            }
        }

        private class Swing : Motion
        {
            private readonly Vector3 axis; private readonly float degrees, period;
            public Swing(Transform t, Vector3 axis, float degrees, float period) : base(t)
            { this.axis = axis; this.degrees = degrees; this.period = period; }
            public override void Update(bool working, float phase, float dt)
            {
                float k = working ? Mathf.Sin(2f * Mathf.PI * phase / period) : 0f;
                t.localRotation = rot0 * Quaternion.AngleAxis(degrees * k, axis);
            }
        }

        private class Flash : Motion
        {
            private readonly Renderer[] rends; private readonly float period;
            private readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
            public Flash(Transform t, float period) : base(t) { rends = t.GetComponentsInChildren<Renderer>(true); this.period = period; }
            public override void Update(bool working, float phase, float dt)
            {
                bool on = working && (phase % period) < 0.25f;
                foreach (var r in rends)
                {
                    r.GetPropertyBlock(mpb);
                    mpb.SetColor("_EmissionColor", Color.white * (on ? 4f : 0.6f));
                    r.SetPropertyBlock(mpb);
                }
            }
        }

        // sliding door: open (offset) while waiting, closed while working
        private class Slide : Motion
        {
            private readonly Vector3 offset;
            public Slide(Transform t, Vector3 offset) : base(t) { this.offset = offset; }
            public override void Update(bool working, float phase, float dt)
            {
                if (dt <= 0f) return;
                t.localPosition = pos0 + offset * Ease(!working, dt);
            }
        }

        // roll-up shutter (scale local Y, axis 1) or curtain (scale local X, axis 0): open = 5 % / 15 %
        private class Roll : Motion
        {
            private readonly int axis;
            public Roll(Transform t, int axis) : base(t) { this.axis = axis; }
            public override void Update(bool working, float phase, float dt)
            {
                if (dt <= 0f) return;
                float k = Ease(!working, dt);
                float closedToOpen = axis == 1 ? 0.05f : 0.15f;
                var s = scale0;
                s[axis] = Mathf.Lerp(scale0[axis], scale0[axis] * closedToOpen, k);
                t.localScale = s;
            }
        }

        // the palletizer: slew to the infeed (180 deg), dip, slew back to the pallet, dip — one cycle per 8 s
        private class PickPlace : Motion
        {
            private readonly Transform lift; private readonly Vector3 lift0;
            public PickPlace(Transform slew, Transform lift) : base(slew)
            { this.lift = lift; if (lift != null) lift0 = lift.localPosition; }
            public override void Update(bool working, float phase, float dt)
            {
                if (!working) { if (dt > 0f) { t.localRotation = Quaternion.Slerp(t.localRotation, rot0, dt * 2f); } return; }
                float u = (phase % 8f) / 8f;                                         // 0..1 over the cycle
                float yaw = 180f * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u < 0.5f ? u : 1f - u) * 4f - 0.5f));
                t.localRotation = rot0 * Quaternion.AngleAxis(yaw, Vector3.up);
                if (lift != null)
                {
                    float dip = Mathf.Max(0f, Mathf.Sin(2f * Mathf.PI * u * 2f + Mathf.PI)) * 0.5f;
                    lift.localPosition = lift0 + Vector3.up * -dip;
                }
            }
        }
    }
}
