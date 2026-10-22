using System.Collections.Generic;
using UnityEngine;
using SimuLean.Serialization;
using SimuLean.Unity;

namespace SimuLean.Unity
{
    /// <summary>
    /// Extracts the simulation model configuration from the Unity scene.
    /// Walks all SElement components and produces a serializable SimulationConfig.
    /// Dispatches per-element extraction and connection logic to registered
    /// <see cref="IElementExtractor"/> implementations via <see cref="ElementExtractorRegistry"/>.
    /// </summary>
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Experiments, "Model Extractor", Description = "Exports the scene as a headless SimulationConfig (JSON)", Singleton = true, Order = 2)]
    public class UnityModelExtractor : MonoBehaviour
    {
        [Header("Model Elements")]
        [Tooltip("Model root - GameObject that contains all simulation elements. Leave null to search the entire scene.")]
        public GameObject modelRoot;

        [Header("Export Options")]
        [Tooltip("Output configuration file name")]
        public string configFileName = "simulation_config.json";

        /// <summary>The model of the scene (or of <see cref="modelRoot"/>) as a <see cref="SimulationConfig"/>.</summary>
        public SimulationConfig ExtractConfiguration() => SceneExtractor.Extract(modelRoot).Config;

        // =====================================================================
        //  JSON import / export helpers
        // =====================================================================

        /// <summary>Extracts configuration and serialises to JSON, saving to persistentDataPath.</summary>
        public string ExportToJson()
        {
            var config = ExtractConfiguration();
            string json = JsonUtility.ToJson(config, prettyPrint: true);

            string path = System.IO.Path.Combine(Application.persistentDataPath, configFileName);
            System.IO.File.WriteAllText(path, json);

            Debug.Log($"[UnityModelExtractor] Configuration exported to: {path}");
            return json;
        }

        [ContextMenu("Extract Model Configuration")]
        public void TestExtractConfiguration()
        {
            var config = ExtractConfiguration();
            Debug.Log($"[UnityModelExtractor] Extracted {config.Elements.Count} elements " +
                      $"and {config.Connections.Count} connections");

            foreach (var elem in config.Elements)
                Debug.Log($"  Element: {elem.Name} (Type: {elem.Type}, ID: {elem.Id})");
        }

        [ContextMenu("Export to JSON")]
        public void TestExportToJson()
        {
            string json = ExportToJson();
            Debug.Log("[UnityModelExtractor] Exported:\n" + json);
        }
    }
}
