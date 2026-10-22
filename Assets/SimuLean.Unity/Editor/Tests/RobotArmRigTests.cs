using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The 6-axis robot member (<c>Prefabs/ResourceMembers/IndustrialRobot</c>): <see cref="RobotArmRig"/> puts the tool
    /// centre point on reachable targets with the tool pointing down, floor- and ceiling-mounted, and stretches towards
    /// unreachable ones.
    /// </summary>
    public class RobotArmRigTests
    {
        private const string PrefabPath = "Assets/SimuLean.Unity/Prefabs/ResourceMembers/IndustrialRobot.prefab";

        private static void Solve(RobotArmRig.Mount mount, Vector3 basePos, Vector3[] targets, float maxDirError)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.IsNotNull(prefab, "robot member prefab");
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                go.transform.position = basePos;
                var rig = go.GetComponent<RobotArmRig>();
                Assert.IsNotNull(rig, "the prefab carries its rig");
                Assert.IsNotNull(go.GetComponent<RobotArmView>(), "and its view");
                rig.mount = mount;
                foreach (var t in targets)
                {
                    Assert.IsTrue(rig.SetTcp(t), $"{mount}: {t} reachable");
                    Assert.Less((rig.TcpWorld - t).magnitude, 1e-3f, $"{mount}: tool at {t}");
                    Assert.Less(Vector3.Angle(rig.ToolSocket.forward, Vector3.down), maxDirError, $"{mount}: tool down at {t}");
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void FloorRobotReachesTargetsToolDown()
        {
            Solve(RobotArmRig.Mount.Floor, Vector3.zero, new[]
            {
                new Vector3(0f, 1f, 1.5f), new Vector3(-2f, 1f, 0.2f), new Vector3(2f, 1f, 1.3f), new Vector3(0f, 2f, -1.7f),
                new Vector3(1f, 0.3f, 1f), new Vector3(-1.2f, 1.8f, -1.2f), new Vector3(0.5f, 0.6f, 2.2f),
            }, 0.1f);
        }

        [Test]
        public void CeilingRobotReachesTargetsToolDown()
        {
            Solve(RobotArmRig.Mount.Ceiling, new Vector3(0f, 4.5f, 0f), new[]
            {
                new Vector3(0f, 2.5f, 1.5f), new Vector3(1.5f, 2.2f, -1f), new Vector3(-1f, 1.8f, 0.8f),
            }, 0.1f);
        }

        [Test]
        public void MountedToolPutsItsCentrePointOnTheTarget([Values("Gripper", "VacuumGripper")] string toolName)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var tool = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/SimuLean.Unity/Art/Models/RobotTools/{toolName}.fbx");
            Assert.IsNotNull(tool, toolName);
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                var socket = go.transform.GetComponentsInChildren<Transform>(true).First(t => t.name == "ToolSocket");
                var t0 = (GameObject)Object.Instantiate(tool, socket, false);   // a tool placed under the socket
                var rig = go.GetComponent<RobotArmRig>();
                Assert.IsNotNull(rig.Tcp, "the tool's TCP is found");
                float expected = toolName == "Gripper" ? 0.18f : 0.315f;
                Assert.AreEqual(expected, rig.ToolLength, 0.005f, "tool length from its TCP");
                var target = new Vector3(0.4f, 0.9f, 1.6f);
                Assert.IsTrue(rig.SetTcp(target));
                Assert.Less((rig.TcpWorld - target).magnitude, 1e-3f, "the tool centre point is on the target");
                Object.DestroyImmediate(t0);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void OutOfReachStretchesTowardsTheTarget()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var rig = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, scene)).GetComponent<RobotArmRig>();
                var far = new Vector3(6f, 1f, 0f);
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("out of reach"));
                Assert.IsFalse(rig.SetTcp(far));
                Assert.Greater(rig.TcpWorld.x, 2.4f, "the arm stretches towards the target");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
