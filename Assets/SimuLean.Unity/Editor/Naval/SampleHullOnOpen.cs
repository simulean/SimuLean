using SimuLean.Unity.Naval;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Naval
{
    /// <summary>
    /// Writes the generic sample hull (<see cref="NavalPresets.SampleHullTable"/>) to the project database when a scene
    /// whose <see cref="UnityBlockBreakdown"/> reads it is opened and the table is not there — a fresh clone: the
    /// database (<c>SimuLeanData/</c>) is not in the repository, so the shipbuilding examples would have no ship. Only that
    /// table, only when it is missing (an existing one is never overwritten), and the scene is not modified.
    /// </summary>
    [InitializeOnLoad]
    internal static class SampleHullOnOpen
    {
        static SampleHullOnOpen()
        {
            EditorSceneManager.sceneOpened += (scene, mode) => EnsureFor(scene);
        }

        /// <summary>Writes the sample hull for every block breakdown of <paramref name="scene"/> that reads it; the
        /// number of tables written.</summary>
        internal static int EnsureFor(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return 0;
            int written = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var bd in root.GetComponentsInChildren<UnityBlockBreakdown>(true))
                    if (bd.table == NavalPresets.SampleHullTable && NavalPresets.EnsureSampleHull(bd.DatabasePath()))
                        written++;
            return written;
        }
    }
}
