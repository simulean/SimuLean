using UnityEngine;
using UnityEngine.AI;

namespace SimuLean.Unity
{
    /// <summary>Default <see cref="IPhysicsMover"/>: a <see cref="NavMeshAgent"/> (added if missing).</summary>
    public class NavMeshMover : MonoBehaviour, IPhysicsMover
    {
        [Tooltip("Distance at which the target counts as reached.")]
        [Min(0.01f)] public float arriveDistance = 0.3f;

        private NavMeshAgent agent;
        private bool moving;

        private NavMeshAgent Agent
        {
            get
            {
                if (agent == null) agent = GetComponent<NavMeshAgent>();
                if (agent == null) agent = gameObject.AddComponent<NavMeshAgent>();
                return agent;
            }
        }

        public void MoveTo(Vector3 target, float speed)
        {
            var a = Agent;
            a.speed = speed;
            a.acceleration = Mathf.Max(8f, speed * 4f);
            a.angularSpeed = 720f;
            a.stoppingDistance = arriveDistance * 0.5f;
            if (!a.isOnNavMesh && NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 5f, NavMesh.AllAreas)) a.Warp(hit.position);
            if (a.isOnNavMesh) { a.SetDestination(target); moving = true; }
            else { Debug.LogWarning($"[SimuLean] '{name}' is not on a NavMesh: bake one (AI Navigation ▸ NavMesh Surface). Moved directly."); transform.position = target; moving = false; }
        }

        public bool Arrived
        {
            get
            {
                if (!moving) return true;
                var a = Agent;
                if (!a.isOnNavMesh || a.pathPending) return false;
                bool there = a.remainingDistance <= Mathf.Max(arriveDistance, a.stoppingDistance);
                if (there) moving = false;
                return there;
            }
        }

        public void Halt(bool halt) { var a = Agent; if (a.isOnNavMesh) a.isStopped = halt; }

        public void Warp(Vector3 position)
        {
            var a = Agent;
            if (NavMesh.SamplePosition(position, out NavMeshHit hit, 5f, NavMesh.AllAreas)) a.Warp(hit.position);
            else transform.position = position;
            moving = false;
        }
    }
}
