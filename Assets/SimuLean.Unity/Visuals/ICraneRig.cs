using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The mechanics of a crane model that <see cref="CraneResourceView"/> drives for a crane resource: the head's plan
    /// position (frame x / z: trolley along the bridge = x, bridge along the runway = z) and its cup height (frame y,
    /// where a load's top hangs). Implemented by the library's gantry portal (<see cref="GantryRig"/>), the hall overhead
    /// crane (<see cref="UnityOverheadCrane"/>) and the travelling-leg gantries (<see cref="TravellingGantryRig"/>: Goliath crane,
    /// welding gantry). A member prefab carrying one is driven as a crane (<see cref="UnityResourceTeam"/>). View only.
    /// </summary>
    public interface ICraneRig
    {
        /// <summary>Whether the model has a head to drive.</summary>
        bool HasHead { get; }

        /// <summary>Cup height at rest = the highest (frame y).</summary>
        float RestCup { get; }

        /// <summary>Head position now: plan (frame x, z).</summary>
        Vector2 Plan { get; }

        /// <summary>Cup position in world space.</summary>
        Vector3 CupWorld { get; }

        /// <summary>World point → plan (frame x, z) / height (frame y).</summary>
        Vector2 PlanOf(Vector3 world);
        float HeightOf(Vector3 world);

        /// <summary>Poses the head: plan (frame x, z) and cup height (frame y), clamped to the travel limits.</summary>
        void SetHead(Vector2 plan, float cup);

        /// <summary>Sizes and places the crane so its head reaches every world point given (with a margin).</summary>
        void FitAround(IEnumerable<Vector3> worldPoints, float margin = 1f);
    }
}
