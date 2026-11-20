using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Selecting a part of an element (its 3D model, a pad, a work point…) in the Scene view or the Hierarchy selects the
    /// element instead (a click on the model went to the mesh deep in the hierarchy and the element was easy to
    /// take apart). The owner is the nearest object up the hierarchy that carries an element or a library tool
    /// (<see cref="SElement"/>, <see cref="SimuLeanElementAttribute"/>, <see cref="SimuLeanToolAttribute"/>); objects
    /// outside any element are left alone. <i>Tools ▸ SimuLean ▸ Edit Element Parts</i> (per user, off) turns it off
    /// to edit the parts; in Play mode nothing is redirected.
    /// </summary>
    [InitializeOnLoad]
    public static class ElementSelection
    {
        private const string PrefKey = "SimuLean.EditElementParts";
        private const string MenuPath = "Tools/SimuLean/Edit Element Parts";

        /// <summary>On: the parts of an element can be selected (off by default, per user).</summary>
        public static bool EditParts
        {
            get => EditorPrefs.GetBool(PrefKey, false);
            set { EditorPrefs.SetBool(PrefKey, value); Menu.SetChecked(MenuPath, value); }
        }

        static ElementSelection()
        {
            Selection.selectionChanged += OnSelectionChanged;
            // also from the update: the event is not raised for every change (a selection set by a tool while the editor
            // is in the background); comparing the active object is cheap
            EditorApplication.update += () =>
            {
                Object active = Selection.activeObject;
                if (active == lastActive) return;
                lastActive = active;
                OnSelectionChanged();
            };
            EditorApplication.delayCall += () => Menu.SetChecked(MenuPath, EditParts);
        }

        private static Object lastActive;

        [MenuItem(MenuPath, priority = 110)]
        private static void Toggle() => EditParts = !EditParts;

        private static bool redirecting;

        /// <summary>Raised once a selection change has been handled (parts replaced by their elements): what follows the
        /// selection (the Place tool's activation) listens here, so it sees the element, and also the changes Unity does
        /// not announce.</summary>
        public static event System.Action Settled;

        private static void OnSelectionChanged()
        {
            Redirect();
            if (redirecting) return;
            lastActive = Selection.activeObject;   // handled: the update does not take it again
            Settled?.Invoke();
        }

        private static void Redirect()
        {
            if (redirecting || EditParts || EditorApplication.isPlayingOrWillChangePlaymode) return;
            Object[] selected = Selection.objects;
            var result = new List<Object>(selected.Length);
            bool changed = false;
            foreach (Object o in selected)
            {
                Object owner = o is GameObject go && go.scene.IsValid() ? OwnerOf(go) : o;
                if (owner != o) changed = true;
                if (!result.Contains(owner)) result.Add(owner);
            }
            if (!changed) return;
            redirecting = true;
            try { Selection.objects = result.ToArray(); lastActive = Selection.activeObject; }
            finally { redirecting = false; }
        }

        /// <summary>The object <paramref name="go"/> belongs to: itself when it carries an element or a library tool, else
        /// its nearest ancestor that does; itself when none does.</summary>
        public static GameObject OwnerOf(GameObject go)
        {
            for (Transform t = go.transform; t != null; t = t.parent)
                if (IsSimuLeanObject(t.gameObject)) return t.gameObject;
            return go;
        }

        private static bool IsSimuLeanObject(GameObject g)
        {
            foreach (MonoBehaviour c in g.GetComponents<MonoBehaviour>())
            {
                if (c == null) continue;
                System.Type type = c.GetType();
                if (c is SElement || type.IsDefined(typeof(SimuLeanElementAttribute), true) ||
                    type.IsDefined(typeof(SimuLeanToolAttribute), true))
                    return true;
            }
            return false;
        }
    }
}
