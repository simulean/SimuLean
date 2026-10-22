using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Single entry point for the Unity object-lookup / identity APIs (target editor: Unity 6.6).
    /// Unity keeps changing them (in 6.6 <c>FindObjectOfType</c>, <c>FindObjectsOfType</c>,
    /// <c>FindFirstObjectByType</c> and every <c>FindObjectsSortMode</c> overload are deprecated, and
    /// <c>Object.GetInstanceID()</c> is a compile error), so code calls these helpers instead of the raw
    /// APIs and a future change is made here only (docs/architecture.md §7, CLAUDE.md).
    /// </summary>
    public static class UnityCompat
    {
        /// <summary>Any loaded active object of type <typeparamref name="T"/> (no ordering guarantee), or null.</summary>
        public static T FindAny<T>() where T : Object => Object.FindAnyObjectByType<T>();

        /// <summary>Every loaded active object of type <typeparamref name="T"/>, unsorted.</summary>
        public static T[] FindAll<T>() where T : Object => Object.FindObjectsByType<T>();

        /// <summary>Every loaded object of type <typeparamref name="T"/>, optionally including inactive ones, unsorted.</summary>
        public static T[] FindAll<T>(bool includeInactive) where T : Object
            => Object.FindObjectsByType<T>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);

        /// <summary>
        /// Every loaded active component of type <typeparamref name="T"/> in hierarchy order: by loaded scene (as
        /// <c>SceneManager</c> lists them; objects of no loaded scene last), then as the Hierarchy window shows them
        /// (depth first, a parent before its children — the order of <c>GetComponentsInChildren</c>), then by the
        /// component's position on its GameObject. Unlike <see cref="FindAll{T}()"/> the order is stable across saves
        /// and reloads (sibling order is serialized), so a model built from it builds the same way every time
        ///.
        /// </summary>
        public static T[] FindAllInHierarchyOrder<T>() where T : Component
        {
            T[] all = FindAll<T>();
            var keys = new System.Collections.Generic.Dictionary<T, (int scene, int[] path, int component)>(all.Length);
            foreach (T c in all) keys[c] = HierarchyKey(c);
            System.Array.Sort(all, (a, b) => CompareKeys(keys[a], keys[b]));
            return all;
        }

        private static (int scene, int[] path, int component) HierarchyKey(Component c)
        {
            var go = c.gameObject;
            int scene = int.MaxValue;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i) == go.scene) { scene = i; break; }
            var path = new System.Collections.Generic.List<int>();
            for (Transform t = go.transform; t != null; t = t.parent) path.Insert(0, t.GetSiblingIndex());
            int component = System.Array.IndexOf(go.GetComponents<Component>(), c);
            return (scene, path.ToArray(), component);
        }

        private static int CompareKeys((int scene, int[] path, int component) a, (int scene, int[] path, int component) b)
        {
            if (a.scene != b.scene) return a.scene.CompareTo(b.scene);
            int n = System.Math.Min(a.path.Length, b.path.Length);
            for (int i = 0; i < n; i++)
                if (a.path[i] != b.path[i]) return a.path[i].CompareTo(b.path[i]);
            if (a.path.Length != b.path.Length) return a.path.Length.CompareTo(b.path.Length);   // the parent first
            return a.component.CompareTo(b.component);
        }

        /// <summary>Every loaded object of <paramref name="type"/> (non-generic, e.g. types found by reflection), unsorted.</summary>
        public static Object[] FindAll(System.Type type, bool includeInactive)
            => Object.FindObjectsByType(type, includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);

        /// <summary>
        /// A session-unique identifier of <paramref name="obj"/> as text (<c>GetEntityId()</c>).
        /// Stable while the object lives; not stable across editor sessions.
        /// </summary>
        public static string ObjectId(Object obj) => obj == null ? "null" : obj.GetEntityId().ToString();

        /// <summary>The object's engine identity (for editor APIs that take it, e.g. <c>AssetPreview</c>).</summary>
        public static EntityId Entity(Object obj) => obj.GetEntityId();

#if UNITY_EDITOR
        /// <summary>Editor only: the object an editor event refers to by id (e.g. <c>ObjectChangeEvents</c>), or null.</summary>
        public static Object EditorObjectFromId(EntityId id) => UnityEditor.EditorUtility.EntityIdToObject(id);
#endif
    }
}
