using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A naval library item: the turning station's view rolls the block about its
    /// long horizontal axis in step with the process, and the block leaves turned (also checked in play mode on the
    /// Shipyard_Library sample: 88° at 51 % of the process).
    /// </summary>
    public class BlockTurnViewTests
    {
        // the station's view in a scene keeps the item's visual (the headless view drops it)
        private sealed class KeepVisual : IVElement
        {
            public void ReportState(string msg) { }
            public object GenerateItem(int type) => null;
            public void LoadItem(Item vItem) { }
            public void UnloadItem(Item vItem) { }
        }

        [Test]
        public void TheBlockRollsWithTheProcessAndLeavesTurned()
        {
            var station = new GameObject("Turning") { hideFlags = HideFlags.HideAndDontSave };
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                block.transform.localScale = new Vector3(4, 1, 2);   // long along x
                var view = station.AddComponent<BlockTurnView>();
                var clock = new SimClock(1);
                var ms = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(10) }, "Turning", clock, vElement: new KeepVisual());
                ms.Start();
                var item = new Item(0) { vItem = block };
                Assert.IsTrue(ms.Receive(item));

                view.Turn(ms, 5f);   // half the process
                Assert.AreEqual(90f, Quaternion.Angle(Quaternion.identity, block.transform.rotation), 0.5f, "half turned at half the process");
                Vector3 axis = block.transform.rotation * Vector3.forward;
                Assert.AreEqual(0f, Vector3.Dot(axis, Vector3.right), 1e-3f, "about the long (x) axis: x stays put, z turns");

                clock.AdvanceClock(20);   // the process ends (no output: it waits there, no longer processing)
                view.Turn(ms, 20f);
                Assert.AreEqual(180f, Quaternion.Angle(Quaternion.identity, block.transform.rotation), 0.5f, "it leaves turned over");
            }
            finally
            {
                Object.DestroyImmediate(station);
                Object.DestroyImmediate(block);
            }
        }
    }
}
