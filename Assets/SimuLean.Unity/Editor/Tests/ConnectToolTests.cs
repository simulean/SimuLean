using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The editor's Connect tool (<see cref="ConnectionModel"/>) offers a flow connection into a Combiner: its inputs are
    /// its incoming connections although it has no input strategy (<c>HasInput</c> false) — the tool refused it, as
    /// <c>simulean_connect</c> did. One rule for both: <see cref="SElement.TakesConnections"/>.
    /// </summary>
    public class ConnectToolTests
    {
        [Test]
        public void FlowIntoACombinerIsOffered()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            if (temporary) SceneManager.SetActiveScene(scene);
            var root = new GameObject("ConnectToolModel");
            SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var src = Make<UnitySource>("CTSrc", root);
                var other = Make<UnitySource>("CTOther", root);
                var comb = Make<UnityCombiner>("CTComb", root);
                var sink = Make<UnitySink>("CTSink", root);

                var options = ConnectionModel.OptionsFor(src, comb, false, out string why);
                var flow = options.FirstOrDefault(o => o.Kind == ConnectionKind.Flow);
                Assert.IsNotNull(flow, "a Source → Combiner flow is offered (was refused: " + why + ")");
                flow.Apply();
                CollectionAssert.Contains(src.nextElements, comb, "the source sends to the combiner");
                Assert.IsTrue(ConnectionModel.OptionsFor(other, comb, false, out _).Any(o => o.Kind == ConnectionKind.Flow), "a second input");

                Assert.IsFalse(ConnectionModel.OptionsFor(comb, other, false, out why).Any(o => o.Kind == ConnectionKind.Flow), "a source takes no input");
                StringAssert.Contains("has no input", why);
                Assert.IsFalse(ConnectionModel.OptionsFor(sink, comb, false, out why).Any(o => o.Kind == ConnectionKind.Flow), "a sink has no output");
                Assert.IsTrue(ConnectionModel.OptionsFor(comb, sink, false, out _).Any(o => o.Kind == ConnectionKind.Flow), "out of a combiner, as before");
            }
            finally
            {
                if (temporary)
                {
                    if (active.IsValid()) SceneManager.SetActiveScene(active);
                    EditorSceneManager.CloseScene(scene, true);
                }
                else Object.DestroyImmediate(root);
            }
        }

        /// <summary>The first element connected is the main input, whatever the hierarchy order (a pallet
        /// supply placed after the cartons' conveyor became a carton carrying 12 pallets): the Connect tool and
        /// <c>simulean_connect</c> append to <see cref="UnityCombiner.inputOrder"/>.</summary>
        [Test]
        public void TheFirstConnectionIsTheMainInput()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            if (temporary) SceneManager.SetActiveScene(scene);
            var root = new GameObject("ConnectOrderModel");
            SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var parts = Make<UnitySource>("CTOParts", root);       // first in the hierarchy
                var pallets = Make<UnitySource>("CTOPallets", root);
                var bins = Make<UnitySource>("CTOBins", root);
                var comb = Make<UnityCombiner>("CTOComb", root);
                comb.requirements = new[] { 4, 1 };

                ConnectionModel.OptionsFor(pallets, comb, false, out _).First(o => o.Kind == ConnectionKind.Flow).Apply();
                ConnectionModel.OptionsFor(parts, comb, false, out _).First(o => o.Kind == ConnectionKind.Flow).Apply();
                SimuLeanCommands.Connect("CTOBins", "CTOComb");
                CollectionAssert.AreEqual(new SElement[] { pallets, parts, bins }, comb.ResolveInputOrder(),
                    "connection order, not hierarchy order");

                pallets.nextElements.Remove(comb);   // a removed connection frees its place; a new one goes last
                ConnectionModel.OptionsFor(pallets, comb, false, out _).First(o => o.Kind == ConnectionKind.Flow).Apply();
                CollectionAssert.AreEqual(new SElement[] { parts, bins, pallets }, comb.ResolveInputOrder());
            }
            finally
            {
                if (temporary)
                {
                    if (active.IsValid()) SceneManager.SetActiveScene(active);
                    EditorSceneManager.CloseScene(scene, true);
                }
                else Object.DestroyImmediate(root);
            }
        }

        private static T Make<T>(string name, GameObject root) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            return go.AddComponent<T>();
        }
    }
}
