using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The procedural transfer car (<see cref="UnityTransferCar"/>, <see cref="TransferCarMesh"/>): lanes × depth from
    /// the capacity, slot sockets and their positions, the gauge following the width, the train view's slot order.</summary>
    public class TransferCarTests
    {
        [TestCase(1, 1, 1)]
        [TestCase(2, 2, 1)]
        [TestCase(3, 3, 1)]
        [TestCase(4, 3, 2)]
        [TestCase(6, 3, 2)]
        [TestCase(7, 3, 3)]
        public void LanesAndDepthFollowTheCapacity(int c, int lanes, int depth)
        {
            var s = TransferCarSpec.ForCapacity(c);
            Assert.AreEqual(lanes, s.lanes, "lanes");
            Assert.AreEqual(depth, s.depth, "depth");
            Assert.GreaterOrEqual(s.Positions, c);
        }

        [Test]
        public void IndexingAlignsTheServedLaneWithTheTransferPoint()
        {
            var go = new GameObject("car");
            var stop = new GameObject("conveyor");
            try
            {
                // a car on a track along world +X (car +Z = the track), at x = 6; a conveyor at x = 6 beside it, its
                // transfer point 2.4 m off the track
                go.transform.SetPositionAndRotation(new Vector3(6f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f));
                var car = go.AddComponent<UnityTransferCar>();
                car.capacity = 3;
                car.Rebuild();
                var tp = new GameObject("TransferPoint").transform;
                tp.SetParent(stop.transform, false);
                stop.transform.position = new Vector3(6f, 0f, -2.4f);
                tp.localPosition = new Vector3(0f, 0.5f, 0.65f);
                Assert.AreSame(tp, MilkRunTrainView.TransferPointOf(stop.transform), "the child TransferPoint wins");
                foreach (var slot in car.Slots)
                {
                    Vector3 rest = slot.position;
                    float offset = MilkRunTrainView.LaneOffset(rest, tp.position, go.transform.forward);
                    Vector3 shifted = rest + go.transform.forward * offset;
                    Assert.Less(Mathf.Abs(Vector3.Dot(shifted - tp.position, go.transform.forward)), 0.01f,
                                $"{slot.name}: in line with the transfer point (the transfer is perpendicular to the track)");
                }
                Assert.AreEqual(0f, MilkRunTrainView.LaneOffset(car.Slots[1].position, tp.position, go.transform.forward) - car.Spec.LaneSpacing * 0f
                                - Vector3.Dot(tp.position - car.Slots[1].position, Vector3.right), 1e-4, "offset measured along the track");
                Assert.AreEqual(1f, MilkRunTrainView.IndexProgress(0.35f, 0.35f), 1e-6, "indexed at the share");
                Assert.AreEqual(0f, MilkRunTrainView.TransferProgress(0.35f, 0.35f), 1e-6, "the transfer starts after it");
                Assert.AreEqual(1f, MilkRunTrainView.TransferProgress(1f, 0.35f), 1e-6, "and ends with the step");
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(stop); }
        }

        // a car on a track along world +X (yaw 90: its lanes, +Z of the slots, point to world −Z)
        static UnityTransferCar Car(GameObject go, int capacity)
        {
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 90f, 0f));
            var car = go.AddComponent<UnityTransferCar>();
            car.capacity = capacity;
            car.Rebuild();
            return car;
        }

        [TestCase(0.40f, 10f, 2, 0.65f)]    // the shift lengthens the leg
        [TestCase(-0.30f, 10f, 0, -0.65f)]  // shortens it
        [TestCase(4.00f, 0.6f, 0, 0f)]      // longer than the leg: it ends behind its start — still one way, no go-and-return
        public void TheLegEndsWithTheServedLaneAlignedAndNeverReverses(float leaveOffset, float legLength, int lane, float tpAlongTrack)
        {
            var go = new GameObject("car");
            try
            {
                var car = Car(go, 3);
                Vector3 axis = go.transform.forward;   // world +X
                Vector3 a = Vector3.zero, b = new Vector3(legLength, 0f, 0f);
                Vector3 tp = b + new Vector3(tpAlongTrack, 0.5f, -2.4f);   // the stop's transfer point, off the track
                Vector3 slotLocal = car.Slots[lane].position - go.transform.position;
                float arrive = MilkRunTrainView.LaneOffset(b + slotLocal, tp, axis);

                // the engine's leg: an eased (accelerate / brake) profile from a to b; the view adds the shift
                Vector3 At(float s)
                {
                    Vector3 basePos = Vector3.Lerp(a, b, Mathf.SmoothStep(0f, 1f, s));
                    return basePos + axis * MilkRunTrainView.LegOffset(leaveOffset, arrive, MilkRunTrainView.LegProgress(basePos, a, b));
                }
                Assert.Less(Vector3.Distance(At(0f), a + axis * leaveOffset), 1e-4f, "it leaves from where it stood (aligned at the last stop)");
                Vector3 end = At(1f) + slotLocal;
                Assert.Less(Mathf.Abs(Vector3.Dot(end - tp, axis)), 0.01f, "on arrival the served lane is in line with the transfer point");

                float dir = Mathf.Sign(Vector3.Dot(At(1f) - At(0f), axis));
                Vector3 prev = At(0f);
                for (int i = 1; i <= 400; i++)
                {
                    Vector3 p = At(i / 400f);
                    Assert.GreaterOrEqual(dir * Vector3.Dot(p - prev, axis), -1e-5f, $"no reversal of the direction of motion (sample {i})");
                    prev = p;
                }

                // at the stop: already aligned, so no separate shift — the transfer starts at once
                Assert.AreEqual(0f, MilkRunTrainView.StepShare(arrive, MilkRunTrainView.LaneOffset(b + slotLocal, tp, axis), 0.35f), "no pre-shift");
                Assert.Greater(MilkRunTrainView.TransferProgress(0.1f, 0f), 0f, "the transfer is under way at 10 % of the step");
                Assert.AreEqual(0.35f, MilkRunTrainView.StepShare(0f, 1.3f, 0.35f), 1e-6, "another lane at the same stop indexes");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void LoadAndUnloadKeepTheHeadingOnAStraightPath()
        {
            var go = new GameObject("car");
            try
            {
                var car = Car(go, 3);
                Transform slot = car.Slots[1];
                // a pallet on a conveyor beside the track that runs towards the track (+Z): it faces the other way from the
                // slot's lane direction (−Z) — it must not be turned round
                Quaternion onConveyor = Quaternion.Euler(0f, 0f, 0f);
                Assert.Greater(Quaternion.Angle(onConveyor, slot.rotation), 170f, "the case: lane and conveyor point opposite ways");
                Vector3 wait = new Vector3(slot.position.x, slot.position.y, -2.3f);
                Quaternion heading = go.transform.rotation * (Quaternion.Inverse(go.transform.rotation) * onConveyor);   // kept relative to the car

                foreach (var (from, to) in new[] { (wait, slot.position), (slot.position, wait) })   // load, then unload
                {
                    Vector3 line = (to - from).normalized;
                    for (int i = 0; i <= 50; i++)
                    {
                        MilkRunTrainView.TransferPose(from, to, heading, i / 50f, out Vector3 p, out Quaternion r);
                        Assert.LessOrEqual(Quaternion.Angle(r, onConveyor), 0.5f, "the heading is kept through the transfer");
                        Vector3 off = (p - from) - line * Vector3.Dot(p - from, line);
                        Assert.Less(off.magnitude, 1e-4f, "on the straight segment between the two positions");
                    }
                    MilkRunTrainView.TransferPose(from, to, heading, 1f, out Vector3 last, out _);
                    Assert.Less(Vector3.Distance(last, to), 1e-4f);
                }
                Assert.Less(Mathf.Abs(Vector3.Dot(wait - slot.position, go.transform.forward)), 1e-4f, "the transfer is perpendicular to the track");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void SlotsSocketsAndGeometry()
        {
            var go = new GameObject("car");
            try
            {
                var car = go.AddComponent<UnityTransferCar>();
                car.capacity = 4;
                car.Rebuild();
                var s = car.Spec;
                Assert.AreEqual(6, car.Slots.Count, "3 lanes × 2 positions");
                for (int d = 0; d < 2; d++)
                    for (int l = 0; l < 3; l++)
                    {
                        var t = go.transform.Find(UnityTransferCar.GeneratedName + $"/Slot_{l}_{d}");
                        Assert.IsNotNull(t, $"Slot_{l}_{d}");
                        Vector3 p = t.localPosition;
                        Assert.AreEqual(s.deckHeight, p.y, 1e-4, "on the deck");
                        Assert.AreEqual((l - 1) * s.LaneSpacing, p.z, 1e-4, "lanes side by side along the track");
                        Assert.AreEqual((d - 0.5f) * s.positionPitch, p.x, 1e-4, "positions along the lane, across the track");
                        Assert.Less(Vector3.Angle(t.forward, Vector3.right), 1e-3f, "slot +Z along the lane");
                    }
                Assert.AreEqual(s.Width - 0.35f, s.RailGauge, 1e-5, "gauge follows the width");
                Assert.Greater(s.Width, 2 * s.positionPitch, "the lanes hold the depth");
                var order = MilkRunTrainView.LaneSlots(go.transform);
                Assert.AreEqual("Slot_0_0", order[0].name);
                Assert.AreEqual("Slot_2_0", order[2].name);
                Assert.AreEqual("Slot_0_1", order[3].name, "depth first, then lane");
                Assert.IsNotNull(go.GetComponentInChildren<LODGroup>(), "one LODGroup");
                Assert.IsNotNull(go.transform.Find(UnityTransferCar.GeneratedName + "/Beacon"));

                car.SetCapacity(1);
                Assert.AreEqual(1, car.Slots.Count, "rebuilt for C = 1: a single lane");
                Assert.AreEqual(1, MilkRunTrainView.LaneSlots(go.transform).Count, "the old sockets are gone");
                var track = TransferCarMesh.BuildTrack(car.Spec, 10f);
                Assert.Greater(track.vertexCount, 0);
                Assert.AreEqual(car.Spec.RailGauge + 0.6f, track.bounds.size.x, 0.05f, "track width follows the gauge");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
