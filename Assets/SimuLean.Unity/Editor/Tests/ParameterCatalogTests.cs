using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SimuLean.Optimization;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Every parameterisable property is offered (parameterisation): each key the scene extraction
    /// writes — for every element type of the library, every downtime kind, a resource team, user properties and every
    /// sample scene — is either a parameter (its type says it) or declared in a <see cref="ParameterSchema"/> (option,
    /// sampler spec, numbers, structural). A new element type, key or property fails here until it is decided.
    /// </summary>
    public class ParameterCatalogTests
    {
        [Test]
        public void EveryKeyOfEveryLibraryElementIsDecided()
        {
            var active = SceneManager.GetActiveScene();
            var scene = !string.IsNullOrEmpty(active.path) ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                var types = TypeCache.GetTypesWithAttribute<SimuLeanElementAttribute>()
                                     .Where(t => typeof(SElement).IsAssignableFrom(t) && !t.IsAbstract).ToList();
                Assert.Greater(types.Count, 8, "the library's element types");
                SElement first = null;
                foreach (var t in types)
                {
                    var go = new GameObject(t.Name);
                    go.transform.SetParent(root.transform);
                    var se = (SElement)go.AddComponent(t);
                    if (se is UnitySource source) source.mode = SourceMode.InterArrival;   // schedule mode needs a loaded table
                    first = first ?? se;
                }
                var labels = first.gameObject.AddComponent<SElementLabels>();
                labels.SetLabelValue("batch", 12);
                var team = new GameObject("Crew").AddComponent<UnityResourceTeam>();
                team.transform.SetParent(root.transform);
                foreach (UnityDowntime.DowntimeKind k in Enum.GetValues(typeof(UnityDowntime.DowntimeKind)))
                {
                    var d = new GameObject("Downtime_" + k).AddComponent<UnityDowntime>();
                    d.transform.SetParent(root.transform);
                    d.kind = k;
                    d.targets.Add(first);
                }

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var undecided = ModelParameterCatalog.Undecided(cfg);
                Assert.IsEmpty(undecided, "keys no schema decides: " + string.Join(", ", undecided));
                Assert.AreEqual(types.Count, cfg.Elements.Count);
                Assert.IsNotEmpty(cfg.ResourceTeams);
                Assert.AreEqual(Enum.GetValues(typeof(UnityDowntime.DowntimeKind)).Length, cfg.Downtimes.Count);

                // and what is offered can be applied
                var firstConfig = cfg.Elements.Find(e => e.Name == first.name);
                var prop = ModelParameterCatalog.ForElement(firstConfig).FirstOrDefault(p => p.Key == PropertyRecords.Key + ".batch");
                Assert.IsNotNull(prop, "a numeric user property is a parameter");
                int offered = cfg.Elements.Sum(e => ModelParameterCatalog.ForElement(e).Count)
                            + cfg.ResourceTeams.Sum(x => ModelParameterCatalog.ForTeam(x).Count)
                            + cfg.Downtimes.Sum(x => ModelParameterCatalog.ForDowntime(x).Count);
                Assert.Greater(offered, 60, $"parameters offered across the library ({offered})");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (scene != active) EditorSceneManager.CloseScene(scene, true);
            }
        }

        static IEnumerable<string> SampleScenes()
            => Directory.GetFiles("Assets/Scenes", "*.unity", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')).OrderBy(p => p);

        [TestCaseSource(nameof(SampleScenes))]
        public void EveryKeyOfTheSampleSceneIsDecided(string path)
        {
            using (var iso = new IsolatedScene(path))
            {
                // as a scene run extracts it (teams moved by Unity physics export their calibration / External travel)
                var undecided = ModelParameterCatalog.Undecided(SceneExtractor.Extract(iso.Root, sceneRun: true).Config);
                Assert.IsEmpty(undecided, "keys no schema decides: " + string.Join(", ", undecided));
            }
        }
    }
}
