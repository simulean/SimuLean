using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Deleting an element left an empty entry in the <c>nextElements</c> of the elements that fed it, so
    /// index-based routing (an expression's output index) pointed at the wrong output. When a GameObject is destroyed in
    /// the editor, the empty entries of every element of that scene are removed (undoable, logged).
    /// </summary>
    [InitializeOnLoad]
    public static class ConnectionCleaner
    {
        static ConnectionCleaner()
        {
            ObjectChangeEvents.changesPublished += OnChanges;
        }

        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            for (int i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) != ObjectChangeKind.DestroyGameObjectHierarchy) continue;
                stream.GetDestroyGameObjectHierarchyEvent(i, out var e);
                // The events are published after the change, so the references to the destroyed object already read
                // null: clean now. A delayCall alone never ran while the editor was in the background driven by the MCP
                // (the entry stayed); it stays as a fallback (a second pass finds nothing).
                var scene = e.scene;
                Clean(scene);
                EditorApplication.delayCall += () => Clean(scene);
            }
        }

        /// <summary>Removes the empty <c>nextElements</c> entries of the elements of <paramref name="scene"/>; returns how many.</summary>
        public static int Clean(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return 0;
            int removed = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var element in root.GetComponentsInChildren<SElement>(true))
                {
                    var next = element.nextElements;
                    if (next == null || !next.Exists(x => x == null)) continue;
                    Undo.RecordObject(element, "Remove deleted connections");
                    int n = next.RemoveAll(x => x == null);
                    removed += n;
                    EditorUtility.SetDirty(element);
                    Debug.Log($"[SimuLean] '{element.name}': {n} connection(s) to a deleted element removed from Next Elements.", element);
                }
            return removed;
        }
    }
}
