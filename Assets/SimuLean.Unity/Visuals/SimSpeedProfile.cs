using SimuLean.Downtime;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Playback speed by the model's calendar: the scene clock runs at <see cref="openSpeed"/>
    /// during <see cref="openHours"/> (clinic hours, a shift) and at <see cref="closedSpeed"/> otherwise (nights, weekends
    /// fly by), and — optionally — slows to <see cref="walkingSpeed"/> while a resource walks, for at most
    /// <see cref="maxSlowSeconds"/> of real time per walk (a 12 m walk would otherwise last one frame). It only sets
    /// <see cref="UnitySimClock.TimeScale"/>: the run is the same at any speed. Pairs with the <see cref="SimRecorder"/>.
    /// The user's speed wins: once the user changes it, the profile steps aside until <see cref="ResumeAuto"/>.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Visualization, "Speed Profile",
        Description = "Playback speed by the calendar (clinic hours slower than nights) and slow motion while staff walk.", Order = 50)]
    [AddComponentMenu("SimuLean/Speed Profile")]
    public sealed class SimSpeedProfile : MonoBehaviour
    {
        [Tooltip("When the model is 'open' (weekly pattern on the model calendar), e.g. Mon-Fri 08:00-15:00.")]
        public string openHours = "Mon-Fri 08:00-15:00";
        [Tooltip("Model time units per second while open (e.g. 0.08 h/s ≈ 5 minutes per second when the model counts hours).")]
        [Min(0.000001f)] public float openSpeed = 0.08f;
        [Tooltip("Model time units per second while closed (nights, weekends).")]
        [Min(0.000001f)] public float closedSpeed = 40f;
        [Tooltip("Slow down while a resource (staff, a vehicle) walks or travels.")]
        public bool slowWhenWalking = true;
        [Tooltip("Model time units per second while someone walks.")]
        [Min(0.000001f)] public float walkingSpeed = 0.004f;
        [Tooltip("At most this long (real seconds) in slow motion per walk.")]
        [Min(0f)] public float maxSlowSeconds = 4f;

        private WeeklyShiftPattern pattern;
        private string parsed;
        private float slowFor;

        /// <summary>The speed for a model date, whether someone walks and how long the slow motion has lasted.</summary>
        public float SpeedAt(System.DateTime date, bool walking, float slowSeconds)
        {
            if (slowWhenWalking && walking && slowSeconds < maxSlowSeconds) return walkingSpeed;
            return IsOpen(date) ? openSpeed : closedSpeed;
        }

        /// <summary>True when <paramref name="date"/> is in the open hours (an unreadable pattern: always open).</summary>
        public bool IsOpen(System.DateTime date)
        {
            if (parsed != openHours)
            {
                parsed = openHours;
                try { pattern = string.IsNullOrWhiteSpace(openHours) ? null : WeeklyShiftPattern.Parse(openHours); }
                catch (System.FormatException e) { pattern = null; Debug.LogWarning($"[SimuLean] Speed Profile: open hours '{openHours}': {e.Message}"); }
            }
            return pattern == null || pattern.IsWorking(date);
        }

        /// <summary>True while a resource member walks or travels (its view moves along a leg), or an item walks by itself.</summary>
        public static bool SomeoneWalks()
        {
            if (ItemWalkView.AnyWalking) return true;
            var animators = UnityVehicleAnimator.Active;
            for (int i = 0; i < animators.Count; i++)
                if (animators[i].Executor != null && animators[i].Executor.CurrentLeg != null) return true;
            return false;
        }

        // the speed it set last (NaN: none yet): another value means the user changed the speed
        private double lastSet = double.NaN;

        /// <summary>The user changed the speed (the HUD's buttons, the camera's + / − keys): the profile steps aside and the
        /// user's speed stays — the clock still caps it at what the machine can run. <see cref="ResumeAuto"/> (the HUD's
        /// <i>Auto</i> button) gives the speed back to the profile.</summary>
        public bool Manual { get; private set; }

        /// <summary>The profile sets the speed again.</summary>
        public void ResumeAuto() { Manual = false; lastSet = double.NaN; }

        /// <summary>The active profile of the scene (null without one).</summary>
        public static SimSpeedProfile Active { get; private set; }

        private void OnEnable() => Active = this;
        private void OnDisable() { if (Active == this) Active = null; }

        /// <summary>One frame of the profile: the speed to set now, or null to leave the clock's (manual, or unchanged).
        /// A clock speed other than the last one set means the user changed it (<see cref="Manual"/>).</summary>
        public double? Next(double current, System.DateTime date, bool walking, float dt)
        {
            if (!double.IsNaN(lastSet) && System.Math.Abs(current - lastSet) > 1e-9) Manual = true;
            if (Manual) return null;
            slowFor = walking ? slowFor + dt : 0f;
            double speed = SpeedAt(date, walking, slowFor);
            lastSet = speed;
            return System.Math.Abs(current - speed) > 1e-9 ? speed : (double?)null;
        }

        private void LateUpdate()
        {
            var sim = UnitySimClock.Instance;
            if (sim == null || sim.clock == null) return;
            if (SimRecorder.Approaching) { lastSet = double.NaN; return; }   // the recorder drives the clock to its start
            double? speed = Next(sim.TimeScale, sim.clock.GetDateTime(), slowWhenWalking && SomeoneWalks(), Time.deltaTime);
            if (speed.HasValue) sim.TimeScale = speed.Value;
        }
    }
}
