using NUnit.Framework;
using SimuLean.Resources;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>Process resources released at another element: the scene names that element.</summary>
    public class ReleaseAtElementTests
    {
        [Test]
        public void TheReleaseElementIsExtractedByName()
        {
            var root = new GameObject("Model");
            try
            {
                var team = new GameObject("Polygraphs").AddComponent<UnityResourceTeam>();
                team.transform.SetParent(root.transform);
                team.AddMembers(2);
                var desk = new GameObject("Device desk").AddComponent<UnityMultiServer>();
                desk.transform.SetParent(root.transform);
                var back = new GameObject("Device return").AddComponent<UnityQueue>();
                back.transform.SetParent(root.transform);
                desk.processBy = new ProcessResourceUse { team = team, release = ProcessRelease.AtElement, releaseAt = back };

                var config = SceneExtractor.ExtractElement(desk);
                Assert.AreEqual("AtElement", config.GetParameter<string>(ResourceUseKeys.ProcessRelease, ""));
                Assert.AreEqual("Device return", config.GetParameter<string>(ResourceUseKeys.ProcessReleaseAt, ""));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void TwoCopiesOfAReleaseElementBuild()
        {
            // review2 #13: the release element went by name; a composite placed twice (two "Device return") made the model
            // unbuildable ("Several elements are named …"). The scene run exports the element's id.
            var root = new GameObject("Model");
            try
            {
                var team = new GameObject("Polygraphs").AddComponent<UnityResourceTeam>();
                team.transform.SetParent(root.transform);
                team.AddMembers(2);
                UnityQueue firstBack = null;
                for (int k = 0; k < 2; k++)
                {
                    var group = new GameObject("Home test " + k);
                    group.transform.SetParent(root.transform);
                    var desk = new GameObject("Device desk").AddComponent<UnityMultiServer>();
                    desk.transform.SetParent(group.transform);
                    var back = new GameObject("Device return").AddComponent<UnityQueue>();
                    back.transform.SetParent(group.transform);
                    if (k == 0) firstBack = back;
                    desk.processBy = new ProcessResourceUse { team = team, release = ProcessRelease.AtElement, releaseAt = back };
                }
                var x = SceneExtractor.Extract(root);
                var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
                Assert.DoesNotThrow(() => model.BuildModel(x.Config), "two copies of the composite build");
                Assert.IsTrue(x.Config.Elements.Exists(e => e.GetParameter<string>(ResourceUseKeys.ProcessReleaseAt, "") == x.ElementIds[firstBack]),
                              "the release element goes by its id");
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
