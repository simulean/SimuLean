using System;
using System.Collections.Generic;
using NUnit.Framework;
using SimuLean.Resources;
using SimuLean.Serialization;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The clock's time unit applies without a start date (the HUD date), and a
    /// team's physical units (m/s, s) are converted with it — the same in the scene's config and headless.
    /// </summary>
    public class TimeUnitsTests
    {
        [Test]
        public void ClockDateUsesTheTimeUnitWithoutAStartDate()
        {
            var go = new GameObject("Clock (test)") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var clock = go.AddComponent<UnitySimClock>();
                clock.StartDateTime = "";
                clock.TimeUnitSeconds = 3600;
                var cal = clock.CreateCalendar();
                Assert.AreEqual(3600, cal.SecondsPerUnit, "the unit, without a start date");
                Assert.IsTrue(cal.IsDefault, "still undated (views show model time)");
                clock.clock.Context.Calendar = cal;
                clock.clock.AdvanceClock(46);
                Assert.AreEqual(new DateTime(2000, 1, 2, 22, 0, 0), clock.CurrentDateTime, "t = 46 h is 2 January, 22:00 (was 0:00:46)");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void PhysicalTeamUnitsAreConvertedWithTheClockUnit()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);   // the safe test runner may leave an untitled scene active
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                var clockGo = new GameObject("Clock");
                clockGo.transform.SetParent(root.transform);
                var clock = clockGo.AddComponent<UnitySimClock>();
                clock.TimeUnitSeconds = 3600;

                var teamGo = new GameObject("Welders");
                teamGo.transform.SetParent(root.transform);
                var team = teamGo.AddComponent<UnityResourceTeam>();
                team.units = ResourceUnits.Physical;
                team.speed = 1.2f;
                team.members = new List<ResourceMemberSpec> { new ResourceMemberSpec { name = "w1", loadTime = 90, unloadTime = 180 } };

                var config = SceneExtractor.Extract(root).Config;
                Assert.AreEqual(3600, config.TimeUnitSeconds, "the clock's unit goes to the config");
                var tc = team.ToConfig("welders");
                Assert.AreEqual("Physical", tc.Units);
                Assert.AreEqual(1.2, tc.Speed, 1e-6, "the config keeps the values as entered (catalog parameters in m/s)");

                var engineClock = new SimClock(1);
                engineClock.Context.Calendar = config.CreateCalendar();
                var executors = new List<TaskExecutor>();
                ResourceBuild.BuildTeam(tc, engineClock, executors);
                Assert.AreEqual(90.0 / 3600, executors[0].LoadTime, 1e-12, "90 s = 0.025 h");
                Assert.AreEqual(180.0 / 3600, executors[0].UnloadTime, 1e-12);
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }
    }
}
