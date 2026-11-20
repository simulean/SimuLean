using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The slewing column arm of a station model (the library's column palletizer): <c>Slew</c> turns about
    /// its vertical, <c>Lift</c> (under it) moves up and down, <c>Reach</c> (under the lift) moves out along the arm, and
    /// the case hangs at <c>LoadSocket</c> under the gripper; the cases wait on the infeed from <c>PickSocket</c> back along
    /// the station's −X. A <see cref="UnityCombiner"/> whose model has one (<see cref="Find"/>) lays the gathered components
    /// on the infeed and, during the process, the arm carries them one after another to their places in the load:
    /// back empty to the infeed, up, round and down (<see cref="Carry"/>). View only: the model's times are unchanged.
    /// </summary>
    public sealed class ColumnArm
    {
        /// <summary>Of a carry's share of time, the part spent going back empty to the next case.</summary>
        public const float ReturnShare = 0.3f;
        /// <summary>Height above the higher end at which the case is carried round (m).</summary>
        public const float Clearance = 0.3f;
        /// <summary>Gap between the cases waiting on the infeed (m).</summary>
        public const float InfeedGap = 0.05f;

        private readonly Transform frame, slew, lift, reach, load, pick;
        private readonly Quaternion slewRest;
        private readonly Vector3 liftRest, reachRest;
        private readonly float restAngle, restRadius, restHeight, liftMin, liftMax, reachMin, reachMax;
        private Vector3 lastTop;   // where the gripper was last (frame space): an empty return starts there

        private ColumnArm(Transform frame, Transform slew, Transform lift, Transform reach, Transform load, Transform pick)
        {
            this.frame = frame; this.slew = slew; this.lift = lift; this.reach = reach; this.load = load; this.pick = pick;
            slewRest = slew.localRotation;
            liftRest = lift.localPosition;
            reachRest = reach.localPosition;
            Vector3 l = Local(load.position);
            restAngle = Angle(l);
            restRadius = Radius(l);
            restHeight = l.y;
            lastTop = l;
            // the model's travel (its script: lift 0.30 .. 2.40, reach 0.60 .. 1.70 from their rest 1.80 / 1.30)
            liftMin = liftRest.y - 1.5f; liftMax = liftRest.y + 0.6f;
            reachMin = reachRest.x - 0.7f; reachMax = reachRest.x + 0.4f;
        }

        /// <summary>The arm of <paramref name="element"/>'s shown model, or null when it has none.</summary>
        public static ColumnArm Find(Component element)
        {
            var sm = element != null ? element.GetComponent<StationModel>() : null;
            Transform root = sm != null && sm.Instance != null ? sm.Instance : null;
            if (root == null) return null;
            Transform slew = Child(root, "Slew"), lift = slew != null ? Child(slew, "Lift") : null,
                      reach = lift != null ? Child(lift, "Reach") : null, load = reach != null ? Child(reach, "LoadSocket") : null,
                      pick = Child(root, "PickSocket");
            return slew != null && lift != null && reach != null && load != null && pick != null && slew.parent != null
                ? new ColumnArm(slew.parent, slew, lift, reach, load, pick) : null;
        }

        private static Transform Child(Transform parent, string name)
        {
            foreach (Transform c in parent) if (c.name == name) return c;
            return null;
        }

        /// <summary>Lays the next waiting case on the infeed: the first (<paramref name="behind"/> 0) on the pick point, its
        /// bottom on the rollers, each next one behind the last along the station's −X; <paramref name="behind"/> grows by
        /// the case's length and a gap. Its rotation is the station's.</summary>
        public void PlaceWaiting(GameObject go, Transform station, ref float behind)
        {
            Vector3 size = Quaternion.Inverse(station.rotation) * ItemVisuals.SizeOf(go);
            go.transform.SetPositionAndRotation(
                pick.position - station.right * behind + station.up * ItemVisuals.PivotHeight(go), station.rotation);
            behind += Mathf.Abs(size.x) + InfeedGap;
        }

        /// <summary>Where a waiting case stands <paramref name="behind"/> metres back from the pick point along the
        /// station's −X (its pivot, its bottom on the rollers).</summary>
        public Vector3 InfeedPoint(GameObject go, Transform station, float behind) =>
            pick.position - station.right * behind + station.up * ItemVisuals.PivotHeight(go);

        /// <summary>A case's place in the infeed queue as the cases ahead of it leave: <paramref name="slot"/> (0 = the pick
        /// point) minus, for each case ahead, its share of the move up — eased over the first half of that case's carry,
        /// after it has left the rollers (its progress <paramref name="aheadProgress"/>; <see cref="ReturnShare"/>).</summary>
        public static float Advance(int slot, IReadOnlyList<float> aheadProgress)
        {
            float s = slot;
            foreach (float p in aheadProgress) s -= Ease(Mathf.Clamp01((p - ReturnShare) / ((1f - ReturnShare) * 0.5f)));
            return Mathf.Max(0f, s);
        }

        /// <summary>Where a case carried from <paramref name="from"/> to <paramref name="to"/> (its pivot, world) stands at
        /// progress <paramref name="p"/> of its share, and the arm's pose then: back empty from where it was to the case
        /// (<see cref="ReturnShare"/>), then up, round the column and down into its place. <paramref name="drive"/>: the arm
        /// follows (the case being carried now); else only the case's pose is given.</summary>
        public Vector3 Carry(GameObject part, Vector3 from, Vector3 to, float p, bool drive, out float turn)
        {
            float top = ItemVisuals.SizeOf(part).y - ItemVisuals.PivotHeight(part);   // pivot → top
            Vector3 up = frame.up;
            Vector3 fromTop = Local(from + up * top), toTop = Local(to + up * top);
            Vector3 grip;
            if (p < ReturnShare)
            {
                grip = Path(lastTop, fromTop, p / ReturnShare);
                turn = 0f;
                if (drive) Pose(grip);
                return from;
            }
            float q = (p - ReturnShare) / (1f - ReturnShare);
            grip = Path(fromTop, toTop, q);
            turn = Mathf.Clamp01((q - 0.25f) / 0.5f);   // the case turns while it goes round
            if (drive) { Pose(grip); lastTop = grip; }
            return frame.TransformPoint(grip + slew.localPosition) - up * top;
        }

        // up from a to the clearance, round the column (angle and radius), then down to b; eased in each leg
        private Vector3 Path(Vector3 a, Vector3 b, float q)
        {
            float h = Mathf.Max(a.y, b.y) + Clearance;
            if (q < 0.25f) return new Vector3(a.x, Mathf.Lerp(a.y, h, Ease(q / 0.25f)), a.z);
            if (q > 0.75f) return new Vector3(b.x, Mathf.Lerp(h, b.y, Ease((q - 0.75f) / 0.25f)), b.z);
            float s = Ease((q - 0.25f) / 0.5f);
            float angle = Mathf.LerpAngle(Angle(a), Angle(b), s), radius = Mathf.Lerp(Radius(a), Radius(b), s);
            return FromPolar(angle, radius, h);
        }

        // the joints that put the gripper (LoadSocket) at g (frame space)
        private void Pose(Vector3 g)
        {
            slew.localRotation = Quaternion.Euler(0f, Angle(g) - restAngle, 0f) * slewRest;
            var r = reachRest; r.x = Mathf.Clamp(reachRest.x + Radius(g) - restRadius, reachMin, reachMax);
            reach.localPosition = r;
            var l = liftRest; l.y = Mathf.Clamp(liftRest.y + g.y - restHeight, liftMin, liftMax);
            lift.localPosition = l;
        }

        // a point in the column's frame: the model root's axes, from the column's axis
        private Vector3 Local(Vector3 world) => frame.InverseTransformPoint(world) - slew.localPosition;

        // the yaw (Unity: +X turns towards −Z for a positive angle) and the distance from the column's axis
        private static float Angle(Vector3 v) => Mathf.Atan2(-v.z, v.x) * Mathf.Rad2Deg;
        private static float Radius(Vector3 v) => new Vector2(v.x, v.z).magnitude;
        private Vector3 FromPolar(float angle, float radius, float y)
        {
            float a = angle * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(a) * radius, y, -Mathf.Sin(a) * radius);
        }

        private static float Ease(float t) => t * t * (3f - 2f * t);
    }
}
