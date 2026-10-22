using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// <i>Tools ▸ SimuLean ▸ Checks ▸ Remove Missing Scripts</i>: removes the components whose script no longer exists
    /// from every object of the open scenes (Undo-recorded) — e.g. after a SimuLean component was retired
    /// (<c>SElementStatsDisplay</c>), in scenes the migration did not touch.
    /// </summary>
    public static class MissingScriptsCheck
    {
        [MenuItem("Tools/SimuLean/Checks/Remove Missing Scripts")]
        public static void RemoveFromOpenScenes()
        {
            int removed = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        int n = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                        if (n == 0) continue;
                        Undo.RegisterCompleteObjectUndo(t.gameObject, "Remove missing scripts");
                        removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                    }
            }
            Debug.Log($"[SimuLean] Removed {removed} missing script component(s) from the open scenes.");
        }
    }
}
