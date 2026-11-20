using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The stacker crane fits the rack it serves — a 10-level rack of 1.4 m levels
    /// (14 m, top pallet seat 12.6 m) was served by the 8.2 m model with its carriage stopped at 5.8 m.
    /// </summary>
    public class StackerCraneViewTests
    {
        private const string Model = "Assets/SimuLean.Unity/Art/Models/ASRS/StackerCrane.fbx";
        private GameObject crane;

        [SetUp]
        public void SetUp()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(Model);
            Assert.IsNotNull(asset, Model);
            crane = Object.Instantiate(asset, Vector3.zero, Quaternion.identity);
        }

        [TearDown]
        public void TearDown() { if (crane != null) Object.DestroyImmediate(crane); }

        private static float Top(GameObject go)
        {
            float top = float.NegativeInfinity;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                foreach (var v in mf.sharedMesh.vertices) top = Mathf.Max(top, r.transform.TransformPoint(v).y);
            }
            return top;
        }

        // the seats of a rack beside the aisle (x = 1.35: a slot centre), every 1.4 m from 0.6 m
        private static List<Vector3> Rack(int levels, float x = 1.35f)
        {
            var pts = new List<Vector3>();
            for (int l = 0; l < levels; l++) pts.Add(new Vector3(x, 0.6f + 1.4f * l, 5f));
            return pts;
        }

        [Test]
        public void TheCarriageReachesTheTopLevelOfATallRack()
        {
            var view = crane.AddComponent<StackerCraneView>();
            float before = Top(crane);
            var rack = Rack(9);                        // seats 0.6 .. 11.8 m on one side
            rack.Add(new Vector3(-1.35f, 12.6f, 8f));  // the top seat, 12.6 m, on the other side of the aisle
            rack.Add(new Vector3(6f, 30f, 5f));        // the next aisle's point (6 m to the side) is not this crane's
            float lift = view.FitTo(rack);
            Assert.AreEqual(12.6f - StackerCraneView.SocketHeight, lift, 1e-3f, "the lift reaches the 12.6 m seat");
            view.PoseAt(new Vector3(-1.35f, 12.6f, 8f));
            Assert.AreEqual(12.0f, view.CarriageLift, 1e-3f, "the carriage at the top level (was clamped at 5.8 m)");
            float carriageTop = view.CarriageLift + 1.35f;   // the carriage's guide frame above its fork top
            float top = Top(crane);
            Assert.Greater(top, carriageTop, "the mast is taller than the carriage at its top");
            Assert.AreEqual(before + (lift - StackerCraneView.ModelLift), top, 0.05f, "the mast stretched by the extra lift");
            Assert.AreEqual(StackerCraneView.ModelMastHeight + (lift - StackerCraneView.ModelLift), view.MastHeight, 1e-3f);

            // fitted again (a new run): the model is stretched once, not twice
            view.FitTo(rack);
            Assert.AreEqual(top, Top(crane), 1e-3f);
        }

        [Test]
        public void ARackWithinTheModelKeepsTheModelAsBuilt()
        {
            var view = crane.AddComponent<StackerCraneView>();
            float before = Top(crane);
            float lift = view.FitTo(Rack(5));         // the library rack: seats 0.6 .. 6.2
            Assert.AreEqual(StackerCraneView.ModelLift, lift, 1e-4f, "never shorter than the model");
            Assert.AreEqual(before, Top(crane), 1e-4f, "the mast is not changed");
            view.PoseAt(new Vector3(1.35f, 3.4f, 2f));
            Assert.AreEqual(2.8f, view.CarriageLift, 1e-3f, "a low seat: the carriage at its height, as before");
        }
    }
}
