using System.Collections.Generic;
using System.Linq;
using SimuLean.Signals;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The element inspector's <i>Reactions</i> section: the reactions that listen to the
    /// element or act on it — each a line that selects it — and <b>+ Reaction</b>, which creates one already listening to
    /// the element. The scene's reactions are looked up again only when the scene changes.
    /// </summary>
    internal static class ReactionsSection
    {
        private static List<UnityReaction> cache;
        private static double cachedAt;

        [InitializeOnLoadMethod]
        private static void WatchChanges()
        {
            EditorApplication.hierarchyChanged += () => cache = null;
            ObjectChangeEvents.changesPublished += (ref ObjectChangeEventStream _) => cache = null;
            Undo.undoRedoPerformed += () => cache = null;
        }

        // looked up again when the scene changes, and at most a second old (a change the events missed)
        private static List<UnityReaction> All()
        {
            double now = EditorApplication.timeSinceStartup;
            if (cache == null || now - cachedAt > 1.0 || cache.Any(r => r == null))
            {
                cache = UnityCompat.FindAll<UnityReaction>(true).Where(r => r != null).OrderBy(r => r.name).ToList();
                cachedAt = now;
            }
            return cache;
        }

        /// <summary>The reactions involving <paramref name="element"/>.</summary>
        public static List<UnityReaction> Of(SElement element) => All().Where(r => r.Involves(element)).ToList();

        /// <summary>The reactions that stop or call back members of <paramref name="team"/>.</summary>
        public static List<UnityReaction> Of(UnityResourceTeam team) => All().Where(r => r.Involves(team)).ToList();

        /// <summary>A team inspector's line: its reactions, each a link that selects it (nothing when there are none).</summary>
        public static void DrawFor(UnityResourceTeam team)
        {
            var list = Of(team);
            if (list.Count == 0) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Reactions", "Reactions that send members off or call them back"));
                foreach (var r in list)
                    if (GUILayout.Button(r.name, EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                    {
                        Selection.activeObject = r.gameObject;
                        EditorGUIUtility.PingObject(r.gameObject);
                    }
            }
        }

        /// <summary>The section header's note: how many.</summary>
        public static string Summary(SElement element)
        {
            int n = Of(element).Count;
            return n == 0 ? "none" : n == 1 ? "1 reaction" : n + " reactions";
        }

        public static void Draw(SElement element)
        {
            var list = Of(element);
            if (list.Count == 0)
                EditorGUILayout.LabelField("Nothing reacts to this element yet: a reaction does something when it signals (an item " +
                                           "enters or leaves, its state changes, it stops or resumes).", EditorStyles.wordWrappedMiniLabel);
            foreach (var r in list)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(r.name, "Select the reaction"), EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                    {
                        Selection.activeObject = r.gameObject;
                        EditorGUIUtility.PingObject(r.gameObject);
                    }
                    string role = !r.IsTimeSignal && r.sources.Contains(element) ? UnityReactionEditor.Sentence(r) : "acts on it · " + UnityReactionEditor.Sentence(r);
                    if (!r.reactionEnabled) role = "(off) " + role;
                    EditorGUILayout.LabelField(role, EditorStyles.wordWrappedMiniLabel);
                }
                if (Application.isPlaying && r.Bound != null)
                    EditorGUILayout.LabelField("   fired " + r.Bound.Fired + (r.Bound.Skipped > 0 ? $" · {r.Bound.Skipped} not met" : ""),
                                               EditorStyles.miniLabel);
            }
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                if (GUILayout.Button(new GUIContent("+ Reaction", "A reaction that listens to this element (choose what it does in its inspector)")))
                    Create(element);
        }

        /// <summary>A new reaction listening to <paramref name="element"/>, selected for editing.</summary>
        public static UnityReaction Create(SElement element)
        {
            var go = new GameObject(GameObjectUtility.GetUniqueNameForSibling(null, "Reaction " + element.name));
            Undo.RegisterCreatedObjectUndo(go, "Add reaction");
            go.transform.position = element.transform.position + Vector3.up * 2f;
            var r = go.AddComponent<UnityReaction>();
            r.sources.Add(element);
            r.actions.Add(new UnityReaction.ActionSpec());
            if (ModelHierarchy.AutoOrganize) ModelHierarchy.Place(go);
            cache = null;
            Selection.activeObject = go;
            return r;
        }
    }
}
