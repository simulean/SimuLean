using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The automatic hierarchy of a SimuLean scene: a root <c>SimuLean Model</c> with fixed groups by
    /// function — <c>Flow</c> (every element), <c>Resources</c>, <c>Lists</c>, <c>Transport</c>, <c>Downtimes</c>,
    /// <c>Logic</c> (reactions), <c>Experiments</c>, <c>Runtime</c> (the clock and its views). A component's group comes from its library
    /// attribute (<see cref="SimuLeanElementAttribute"/> → Flow; <see cref="SimuLeanToolAttribute.Category"/>), so a
    /// new type needs nothing here. Whatever is created at the scene root with such a component is moved into its group
    /// (<see cref="AutoOrganize"/>); <see cref="Organize"/> tidies an existing scene (both in the SimuLean window). Groups sit at the origin with
    /// identity transforms, so nothing moves in the world; what the user nests elsewhere is left alone.
    /// </summary>
    [InitializeOnLoad]
    public static class ModelHierarchy
    {
        public const string RootName = "SimuLean Model";

        /// <summary>The groups, in their order under the root.</summary>
        public static readonly string[] Groups = { "Data", "Flow", "Resources", "Lists", "Transport", "Downtimes", "Logic", "Experiments", "Runtime" };

        private const string PrefAuto = "SimuLean_AutoOrganizeHierarchy";

        /// <summary>New SimuLean objects created at the scene root go into their group (per user, default on).</summary>
        public static bool AutoOrganize
        {
            get => EditorPrefs.GetBool(PrefAuto, true);
            set => EditorPrefs.SetBool(PrefAuto, value);
        }

        static ModelHierarchy()
        {
            ObjectChangeEvents.changesPublished += OnChanges;
        }

        // ── which group ──────────────────────────────────────────────────────

        private static readonly Dictionary<Type, string> groupOfType = new Dictionary<Type, string>();

        /// <summary>The group of a component type, or null when it is not a SimuLean model / tool component (or lives on
        /// the camera).</summary>
        public static string GroupOf(Type type)
        {
            if (groupOfType.TryGetValue(type, out var g)) return g;
            g = null;
            if (type.GetCustomAttribute<SimuLeanElementAttribute>() != null) g = "Flow";
            else if (type.GetCustomAttribute<SimuLeanToolAttribute>() is SimuLeanToolAttribute tool && !tool.OnMainCamera)
            {
                switch (tool.Category)
                {
                    case SimuLeanToolCategories.Data: g = "Data"; break;
                    case SimuLeanToolCategories.Resources: g = "Resources"; break;
                    case SimuLeanToolCategories.Lists: g = "Lists"; break;
                    case SimuLeanToolCategories.Transport: g = "Transport"; break;
                    case SimuLeanToolCategories.Downtime: g = "Downtimes"; break;
                    case SimuLeanToolCategories.Logic: g = "Logic"; break;
                    case SimuLeanToolCategories.Experiments: g = "Experiments"; break;
                    default: g = "Runtime"; break;   // Simulation, Analysis, Visualization: the clock and its views
                }
            }
            groupOfType[type] = g;
            return g;
        }

        /// <summary>The group of a scene object: its first SimuLean component's (an element wins), or null.</summary>
        public static string GroupOf(GameObject go)
        {
            if (go == null || go.GetComponent<Camera>() != null) return null;
            string found = null;
            foreach (var c in go.GetComponents<MonoBehaviour>())
            {
                if (c == null) continue;
                string g = GroupOf(c.GetType());
                if (g == "Flow") return g;
                found = found ?? g;
            }
            return found;
        }

        // ── the structure ────────────────────────────────────────────────────

        /// <summary>The group's Transform in <paramref name="scene"/>, created (with the root) when missing.</summary>
        public static Transform GroupTransform(Scene scene, string group)
        {
            Transform root = null;
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == RootName) { root = go.transform; break; }
            if (root == null)
            {
                var go = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(go, scene);
                Undo.RegisterCreatedObjectUndo(go, "SimuLean hierarchy");
                root = go.transform;
                root.SetSiblingIndex(0);
            }
            var t = root.Find(group);
            if (t == null)
            {
                var go = new GameObject(group);
                Undo.RegisterCreatedObjectUndo(go, "SimuLean hierarchy");
                Undo.SetTransformParent(go.transform, root, "SimuLean hierarchy");
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                t = go.transform;
            }
            // groups in their fixed order
            int index = Array.IndexOf(Groups, group), sibling = 0;
            for (int i = 0; i < index; i++) if (root.Find(Groups[i]) != null) sibling++;
            t.SetSiblingIndex(sibling);
            return t;
        }

        /// <summary>Moves <paramref name="go"/> into its group (world placement kept). False when it has none.</summary>
        public static bool Place(GameObject go)
        {
            string group = GroupOf(go);
            if (group == null || !go.scene.IsValid()) return false;
            var parent = GroupTransform(go.scene, group);
            if (go.transform.parent == parent) return true;
            Undo.SetTransformParent(go.transform, parent, true, "SimuLean hierarchy");
            return true;
        }

        /// <summary>Every SimuLean object at the root of the open scenes into its group; returns how many moved
        /// (SimuLean window ▸ Model / Settings).</summary>
        public static int OrganizeOpenScenes()
        {
            int n = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++) n += Organize(SceneManager.GetSceneAt(i));
            Debug.Log($"[SimuLean] Organize Hierarchy: {n} object(s) placed in their groups.");
            return n;
        }

        public static int Organize(Scene scene)
        {
            if (!scene.isLoaded) return 0;
            int n = 0;
            Undo.SetCurrentGroupName("Organize SimuLean hierarchy");
            foreach (var go in scene.GetRootGameObjects())
                if (go.name != RootName && GroupOf(go) != null && Place(go)) n++;
            return n;
        }

        // ── automatic placement of what is created ───────────────────────────

        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            if (!AutoOrganize || Application.isPlaying) return;
            for (int i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) != ObjectChangeKind.CreateGameObjectHierarchy) continue;
                stream.GetCreateGameObjectHierarchyEvent(i, out var e);
                // published after the creating code has run, so its components are already there
                if (UnityCompat.EditorObjectFromId(e.entityId) is GameObject go && go != null && go.transform.parent == null)
                    Place(go);
            }
        }
    }
}
