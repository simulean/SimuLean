using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The model card's inspector (<see cref="SimuLeanModelCardEditor"/>): its <i>Scripts / Logic</i> section reads the card's
    /// scripts as <c>simulean_card</c> does — the choice rules and the decisions by rung of the example
    /// <c>ChoiceLogicDemo</c> — only while the card's scene is the open one (else a note), without dirtying the card or the scene.
    /// </summary>
    public class ModelCardEditorTests
    {
        private const string Demo = "Assets/Scenes/ChoiceLogicDemo.unity";

        [Test]
        public void TheCardInspectorListsTheRuleOnlyWhileTheSceneIsOpen()
        {
            var card = AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(SimuLeanModels.ExampleCardPath(Demo));
            Assert.IsNotNull(card, "the demo's card");
            Assert.AreSame(card, SimuLeanModels.CardOf(Demo));
            bool cardDirty = EditorUtility.IsDirty(card);

            var elsewhere = ScriptableObject.CreateInstance<SimuLeanModelCard>();
            try
            {
                elsewhere.scenePath = "Assets/NotOpen.unity";
                StringAssert.Contains("open the model", (string)SimuLeanModelCardEditor.ScriptsOf(elsewhere)["note"]);
            }
            finally { Object.DestroyImmediate(elsewhere); }

            var previous = SceneManager.GetActiveScene();
            using (var iso = new IsolatedScene(Demo))
            {
                bool sceneDirty = iso.Scene.isDirty;
                var probe = ScriptableObject.CreateInstance<SimuLeanModelCard>();
                try
                {
                    probe.scenePath = iso.Scene.path;   // the demo, or its copy when the demo is open
                    SceneManager.SetActiveScene(iso.Scene);
                    var scripts = SimuLeanModelCardEditor.ScriptsOf(probe, iso.Root);
                    Assert.IsFalse(scripts.ContainsKey("note"), "scripts of the open scene: " + scripts.GetValueOrDefault("note"));
                    var rules = ((IList)scripts["rules"]).Cast<Dictionary<string, object>>().ToList();
                    Assert.AreEqual(1, rules.Count);
                    Assert.AreEqual("ShortestQueueRule", rules[0]["rule"]);
                    Assert.AreEqual("Pack Queue", rules[0]["owner"]);
                    Assert.IsNotEmpty((string)rules[0]["why"]);
                    Assert.AreEqual(1, (int)scripts["scripted"]);
                    StringAssert.Contains("1 by a choice rule", (string)scripts["summary"]);
                    var rungs = (Dictionary<string, int>)scripts["decisions"];
                    Assert.Greater(rungs["expression"], 0);
                    Assert.Greater(rungs["reaction"], 0);
                }
                finally
                {
                    if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                    Object.DestroyImmediate(probe);
                }
                Assert.AreEqual(sceneDirty, iso.Scene.isDirty, "read-only: the scene is not modified");
            }
            Assert.AreEqual(cardDirty, EditorUtility.IsDirty(card), "read-only: the card is not modified");
        }
    }
}
