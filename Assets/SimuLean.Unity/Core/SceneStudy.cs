using System.Collections.Generic;
using SimuLean.Optimization;
using SimuLean.Results;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A <see cref="Study"/> of the open scene: the scene extracted once (its clock's horizon,
    /// warm-up, calendar and end condition come with it), tables read with the Unity reader (CSV, <c>.data</c> and Excel),
    /// named after the scene. Scripts and the MCP build an experiment in one expression and run it headless, e.g.
    /// <code>SceneStudy.Of().Factor("Yard.capacity", 1, 2, 4).Factor("team:Crew.members[skill=welding]", 3, 5, 8)
    ///     .Factor("Plates.table", "Data/push.csv", "Data/pull.csv").Metric("makespan", "model.makespan")
    ///     .WithReplications(5).Run()</code>
    /// The scene is never changed.
    /// </summary>
    public static class SceneStudy
    {
        /// <param name="root">Model root (null = the scene's <see cref="UnityModelExtractor"/> root, else the whole scene).</param>
        public static Study Of(GameObject root = null)
        {
            if (root == null)
            {
                var extractor = UnityCompat.FindAny<UnityModelExtractor>();
                if (extractor != null) root = extractor.modelRoot;
            }
            var config = SceneExtractor.Extract(root).Config;
            string scene = root != null ? root.scene.name : UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            return new Study(config)
            {
                Name = $"Study_{scene}_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                TableLoader = path => TableFiles.Read(path),
            };
        }

        /// <summary>Runs <paramref name="study"/>, publishes the result set (the SimuLean window's Results tab) and writes it
        /// in <paramref name="formats"/> (none = no file). Main thread (reads the project folder).</summary>
        public static ResultSet RunAndPublish(Study study, ResultFormats formats = ResultFormats.None)
        {
            var set = study.Run();
            set.Model = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            foreach (string path in ResultsOutput.Keep(set, formats, ResultsOutput.ProjectRoot))
                Debug.Log($"[SimuLean] Results: {path}");
            return set;
        }
    }
}
