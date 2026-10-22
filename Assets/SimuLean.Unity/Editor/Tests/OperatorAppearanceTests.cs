using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The operator member (<c>Prefabs/ResourceMembers/Operator</c>): both looks carry the joints the views drive,
    /// stand 1.74 m tall facing +Z, and <see cref="OperatorAppearance"/> shows one look (Mixed alternates by member)
    /// and toggles the hard hat.
    /// </summary>
    public class OperatorAppearanceTests
    {
        private const string PrefabPath = "Assets/SimuLean.Unity/Prefabs/ResourceMembers/Operator.prefab";
        private static readonly string[] Joints =
            { "Torso", "Leg_L", "Leg_R", "Shin_L", "Shin_R", "Arm_L", "Arm_R", "Forearm_L", "Forearm_R", "CarrySocket", "Helmet" };

        private static Transform FindActive(Transform t, string n)
        {
            if (!t.gameObject.activeInHierarchy) return null;
            if (t.name == n) return t;
            foreach (Transform c in t) { var r = FindActive(c, n); if (r != null) return r; }
            return null;
        }

        // The vertices' world bounds (a skinned renderer's own bounds are padded on purpose).
        private static Bounds MeshBounds(Renderer r)
        {
            if (!(r is SkinnedMeshRenderer smr)) return r.bounds;
            var m = new Mesh();
            smr.BakeMesh(m, true);
            var v = m.vertices;
            var b = new Bounds(smr.transform.TransformPoint(v[0]), Vector3.zero);
            foreach (var p in v) b.Encapsulate(smr.transform.TransformPoint(p));
            Object.DestroyImmediate(m);
            return b;
        }

        private static void WithOperator(System.Action<GameObject, OperatorAppearance> body)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.IsNotNull(prefab, "operator member prefab");
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                var look = go.GetComponent<OperatorAppearance>();
                Assert.IsNotNull(look, "the prefab carries its appearance");
                Assert.IsNotNull(look.femaleModel, "and the female look");
                Assert.IsNotNull(go.GetComponent<UnityVehicleAnimator>(), "and its animator");
                body(go, look);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void BothLooksHaveTheJointsAndTheSameSize([Values(OperatorAppearance.Gender.Male, OperatorAppearance.Gender.Female)] OperatorAppearance.Gender g)
        {
            WithOperator((go, look) =>
            {
                look.gender = g;
                look.helmet = true;
                look.Apply();
                Assert.AreEqual(g == OperatorAppearance.Gender.Female, look.femaleModel.activeSelf, "the chosen look is shown");
                foreach (var j in Joints) Assert.IsNotNull(FindActive(go.transform, j), $"{g}: joint {j}");
                var lod0 = go.GetComponentsInChildren<Renderer>(false).Where(r => r.name.EndsWith("_LOD0")).ToArray();
                Assert.IsNotEmpty(lod0, $"{g}: LOD0 renderers");
                var b = MeshBounds(lod0[0]);
                foreach (var r in lod0) b.Encapsulate(MeshBounds(r));
                Assert.AreEqual(1.74f, b.max.y - go.transform.position.y, 0.06f, $"{g}: height");
                Assert.Less(Mathf.Abs(b.min.y - go.transform.position.y), 0.03f, $"{g}: on the floor");
                Vector3 socket = FindActive(go.transform, "CarrySocket").position - go.transform.position;
                Assert.Greater(socket.z, 0.25f, $"{g}: carries in front (+Z)");
            });
        }

        [Test]
        public void MixedAlternatesAndTheHatToggles()
        {
            WithOperator((go, look) =>
            {
                look.gender = OperatorAppearance.Gender.Mixed;
                look.ForMember(0);
                Assert.IsFalse(look.femaleModel.activeSelf, "member 0: male");
                look.ForMember(1);
                Assert.IsTrue(look.femaleModel.activeSelf, "member 1: female");
                look.helmet = false;
                look.Apply();
                Assert.IsNull(FindActive(go.transform, "Helmet"), "no hat");
            });
        }
    }
}
