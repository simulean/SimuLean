using System.Collections.Generic;
using UnityEngine;
using SimuLean.Resources;
using SimuLean.Transport;

namespace SimuLean.Unity
{
    /// <summary>
    /// View of a crane resource (a <see cref="TaskExecutor"/> with axis travel) on the library's
    /// crane model through its <see cref="ICraneRig"/> — the library's gantry (<see cref="GantryRig"/>, the same gantry a
    /// stacker animates by itself when it cycles by time), the hall overhead crane or a travelling-leg gantry (Goliath,
    /// welding gantry: <see cref="TravellingGantryRig"/>). Driven by the engine, never changing it:
    /// <list type="bullet">
    /// <item><b>Travel:</b> the head travels with the travel leg; with <see cref="AxisTravel"/> each horizontal axis follows
    /// its own speed / acceleration profile (the times the engine computed), so the bridge and the trolley start
    /// together and each arrives when its axis does. The head stays up.</item>
    /// <item><b>Load / unload:</b> within the step's time the head goes down to the item (load: its top where it lies;
    /// unload: where it will rest), grips / releases it in the middle and goes back up.</item>
    /// <item>The item hangs from the head from the grip until the release.</item>
    /// </list>
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Crane Resource View")]
    public class CraneResourceView : MonoBehaviour
    {
        [Tooltip("Share of a load / unload spent going down (the same going up; the grip / release is in between).")]
        [Range(0.1f, 0.45f)] public float lowerShare = 0.4f;

        private TaskExecutor executor;
        private TransportNetwork points;
        private ICraneRig rig;
        private float lastU;

        public TaskExecutor Executor => executor;
        public ICraneRig Rig => rig;

        public void Bind(TaskExecutor executor, TransportNetwork points, ICraneRig rig)
        {
            this.executor = executor;
            this.points = points;
            this.rig = rig;
            if (Node(executor.Location ?? executor.HomeNode, out Vector3 home)) rig.SetHead(rig.PlanOf(home), rig.RestCup);
        }

        private void Update()
        {
            if (executor == null || rig == null || !rig.HasHead || UnitySimClock.Instance == null) return;
            double now = UnitySimClock.Instance.clock.GetSimulationTime();

            // Horizontal: along the leg (axis profiles) or at its location.
            Vector2 plan = rig.Plan;
            var leg = executor.CurrentLeg;
            if (leg != null) plan = rig.PlanOf(AlongLeg(leg, now));
            else if (Node(executor.Location, out Vector3 at)) plan = rig.PlanOf(at);

            // Vertical: up, except while loading / unloading.
            float cup = rig.RestCup;
            var step = executor.CurrentStep;
            Item gripped = null;
            bool releasedStepItem = false;
            if (step != null && (step.Kind == StepKind.Load || step.Kind == StepKind.Unload))
            {
                float u = StepFraction(now);
                float target = TargetCup(step, at: Node(executor.Location, out Vector3 here) ? here : rig.CupWorld);
                float down = lowerShare, up = 1f - lowerShare;
                cup = u < down ? Mathf.Lerp(rig.RestCup, target, Ease(u / down))
                    : u < up ? target
                    : Mathf.Lerp(target, rig.RestCup, Ease((u - up) / (1f - up)));
                float grip = 0.5f;
                if (step.Kind == StepKind.Load && u >= grip) gripped = step.Item;
                if (step.Kind == StepKind.Unload && u >= grip) releasedStepItem = true;
            }
            rig.SetHead(plan, cup);

            Vector3 cupW = rig.CupWorld;
            foreach (var item in executor.Carried)
                if (!(releasedStepItem && item == step.Item)) Hang(item, cupW);
            if (gripped != null && !Contains(executor.Carried, gripped)) Hang(gripped, cupW);
        }

        // The head height (frame y) for the step: the item's top where it lies (load) or where it will rest (unload).
        private float TargetCup(TaskStep step, Vector3 at)
        {
            var go = step.Item?.vItem as GameObject;
            if (step.Kind == StepKind.Load && go != null && !Contains(executor.Carried, step.Item))
                return rig.HeightOf(new Vector3(at.x, Top(go), at.z));
            float h = go != null ? Top(go) - Bottom(go) : 0.5f;
            return rig.HeightOf(at) + h;
        }

        // Only the plan (x / z) is used: the head stays up while travelling.
        private Vector3 AlongLeg(TravelLeg leg, double now)
        {
            Node(leg.From, out Vector3 a);
            Node(leg.To, out Vector3 b);
            return AxisLegPath.Along(executor.TravelModel, leg, a, b, now);
        }

        private float StepFraction(double now)
        {
            double a = executor.StepStartTime, b = executor.StepEndTime;
            if (executor.IsWorkPaused) return lastU;
            if (double.IsNaN(a) || double.IsNaN(b) || b <= a) return lastU = 1f;
            return lastU = Mathf.Clamp01((float)((now - a) / (b - a)));
        }

        // The item hangs under the head: its top at the cup.
        private void Hang(Item item, Vector3 cupWorld)
        {
            if (!(item?.vItem is GameObject go) || go == null) return;
            float topAbovePivot = Top(go) - go.transform.position.y;
            go.transform.position = cupWorld - Vector3.up * topAbovePivot;
        }

        private bool Node(string id, out Vector3 p)
        {
            p = default;
            TransportNode n = id != null ? points?.GetNode(id) : null;
            if (n == null) return false;
            p = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
            return true;
        }

        private static bool Contains(IReadOnlyList<Item> list, Item it) { foreach (var x in list) if (x == it) return true; return false; }

        private static float Top(GameObject go)
        {
            float max = float.MinValue; bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { max = Mathf.Max(max, r.bounds.max.y); any = true; }
            return any ? max : go.transform.position.y;
        }

        private static float Bottom(GameObject go)
        {
            float min = float.MaxValue; bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { min = Mathf.Min(min, r.bounds.min.y); any = true; }
            return any ? min : go.transform.position.y;
        }

        private static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
    }
}
