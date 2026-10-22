using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>Animation level of machine views (gantries, turners, robots…).</summary>
    public enum VisualDetailMode
    {
        /// <summary>Detailed while the simulation speed is ≤ <see cref="SimVisualDetail.autoThreshold"/>, fast above it
        /// and at "maximum speed".</summary>
        Auto,
        /// <summary>Full kinematics (e.g. a gantry runs its pick-and-place cycle).</summary>
        Detailed,
        /// <summary>No kinematics: moving parts parked, results snap into place (cheap low-rate refresh only).</summary>
        Fast,
    }

    /// <summary>A view animated according to <see cref="SimVisualDetail"/> (register it with
    /// <see cref="SimVisualDetail.Register"/> in its <c>Start</c>, unregister in <c>OnDestroy</c>).</summary>
    public interface IVisualDetailView
    {
        /// <summary>Switches between the detailed animation (true) and the fast mode (false: park the moving parts
        /// and snap; a view may disable itself to cost nothing per frame).</summary>
        void SetDetailed(bool detailed);

        /// <summary>Fast mode only: called at <see cref="SimVisualDetail.fastRefreshHz"/> for cheap updates
        /// (only when something changed).</summary>
        void FastRefresh();
    }

    /// <summary>
    /// Scene-wide "detailed / fast" animation setting of the machine views. Views (e.g.
    /// <see cref="GantryStackerView"/>) register themselves; this component tells them which mode applies
    /// and calls <see cref="IVisualDetailView.FastRefresh"/> at a low rate in fast mode. <see cref="VisualDetailMode.Auto"/>
    /// (default) is detailed while <see cref="UnitySimClock.TimeScale"/> ≤ <see cref="autoThreshold"/> and fast above it
    /// or when the clock runs at maximum speed — so a fast-forward never pays for kinematics nobody can follow.
    /// <para><b>Visual only:</b> views read the simulation (element events / state) and never change it, so a run
    /// produces the same results in every mode.</para>
    /// <para>Optional: if no instance exists when the first view registers, one is created at run time with the
    /// defaults (Auto, threshold ×60). Controls: the inspector, <see cref="Cycle"/> / <see cref="Mode"/> from a HUD,
    /// <see cref="cycleKey"/> (default none), and the command line <c>-visual-detail auto|detailed|fast</c>.</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Visual Detail")]
    [DefaultExecutionOrder(-60)]
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Visualization, "Visual Detail", Description = "Auto / Detailed / Fast visuals for large models", Singleton = true, Order = 2)]
    public class SimVisualDetail : MonoBehaviour
    {
        [Tooltip("Auto = detailed up to the threshold speed, fast above it (and at maximum speed).")]
        public VisualDetailMode mode = VisualDetailMode.Auto;

        [Tooltip("Auto mode: highest simulation speed (sim time units per real second) that still shows the detailed animation.")]
        public double autoThreshold = 60;

        [Tooltip("Fast mode: refreshes per real second sent to the views.")]
        public float fastRefreshHz = 4f;

        [Tooltip("Key that cycles Auto → Detailed → Fast (None = no key).")]
        public KeyCode cycleKey = KeyCode.None;

        public static SimVisualDetail Instance { get; private set; }

        /// <summary>True when the detailed animation is active now (mode, or Auto and the current speed).</summary>
        public bool EffectiveDetailed { get; private set; } = true;

        /// <summary>Current mode (settable at run time, e.g. from a HUD).</summary>
        public VisualDetailMode Mode { get => mode; set => mode = value; }

        /// <summary>Raised when the effective mode changes (true = detailed).</summary>
        public event Action<bool> DetailChanged;

        private static readonly List<IVisualDetailView> views = new List<IVisualDetailView>();
        private bool applied, appliedDetailed;
        private float nextFast;

        // ── registration ─────────────────────────────────────────────────────

        /// <summary>Registers a view and applies the current mode to it. Without a manager in the scene the views stay
        /// detailed: nothing is created at run time (the scene set-up adds the manager).</summary>
        public static void Register(IVisualDetailView view)
        {
            if (view == null) return;
            if (!views.Contains(view)) views.Add(view);
            if (Instance != null && Instance.applied) view.SetDetailed(Instance.appliedDetailed);
        }

        public static void Unregister(IVisualDetailView view) => views.Remove(view);

        /// <summary>True when the detailed animation should run now (no manager = detailed).</summary>
        public static bool DetailedNow => Instance == null || Instance.EffectiveDetailed;

        /// <summary>Auto → Detailed → Fast → Auto.</summary>
        public void Cycle() => mode = mode == VisualDetailMode.Auto ? VisualDetailMode.Detailed
                                    : mode == VisualDetailMode.Detailed ? VisualDetailMode.Fast : VisualDetailMode.Auto;

        /// <summary>Parses <c>auto</c> / <c>detailed</c> / <c>fast</c> (case-insensitive).</summary>
        public static bool TryParseMode(string text, out VisualDetailMode m)
            => Enum.TryParse((text ?? "").Trim(), true, out m) && Enum.IsDefined(typeof(VisualDetailMode), m);

        // ── lifecycle ────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { enabled = false; return; }
            Instance = this;
            var a = Environment.GetCommandLineArgs();
            for (int i = 1; i < a.Length - 1; i++)
            {
                string k = a[i].TrimStart('-').ToLowerInvariant();
                if (k == "visual-detail" && TryParseMode(a[i + 1], out var m)) mode = m;
                else if (k == "visual-detail-threshold"
                         && double.TryParse(a[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t > 0)
                    autoThreshold = t;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (cycleKey != KeyCode.None && Input.GetKeyDown(cycleKey)) Cycle();

            bool detailed;
            switch (mode)
            {
                case VisualDetailMode.Detailed: detailed = true; break;
                case VisualDetailMode.Fast: detailed = false; break;
                default:
                    var clock = UnitySimClock.Instance;
                    detailed = clock == null || (!clock.RunAsFastAsPossible && clock.TimeScale <= autoThreshold + 1e-9);
                    break;
            }
            EffectiveDetailed = detailed;
            if (!applied || detailed != appliedDetailed)
            {
                applied = true;
                appliedDetailed = detailed;
                for (int i = views.Count - 1; i >= 0; i--)
                {
                    if (views[i] == null || (views[i] is UnityEngine.Object o && o == null)) { views.RemoveAt(i); continue; }
                    views[i].SetDetailed(detailed);
                }
                nextFast = 0;
                DetailChanged?.Invoke(detailed);
            }
            if (!detailed && Time.unscaledTime >= nextFast)
            {
                nextFast = Time.unscaledTime + 1f / Mathf.Max(0.5f, fastRefreshHz);
                for (int i = 0; i < views.Count; i++) views[i]?.FastRefresh();
            }
        }
    }
}
