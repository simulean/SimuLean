using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The palletizer's column arm (<see cref="ColumnArm"/>): the case hangs under the gripper all the way from
    /// the infeed to its place on the pallet, the arm goes back empty first, and the cases on the infeed move up as the
    /// ones ahead leave.
    /// </summary>
    public class ColumnArmTests
    {
        private GameObject station, box;
        private ColumnArm arm;
        private Transform load;

        [SetUp]
        public void SetUp()
        {
            station = new GameObject("Palletizer (test)");
            var sm = station.AddComponent<StationModel>();
            sm.options.Add(new StationModel.Option
            {
                name = "Column palletizer",
                model = AssetDatabase.LoadAssetAtPath<GameObject>(LibraryPresets.Models + "Palletizer/Palletizer.fbx"),
            });
            sm.selected = 0;
            sm.Rebuild();
            arm = ColumnArm.Find(station.transform);
            foreach (var t in sm.Instance.GetComponentsInChildren<Transform>()) if (t.name == "LoadSocket") load = t;
            box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.transform.localScale = new Vector3(0.4f, 0.28f, 0.6f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(station);
            Object.DestroyImmediate(box);
        }

        [Test]
        public void ThePalletizerModelHasAnArm() => Assert.IsNotNull(arm, "Slew / Lift / Reach / LoadSocket / PickSocket found");

        [Test]
        public void TheCaseHangsUnderTheGripperAllTheWay()
        {
            Vector3 from = arm.InfeedPoint(box, station.transform, 0f);
            Vector3 to = station.transform.TransformPoint(new Vector3(1.4f, 0.3f, 0.25f));
            float top = ItemVisuals.SizeOf(box).y - ItemVisuals.PivotHeight(box);
            for (float p = ColumnArm.ReturnShare + 0.01f; p < 1f; p += 0.05f)
            {
                box.transform.position = arm.Carry(box, from, to, p, true, out _);
                Assert.Less(Vector3.Distance(box.transform.position + Vector3.up * top, load.position), 1e-3f, $"at {p:0.00}");
            }
            Assert.Less(Vector3.Distance(arm.Carry(box, from, to, 0.999f, true, out _), to), 0.01f, "it ends in its place");
        }

        [Test]
        public void TheArmGoesBackEmptyWhileTheCaseWaits()
        {
            Vector3 from = arm.InfeedPoint(box, station.transform, 0f);
            Vector3 to = station.transform.TransformPoint(new Vector3(1.3f, 0.3f, 0f));
            Vector3 before = load.position;
            Assert.AreEqual(from, arm.Carry(box, from, to, ColumnArm.ReturnShare * 0.5f, true, out _), "the case stays on the rollers");
            Assert.Greater(Vector3.Distance(load.position, before), 0.1f, "the arm moves towards it");
        }

        [Test]
        public void TheCasesOnTheInfeedMoveUpAsTheOnesAheadLeave()
        {
            Assert.AreEqual(2f, ColumnArm.Advance(2, new[] { 0f, 0f }), 1e-4f, "nobody gone: the third waits two places back");
            Assert.AreEqual(2f, ColumnArm.Advance(2, new[] { ColumnArm.ReturnShare, 0f }), 1e-4f, "the first still on the rollers");
            Assert.AreEqual(1f, ColumnArm.Advance(2, new[] { 1f, 0f }), 1e-4f, "the first laid: one place up");
            Assert.AreEqual(0f, ColumnArm.Advance(2, new[] { 1f, 1f }), 1e-4f, "both gone: on the pick point");
        }
    }
}
