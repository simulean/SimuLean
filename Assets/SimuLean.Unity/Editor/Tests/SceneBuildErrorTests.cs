using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>A station processed by a team that is not part of the model is a clear error naming both
    /// (it was a KeyNotFoundException, which the clock retried every frame).</summary>
    public class SceneBuildErrorTests
    {
        [Test]
        public void AnInactiveTeamIsAClearError()
        {
            var root = new GameObject("Model");
            try
            {
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }
                var team = Make<UnityResourceTeam>("Crew");
                team.AddMembers(1);
                var server = Make<UnityMultiServer>("Server");
                var sink = Make<UnitySink>("Sink");
                server.nextElements = new List<SElement> { sink };
                server.processBy = new ProcessResourceUse { team = team };
                team.gameObject.SetActive(false);

                var e = Assert.Throws<System.InvalidOperationException>(() => SceneExtractor.Extract(root));
                StringAssert.Contains("Server", e.Message);
                StringAssert.Contains("Crew", e.Message);
            }
            finally { Object.DestroyImmediate(root); }
        }

        /// <summary>The clock: a scene model that cannot be built ends the run with one error; the next
        /// frames do not extract and build it again (they did, every frame, with a new seed logged each time).</summary>
        [Test]
        public void TheClockReportsABuildErrorOnce()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            var instanceField = typeof(UnitySimClock).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static);
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }
                var clock = Make<UnitySimClock>("Clock");
                var team = Make<UnityResourceTeam>("Crew");
                team.AddMembers(1);
                var server = Make<UnityMultiServer>("Server");
                var sink = Make<UnitySink>("Sink");
                server.nextElements = new List<SElement> { sink };
                server.processBy = new ProcessResourceUse { team = team };
                team.gameObject.SetActive(false);

                void Call(string method) => typeof(UnitySimClock).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(clock, null);
                Call("Awake");
                Call("Start");
                clock.RunSimulation();
                LogAssert.Expect(LogType.Error, new Regex("cannot be built.*Crew"));
                Call("Update");
                Assert.IsFalse(clock.SimOn, "the run is switched off");
                Call("Update");   // no second error (LogAssert fails the test on an unexpected one)
                Call("Update");
                Assert.IsFalse(clock.SimStarted);
            }
            finally
            {
                instanceField?.SetValue(null, null);
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }
    }
}
