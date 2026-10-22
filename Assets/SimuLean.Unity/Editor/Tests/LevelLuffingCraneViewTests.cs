using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The level-luffing crane member (<c>Prefabs/ResourceMembers/LevelLuffingCrane</c>): <see cref="LevelLuffingCraneView"/>
    /// puts the hook on targets by travelling, slewing and luffing; the jib tip hangs over the hook, the tie and the jib
    /// tail stay joined, and the tip stays almost level over the luffing range (the four-bar linkage).
    /// </summary>
    public class LevelLuffingCraneViewTests
    {
        private const string PrefabPath = "Assets/SimuLean.Unity/Prefabs/ResourceMembers/LevelLuffingCrane.prefab";

        [Test]
        public void LinkageKeepsTheTipLevelOverTheLuffingRange()
        {
            var go = new GameObject("linkage");
            try
            {
                var v = go.AddComponent<LevelLuffingCraneView>();
                float lo = float.MaxValue, hi = float.MinValue, lastReach = float.MaxValue;
                for (float th = v.minBoomAngle; th <= v.maxBoomAngle + 1e-3f; th += 2f)
                {
                    v.Linkage(th, out _, out _, out Vector2 tip);
                    lo = Mathf.Min(lo, tip.y);
                    hi = Mathf.Max(hi, tip.y);
                    Assert.Less(tip.x, lastReach, $"reach falls as the boom rises ({th} deg)");
                    lastReach = tip.x;
                    Assert.AreEqual(th, v.AngleForReach(tip.x), 0.01f, "the reach inverts to the boom angle");
                }
                Assert.Less(hi - lo, 1.0f, "jib tip level within 1 m");
                v.Linkage(v.minBoomAngle, out _, out _, out Vector2 far);
                v.Linkage(v.maxBoomAngle, out _, out _, out Vector2 near);
                Assert.Greater(far.x, 34f, "maximum reach");
                Assert.Less(near.x, 19f, "minimum reach");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void HookReachesTargetsByTravelSlewAndLuff()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.IsNotNull(prefab, "crane member prefab");
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                var v = go.GetComponent<LevelLuffingCraneView>();
                Assert.IsNotNull(v, "the prefab carries its view");
                Assert.IsTrue(v.FindRig(), "Slew / Boom / Jib found");
                Transform hook = Find(go.transform, "HookBlock"), tip = Find(go.transform, "JibTip"),
                          tieEnd = Find(go.transform, "TieEnd"), jib = Find(go.transform, "Jib");
                var targets = new[]
                {
                    new Vector3(29f, 24f, 0f),     // rest reach, abreast
                    new Vector3(-20f, 24f, 10f),   // other side, along the rails
                    new Vector3(0f, 18f, 33f),     // on the rail line: travel to keep it beyond the minimum reach
                    new Vector3(34f, 10f, -5f),    // far out, low
                };
                foreach (var t in targets)
                {
                    v.Solve(t);
                    Assert.Less((hook.position - t).magnitude, 0.01f, $"hook at {t}");
                    Vector2 off = new Vector2(tip.position.x - t.x, tip.position.z - t.z);
                    Assert.Less(off.magnitude, 0.4f, $"jib tip over the hook ({t})");
                    // the tie's end stays on the jib tail: the jib's own tail pin (rest pose, carried by the Jib node)
                    v.Linkage(v.restBoomAngle, out Vector2 b0, out Vector2 r0, out _);
                    Vector3 tailOnJib = jib.TransformPoint(new Vector3(r0.x - b0.x, r0.y - b0.y, 0f));
                    Assert.Less((tieEnd.position - tailOnJib).magnitude, 0.05f, $"tie joined to the jib tail ({t})");
                    Assert.AreEqual(0f, go.transform.position.x, 1e-4f, "travels only along the rails");
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        private static Transform Find(Transform t, string n)
        {
            if (t.name == n) return t;
            foreach (Transform c in t)
            {
                var r = Find(c, n);
                if (r != null) return r;
            }
            return null;
        }
    }
}
