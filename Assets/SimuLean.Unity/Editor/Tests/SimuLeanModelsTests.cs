using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Model management (<see cref="SimuLeanModels"/>): create, duplicate, rename, an example copied once then
    /// numbered, the card round trip, the setting's default and the per-model results folder. Everything is made under a
    /// temporary folder (the models folder overridden), deleted afterwards; the project's settings file is never touched.
    /// </summary>
    public class SimuLeanModelsTests
    {
        private const string Temp = "Assets/__SimuLeanModelsTest__";
        private const string Models = Temp + "/Models";
        private bool hosted;

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(Temp);
            SimuLeanModels.EnsureFolder(Temp);
            SimuLeanModels.ModelsFolderOverride = Models;
            // a model is made beside the open scenes, which needs no untitled one (the batch runner's is): save it here
            var active = SceneManager.GetActiveScene();
            hosted = string.IsNullOrEmpty(active.path);
            if (hosted) Assert.IsTrue(EditorSceneManager.SaveScene(active, Temp + "/Host.unity"));
        }

        [TearDown]
        public void TearDown()
        {
            SimuLeanModels.ModelsFolderOverride = null;
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.path.StartsWith(Temp + "/") && SceneManager.sceneCount > 1 && !(hosted && s.path == Temp + "/Host.unity"))
                    EditorSceneManager.CloseScene(s, true);
            }
            if (hosted) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Temp);
        }

        [Test]
        public void TheModelsFolderSettingHasItsDefault()
        {
            Assert.AreEqual("Assets/SimuLeanModels", new SimuLeanProjectSettings().modelsFolder);
            string root = Path.Combine(Path.GetTempPath(), "simulean_models_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
                string file = Path.Combine(root, SimuLeanProjectSettings.RelativePath);
                File.WriteAllText(file, "{ \"autoSetUpNewScenes\": true }");   // an older file without the key
                Assert.AreEqual(SimuLeanProjectSettings.DefaultModelsFolder, SimuLeanProjectSettings.Load(root).modelsFolder);
                File.WriteAllText(file, "{ \"modelsFolder\": \"Assets\\\\Mine\\\\\" }");
                Assert.AreEqual("Assets/Mine", SimuLeanProjectSettings.Load(root).modelsFolder);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void AModelsResultsGoToItsOwnSubfolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "simulean_models_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
                File.WriteAllText(Path.Combine(root, SimuLeanProjectSettings.RelativePath), "{ \"modelsFolder\": \"Assets/M\", \"resultsFolder\": \"R\" }");
                Directory.CreateDirectory(Path.Combine(root, "Assets", "M", "Line"));
                File.WriteAllText(Path.Combine(root, "Assets", "M", "Line", "Line.unity"), "");
                Assert.AreEqual(Path.Combine(root, "R", "Line"), ResultsOutput.Folder(root, "Line"));
                Assert.AreEqual(Path.Combine(root, "R"), ResultsOutput.Folder(root, "NotAModel"));
                Assert.AreEqual(Path.Combine(root, "R"), ResultsOutput.Folder(root, ""));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void CreateMakesTheFolderASetUpSceneAndTheCard()
        {
            var m = SimuLeanModels.Create("Alpha", open: false, description: "A test line.");
            Assert.AreEqual(Models + "/Alpha", m.Folder);
            Assert.AreEqual(Models + "/Alpha/Alpha.unity", m.ScenePath);
            Assert.IsNotNull(m.Card);
            Assert.AreEqual("Alpha", m.Card.displayName);
            Assert.AreEqual("A test line.", m.Card.description);
            Assert.AreEqual(m.ScenePath, m.Card.scenePath);
            Assert.AreEqual("", m.Card.copiedFrom);
            Assert.IsNotEmpty(m.Card.created);
            Assert.AreEqual(1, SimuLeanModels.List().Count);
            Assert.AreSame(m.Card, SimuLeanModels.CardOf(m.ScenePath));

            var scene = EditorSceneManager.OpenScene(m.ScenePath, OpenSceneMode.Additive);
            try
            {
                Assert.IsTrue(scene.GetRootGameObjects().Any(r => r.GetComponentInChildren<UnitySimClock>(true) != null), "the scene is set up (clock)");
                Assert.IsFalse(scene.isDirty, "opening a model does not dirty it");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }

            Assert.Throws<ArgumentException>(() => SimuLeanModels.Create("alpha", open: false), "a taken name (any case)");
            Assert.Throws<ArgumentException>(() => SimuLeanModels.Create("a/b", open: false), "not a valid name");
            Assert.AreEqual("Alpha_2", SimuLeanModels.FreeName("Alpha"));
        }

        [Test]
        public void ANewModelUsesAFolderThatHoldsOnlyItsData()
        {
            // a folder made on disk with a data file, not yet imported by Unity (as a script would prepare it)
            SimuLeanModels.EnsureFolder(Models);
            string dataDir = Path.Combine(Directory.GetCurrentDirectory(), Models, "Crib");
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "service.csv"), "minutes\n1.5\n2.5\n");
            var m = SimuLeanModels.Create("Crib", open: false);
            Assert.AreEqual(Models + "/Crib", m.Folder);
            Assert.IsTrue(File.Exists(Path.Combine(dataDir, "service.csv")), "the data stays");
            Assert.IsTrue(AssetDatabase.IsValidFolder(Models + "/Crib"));
            Assert.IsFalse(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), Models, "Crib 1")), "no 'Crib 1' folder left behind");
            var ex = Assert.Throws<ArgumentException>(() => SimuLeanModels.Create("Crib", open: false), "a folder that holds a model is taken");
            StringAssert.Contains("already a model", ex.Message);
        }

        [Test]
        public void AModelMadeClosedHasItsViewsWiredInsideItsOwnScene()
        {
            // another open scene with a clock and a camera controller: the new model's views must not reference them
            // (Unity drops cross-scene references on save)
            var previous = SceneManager.GetActiveScene();
            var other = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var otherClock = new GameObject("OtherClock");
            SceneManager.MoveGameObjectToScene(otherClock, other);
            otherClock.AddComponent<UnitySimClock>();
            var otherCam = new GameObject("OtherCamera");
            SceneManager.MoveGameObjectToScene(otherCam, other);
            otherCam.AddComponent<Camera>();
            otherCam.AddComponent<SimCameraController>();
            Assert.IsTrue(EditorSceneManager.SaveScene(other, Temp + "/Other.unity"));
            SceneManager.SetActiveScene(other);
            try
            {
                var m = SimuLeanModels.Create("Closed", open: false);
                var scene = EditorSceneManager.OpenScene(m.ScenePath, OpenSceneMode.Additive);
                try
                {
                    T One<T>() where T : Component
                    {
                        var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)).ToList();
                        Assert.AreEqual(1, all.Count, typeof(T).Name + " in the model's scene");
                        return all[0];
                    }
                    var clock = One<UnitySimClock>();
                    var cam = One<SimCameraController>();
                    var hud = One<SimRuntimeHud>();
                    var panel = One<ElementInspectorPanel>();
                    One<SimVisualDetail>();
                    Assert.AreSame(clock, hud.simClock, "the HUD's clock");
                    Assert.AreSame(cam, hud.cameraController, "the HUD's camera");
                    Assert.AreSame(clock, panel.simClock, "the inspector panel's clock");
                    Assert.AreSame(cam, panel.cameraController, "the inspector panel's camera");
                    Assert.IsNotNull(cam.GetComponent<Camera>(), "the controller is on the scene's camera");
                }
                finally { EditorSceneManager.CloseScene(scene, true); }
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(other, true);
            }
        }

        [Test]
        public void DuplicateCopiesTheFolderUnderANewName()
        {
            var a = SimuLeanModels.Create("Alpha", open: false);
            var b = SimuLeanModels.Duplicate(a);
            Assert.AreEqual("Alpha_2", b.Name);
            Assert.IsTrue(File.Exists(b.ScenePath), b.ScenePath);
            Assert.IsFalse(File.Exists(b.Folder + "/Alpha.unity"), "the copied scene is renamed");
            Assert.AreEqual("Alpha_2", b.Card.displayName);
            Assert.AreEqual(b.ScenePath, b.Card.scenePath);
            Assert.AreNotEqual(AssetDatabase.AssetPathToGUID(a.CardPath), AssetDatabase.AssetPathToGUID(b.CardPath));
            Assert.AreEqual(2, SimuLeanModels.List().Count);
        }

        [Test]
        public void RenameMovesTheFolderSceneAndCard()
        {
            var a = SimuLeanModels.Create("Alpha", open: false);
            string cardGuid = AssetDatabase.AssetPathToGUID(a.CardPath);
            var b = SimuLeanModels.Rename(a, "Beta");
            Assert.AreEqual(Models + "/Beta/Beta.unity", b.ScenePath);
            Assert.IsTrue(File.Exists(b.ScenePath));
            Assert.IsFalse(AssetDatabase.IsValidFolder(Models + "/Alpha"));
            Assert.AreEqual("Beta", b.Card.displayName);
            Assert.AreEqual(b.ScenePath, b.Card.scenePath);
            Assert.AreEqual(cardGuid, AssetDatabase.AssetPathToGUID(b.CardPath), "the same card, moved");
            Assert.IsNull(SimuLeanModels.Find("Alpha"));
            Assert.Throws<ArgumentException>(() => SimuLeanModels.Rename(b, "bad/name"));
        }

        [Test]
        public void AnExampleIsCopiedWithItsOwnAssetsAndThenNumbered()
        {
            // an example with an asset of its own folder (Ex_Data/Mat.mat) used by its scene
            string exFolder = Temp + "/Examples";
            SimuLeanModels.EnsureFolder(exFolder + "/Ex_Data");
            var mat = new Material(Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.CreateAsset(mat, exFolder + "/Ex_Data/Mat.mat");
            string exPath = exFolder + "/Ex.unity";
            var ex = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(cube, ex);
            cube.GetComponent<MeshRenderer>().sharedMaterial = mat;
            Assert.IsTrue(EditorSceneManager.SaveScene(ex, exPath));
            EditorSceneManager.CloseScene(ex, true);

            var copy = SimuLeanModels.CopyExample(exPath, null, open: false);
            Assert.AreEqual("Ex", copy.Name);
            Assert.AreEqual(exPath, copy.Card.copiedFrom);
            Assert.AreEqual(1, SimuLeanModels.CopiesOf(exPath).Count);
            string copiedMat = copy.Folder + "/Ex_Data/Mat.mat";
            Assert.IsTrue(File.Exists(copiedMat), "the example's own folder is copied");
            var scene = EditorSceneManager.OpenScene(copy.ScenePath, OpenSceneMode.Additive);
            try
            {
                var r = scene.GetRootGameObjects().Select(g => g.GetComponent<MeshRenderer>()).First(x => x != null);
                Assert.AreEqual(copiedMat, AssetDatabase.GetAssetPath(r.sharedMaterial), "the copy uses the copied asset, not the example's");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }

            var again = SimuLeanModels.CopyExample(exPath, null, open: false);
            Assert.AreEqual("Ex_2", again.Name, "a new copy gets a numbered name");
            Assert.AreEqual(2, SimuLeanModels.CopiesOf(exPath).Count);
            Assert.IsTrue(File.Exists(exPath), "the example is left as it was");

            var examples = SimuLeanModels.Examples();
            Assert.IsTrue(examples.Any(e => e.ScenePath == "Assets/Scenes/Validation/MM1.unity"), "the repository's examples are listed");
            Assert.IsFalse(examples.Any(e => e.ScenePath == SimuLeanTestRunner.NeutralScene), "the neutral scene is not an example");
        }

        [Test]
        public void AnExamplesCardIsReadForItsSceneAndGoesWithACopy()
        {
            // an example's card lives in its own folder <Example>/ (examples share their folder)
            string exFolder = Temp + "/Examples";
            SimuLeanModels.EnsureFolder(exFolder + "/Ex");
            string exPath = exFolder + "/Ex.unity";
            var ex = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.IsTrue(EditorSceneManager.SaveScene(ex, exPath));
            EditorSceneManager.CloseScene(ex, true);
            var exCard = ScriptableObject.CreateInstance<SimuLeanModelCard>();
            exCard.displayName = "An example"; exCard.description = "What it shows."; exCard.author = "SimuLean";
            exCard.kpis = "out=Exit.input"; exCard.assumptions = "One shift.";
            AssetDatabase.CreateAsset(exCard, exFolder + "/Ex/" + SimuLeanModelCard.FileName);
            Assert.AreEqual(exFolder + "/Ex/" + SimuLeanModelCard.FileName, SimuLeanModels.ExampleCardPath(exPath));
            Assert.AreSame(exCard, SimuLeanModels.CardOf(exPath), "the example scene's card (simulean_card, simulean_model info)");

            var copy = SimuLeanModels.CopyExample(exPath, null, open: false);
            Assert.AreEqual(copy.CardPath, AssetDatabase.GetAssetPath(copy.Card), "the copy's card sits beside its scene");
            Assert.AreEqual(exPath, copy.Card.copiedFrom);
            Assert.AreEqual("What it shows.", copy.Description, "the example's text goes with the copy");
            Assert.AreEqual("out=Exit.input", copy.Card.kpis);
            Assert.AreEqual("One shift.", copy.Card.assumptions);
            Assert.AreEqual(copy.ScenePath, copy.Card.scenePath);
            Assert.IsFalse(AssetDatabase.IsValidFolder(copy.Folder + "/Ex"), "no stale copy of the example's card");
            Assert.AreSame(copy.Card, SimuLeanModels.CardOf(copy.ScenePath));
            Assert.AreEqual("An example", exCard.displayName, "the example's card is left as it was");

            // the repository's example has its card, shown in the Models tab
            var workshop = SimuLeanModels.Examples().FirstOrDefault(e => e.ScenePath == "Assets/Scenes/LibraryExamples/Workshop_Resources.unity");
            Assert.IsNotNull(workshop?.Card, "Workshop_Resources has its card");
            Assert.IsFalse(string.IsNullOrWhiteSpace(workshop.Description));
            Assert.AreNotEqual(workshop.Name, workshop.DisplayName);
        }

        [Test]
        public void TheCardRoundTrips()
        {
            var m = SimuLeanModels.Create("Alpha", open: false);
            SimuLeanModels.SetCardField(m.Card, "kpis", "out=Exit.input; wip=Buffer.content_avg");
            SimuLeanModels.SetCardField(m.Card, "Represents", "A two-station line.");
            var reread = AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(m.CardPath);
            Assert.AreEqual("out=Exit.input; wip=Buffer.content_avg", reread.kpis);
            Assert.AreEqual("A two-station line.", reread.Get("represents"));
            var all = reread.ToDictionary();
            Assert.AreEqual(SimuLeanModelCard.Fields.Count, all.Count);
            Assert.IsTrue(all.ContainsKey("assumptions") && all.ContainsKey("copiedFrom"));
            Assert.Throws<ArgumentException>(() => SimuLeanModels.SetCardField(m.Card, "created", "never"), "kept by SimuLean");
            Assert.Throws<ArgumentException>(() => SimuLeanModels.SetCardField(m.Card, "colour", "blue"), "no such field");
            Assert.IsTrue(File.ReadAllText(m.CardPath).Contains("A two-station line."), "saved to the asset");
        }

        [Test]
        public void NewEchoesItsDefaultsInWordsAndTheCardGivesTheTextItReplaced()
        {
            var made = Newtonsoft.Json.Linq.JObject.FromObject(SimuLeanCommands.Models("new", "Delta", open: false));
            StringAssert.StartsWith("none", (string)made["settings"]["warmup"], "no warm-up in words: " + made);
            StringAssert.Contains("headless runs and studies use seeds 1..N", (string)made["settings"]["seed"]);
            var first = Newtonsoft.Json.Linq.JObject.FromObject(SimuLeanCommands.Card("represents", "A line.", "Delta"));
            var second = Newtonsoft.Json.Linq.JObject.FromObject(SimuLeanCommands.Card("represents", "Two lines.", "Delta"));
            Assert.AreEqual("A line.", (string)second["previous"], "the overwritten text: " + second);
            Assert.IsNotNull(first["previous"]);
            Assert.AreEqual(Newtonsoft.Json.Linq.JTokenType.Null, Newtonsoft.Json.Linq.JObject.FromObject(SimuLeanCommands.Card(model: "Delta"))["previous"].Type, "a read replaces nothing");
        }

        /// <summary><c>simulean_models action=save</c>: only the open model, only when the
        /// commands created or opened it in this editor session; never another scene.</summary>
        [Test]
        public void SaveSavesOnlyAModelTheCommandsOpened()
        {
            var host = SceneManager.GetActiveScene();
            var notAModel = Assert.Throws<InvalidOperationException>(() => SimuLeanCommands.Models("save"));
            StringAssert.Contains("not one of the project's models", notAModel.Message);

            var m = SimuLeanModels.Create("Gamma", open: false);
            var scene = EditorSceneManager.OpenScene(m.ScenePath, OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var clean = Newtonsoft.Json.Linq.JObject.FromObject(SimuLeanCommands.Models("save"));
                Assert.IsFalse((bool)clean["saved"], "nothing to save: " + clean);

                var go = new GameObject("GammaEdit");
                SceneManager.MoveGameObjectToScene(go, scene);
                EditorSceneManager.MarkSceneDirty(scene);
                // not opened through the commands in this session: its changes may be someone else's
                SessionStateForget(m.ScenePath);
                var e = Assert.Throws<InvalidOperationException>(() => SimuLeanCommands.Models("save"));
                StringAssert.Contains("ask the user", e.Message);
                Assert.IsTrue(scene.isDirty);

                SimuLeanCommands.RecordAgentScene(m.ScenePath);
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Models("save", "Alpha"), "save saves the open model only");
                var saved = Newtonsoft.Json.Linq.JObject.FromObject(SimuLeanCommands.Models("save", "Gamma"));
                Assert.IsTrue((bool)saved["saved"], saved.ToString());
                Assert.IsFalse(scene.isDirty);
                StringAssert.Contains("GammaEdit", File.ReadAllText(m.ScenePath));
            }
            finally
            {
                if (host.IsValid()) SceneManager.SetActiveScene(host);
                SessionStateForget(m.ScenePath);
            }
        }

        private static void SessionStateForget(string scenePath)
        {
            var kept = SimuLeanCommands.AgentScenes;
            kept.Remove(scenePath);
            SessionState.SetString("SimuLean.AgentModelScenes", string.Join("|", kept));
        }
    }
}
