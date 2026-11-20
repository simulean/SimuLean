using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A selected part of an element stands for the element (<see cref="ElementSelection"/>): the owner is the
    /// nearest object up the hierarchy with an element or a library tool; an object outside any element is its own.
    /// </summary>
    public class ElementSelectionTests
    {
        private Scene scene;
        private readonly List<GameObject> created = new List<GameObject>();

        [SetUp]
        public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
            created.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        private GameObject Make(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            else { SceneManager.MoveGameObjectToScene(go, scene); created.Add(go); }
            return go;
        }

        [Test]
        public void APartStandsForItsElement()
        {
            var station = Make("Station");
            station.AddComponent<UnityMultiServer>();
            var model = Make("Model", station.transform);
            var mesh = Make("Mesh", model.transform);
            Assert.AreSame(station, ElementSelection.OwnerOf(mesh), "a mesh deep in the model selects the station");
            Assert.AreSame(station, ElementSelection.OwnerOf(station));

            var team = Make("Team");
            team.AddComponent<UnityResourceTeam>();
            Assert.AreSame(team, ElementSelection.OwnerOf(Make("Member", team.transform)), "a team member preview selects the team");

            var network = Make("Network");
            network.AddComponent<UnityTransportNetwork>();
            var node = Make("Node", network.transform);
            node.AddComponent<UnityTransportNode>();
            Assert.AreSame(node, ElementSelection.OwnerOf(node), "a node in a network is selectable on its own");

            var group = Make("Decor");
            var chair = Make("Chair", group.transform);
            Assert.AreSame(chair, ElementSelection.OwnerOf(chair), "an object outside any element is left alone");
        }
    }
}
