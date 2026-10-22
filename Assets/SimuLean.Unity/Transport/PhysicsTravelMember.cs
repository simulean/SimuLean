using UnityEngine;
using UnityEngine.AI;
using SimuLean.Resources;
using SimuLean.Transport;

namespace SimuLean.Unity
{
    /// <summary>
    /// Something that moves a resource in the scene with Unity's physics / navigation. Implement it
    /// on a member prefab to use your own controller (rigidbody, character controller…); otherwise a
    /// <see cref="NavMeshAgent"/> is used.
    /// </summary>
    public interface IPhysicsMover
    {
        /// <summary>Start moving to <paramref name="target"/> at <paramref name="speed"/> (world units per real second).</summary>
        void MoveTo(Vector3 target, float speed);
        /// <summary>True once the target is reached.</summary>
        bool Arrived { get; }
        /// <summary>Freeze / release the motion (the resource is stopped, or the clock).</summary>
        void Halt(bool halt);
        /// <summary>Put it at <paramref name="position"/> at once (start, time-out).</summary>
        void Warp(Vector3 position);
    }

    /// <summary>
    /// A member of a team with <b>Unity Physics</b> travel: when the engine starts an open-ended move
    /// (<see cref="ExternalTravel"/>), the member is sent to the destination node by its <see cref="IPhysicsMover"/>
    /// (a <see cref="NavMeshAgent"/> by default — the scene needs a baked NavMesh — or a component of the prefab), and
    /// when it gets there <see cref="TaskExecutor.Arrive"/> tells the engine: Unity decides how long the move takes.
    /// Speed in the scene = team speed × the clock's time scale, so sim time and motion agree; stopped resources and a
    /// paused clock halt it. A move longer than <see cref="timeoutFactor"/> × its estimated time is ended with a warning.
    /// Carried items ride at <see cref="carriedOffset"/>. Scene only (the moves feed calibration, never headless runs).
    /// </summary>
    public class PhysicsTravelMember : MonoBehaviour
    {
        public Vector3 carriedOffset = new Vector3(0f, 0.75f, 0f);
        [Min(1f)] public float timeoutFactor = 5f;

        private TaskExecutor executor;
        private TransportNetwork points;
        private float simSpeed;
        private IPhysicsMover mover;
        private TravelLeg commanded;
        private bool warned;

        public TaskExecutor Executor => executor;

        public void Bind(TaskExecutor executor, TransportNetwork points, float simSpeed)
        {
            this.executor = executor;
            this.points = points;
            this.simSpeed = simSpeed;
            mover = GetComponent<IPhysicsMover>();
            if (mover == null || (mover is Object o && o == null)) mover = gameObject.AddComponent<NavMeshMover>();
            if (Node(executor.Location ?? executor.HomeNode, out Vector3 home)) mover.Warp(home);
        }

        private void Update()
        {
            var sim = UnitySimClock.Instance;
            if (executor == null || sim == null) return;
            double now = sim.clock.GetSimulationTime();
            float scale = (float)sim.TimeScale;
            // a paused or finished run halts the body too 
            // (it walked on while paused, so its travel was recorded short)
            mover.Halt(executor.IsWorkPaused || !sim.SimStarted || sim.SimPaused || !sim.SimOn || scale <= 0);

            var leg = executor.CurrentLeg;
            if (leg != null && leg.IsOpenEnded)
            {
                if (!ReferenceEquals(leg, commanded) && Node(leg.To, out Vector3 target))
                {
                    commanded = leg;
                    float speed = simSpeed * Mathf.Max(scale, 0.0001f);
                    if (speed > 40f && !warned)
                    {
                        warned = true;
                        Debug.LogWarning($"[SimuLean] '{executor.GetName()}' moves at {speed:F0} units/s in the scene (speed × time scale): " +
                                         "physics loses accuracy that fast — lower the clock's time scale for calibration runs.");
                    }
                    mover.MoveTo(target, speed);
                }
                if (ReferenceEquals(leg, commanded) && mover.Arrived)
                {
                    commanded = null;
                    executor.Arrive();
                }
                else if (ReferenceEquals(leg, commanded) && simSpeed > 0 &&
                         now - leg.StartTime > timeoutFactor * Mathf.Max(1f, (float)(leg.Distance / simSpeed)))
                {
                    Debug.LogWarning($"[SimuLean] '{executor.GetName()}' did not reach '{leg.To}' in {timeoutFactor}× its estimated time: ended there.");
                    if (Node(leg.To, out Vector3 t)) mover.Warp(t);
                    commanded = null;
                    executor.Arrive();
                }
            }
            foreach (var item in executor.Carried)
                if (item?.vItem is GameObject go && go != null) go.transform.position = transform.position + carriedOffset;
        }

        private bool Node(string id, out Vector3 p)
        {
            p = default;
            TransportNode n = id != null ? points?.GetNode(id) : null;
            if (n == null) return false;
            p = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
            return true;
        }
    }
}
