using System.Linq;
using NUnit.Framework;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>A new <see cref="UnityDowntime"/> stops with its kind's state and mode (a shift OFF_SHIFT and AfterCurrent,
    /// a periodic stop SCHEDULED_DOWN, a timetable STOPPED, a failure BREAKDOWN); a state or mode set on the component is
    /// exported as set, so a scene that saved the former default BREAKDOWN / Immediate keeps it.</summary>
    public class DowntimeKindDefaultsSceneTests
    {
        private static DowntimeConfig Extract(UnityDowntime.DowntimeKind kind, System.Action<UnityDowntime> set = null)
        {
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var press = new GameObject("Press").AddComponent<UnityMultiServer>();
                press.transform.SetParent(root.transform);
                var d = new GameObject("Stops").AddComponent<UnityDowntime>();
                d.transform.SetParent(root.transform);
                d.kind = kind;
                d.targets.Add(press);
                if (kind == UnityDowntime.DowntimeKind.Timetable) d.rows.Add(new UnityDowntime.TimetableRow { start = "10", duration = 5 });
                set?.Invoke(d);
                return SceneExtractor.Extract(root).Config.Downtimes.Single();
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ANewDowntimeTakesItsKindsStateAndMode()
        {
            var expected = new (UnityDowntime.DowntimeKind kind, string state, StopMode mode)[]
            {
                (UnityDowntime.DowntimeKind.MtbfMttr, "BREAKDOWN", StopMode.Immediate),
                (UnityDowntime.DowntimeKind.Timetable, "STOPPED", StopMode.Immediate),
                (UnityDowntime.DowntimeKind.Shift, "OFF_SHIFT", StopMode.AfterCurrent),
                (UnityDowntime.DowntimeKind.Periodic, "SCHEDULED_DOWN", StopMode.Immediate),
            };
            foreach (var (kind, state, mode) in expected)
            {
                var dc = Extract(kind);
                Assert.AreEqual(state, dc.Settings.StateName, $"{kind}: its state");
                Assert.AreEqual(mode, dc.Settings.Mode, $"{kind}: its mode");
            }
        }

        [Test]
        public void AStateAndModeSetOnTheComponentAreKept()
        {
            var dc = Extract(UnityDowntime.DowntimeKind.Shift, d => { d.stateName = "BREAKDOWN"; d.mode = UnityDowntime.DowntimeMode.Immediate; });
            Assert.AreEqual("BREAKDOWN", dc.Settings.StateName, "the former default, saved: kept");
            Assert.AreEqual(StopMode.Immediate, dc.Settings.Mode, "Immediate set on a shift: kept");
            dc = Extract(UnityDowntime.DowntimeKind.MtbfMttr, d => { d.stateName = "CALLED_AWAY"; d.mode = UnityDowntime.DowntimeMode.AfterCurrent; });
            Assert.AreEqual("CALLED_AWAY", dc.Settings.StateName);
            Assert.AreEqual(StopMode.AfterCurrent, dc.Settings.Mode);
            Assert.AreEqual((int)StopMode.Immediate, (int)UnityDowntime.DowntimeMode.Immediate, "a saved Immediate (0) reads Immediate");
            Assert.AreEqual((int)StopMode.AfterCurrent, (int)UnityDowntime.DowntimeMode.AfterCurrent, "a saved AfterCurrent (1) reads AfterCurrent");
        }
    }
}
