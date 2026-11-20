using System.Text;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Inspector for <see cref="UnityStacker"/> (live stacks in Play mode, a force-release button) and the
    /// GameObject → SimuLean → Stacking menu: a plain stacker, or a complete gantry stacker (element + the
    /// library's <c>GantryStacker</c> model + <see cref="GantryStackerView"/>).
    /// </summary>
    [CustomEditor(typeof(UnityStacker))]
    public class UnityStackerEditor : SimuLean.Unity.Editor.SElementInspector
    {
        private const string GantryModelPath = "Assets/SimuLean.Unity/Art/Models/GantryStacker/GantryStacker.fbx";

        protected override void DrawParameters() => DrawRemaining(false);

        protected override void DrawVisual()
        {
            DrawRemaining(true);
            if (!Application.isPlaying && GUILayout.Button("Create crane nodes (pick point + positions)"))
            {
                var stacker = (UnityStacker)target;
                Undo.RecordObject(stacker, "Create crane nodes");
                stacker.CreateCraneNodes();
                EditorUtility.SetDirty(stacker);
            }
        }

        protected override void DrawPlayExtra()
        {
            var u = (UnityStacker)target;
            var s = u.Stacker;
            if (s == null) return;

            EditorGUILayout.Space();
            var sb = new StringBuilder();
            sb.Append($"State {s.CurrentState.Name}  |  stacked {s.ItemsStacked}  |  stacks out {s.StacksReleased} (partial {s.PartialStacksReleased})");
            sb.Append($"\nPick point: {(s.PickItem != null ? "item " + s.PickItem.GetId() : "free")}  |  cycle: {(s.CycleInProgress ? "running" : "-")}  |  waiting stacks {s.WaitingStacks}");
            foreach (var p in s.Positions)
                sb.Append($"\n  #{p.Index}: {p.Count}/{s.CapacityOf(p)}  h {p.Height:0.###}" +
                          (string.IsNullOrEmpty(p.Key) ? "" : $"  key {p.Key}") + (p.IsComplete ? $"  complete ({p.CompletionReason})" : ""));
            EditorGUILayout.HelpBox(sb.ToString(), MessageType.None);
            if (GUILayout.Button("Release all stacks now")) s.ReleaseAll();
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/GantryStacker/GantryStacker.fbx")]
        // an automatic stacker with its own gantry (the stacker of a cutting line): the same Stacker, its cycle shown by the
        // gantry; a hall crane that stacks and also serves other stations is the Stacker's Crane mode with a crane team
        [SimuLeanVariantOf(typeof(UnityStacker))]
        [MenuItem("GameObject/SimuLean/Stacking/Stacker with Gantry", false, 41)]
        private static void CreateGantryStacker(MenuCommand cmd) => Create(cmd, withGantry: true);

        private static void Create(MenuCommand cmd, bool withGantry)
        {
            var go = new GameObject(withGantry ? "GantryStacker" : "Stacker");
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            go.AddComponent<UnityStacker>();

            if (withGantry)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(GantryModelPath);
                var view = go.AddComponent<GantryStackerView>();
                if (model != null)
                {
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(model, go.transform);
                    inst.transform.localPosition = Vector3.zero;
                    // the gantry's runway (its Bridge travels along the model's Z) along the stacker's row (its X)
                    inst.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
                    view.gantryRoot = inst.transform;
                }
                else
                    Debug.LogWarning($"[SimuLean] {GantryModelPath} not found: the stacker animates the items without a gantry.");
            }
            Undo.RegisterCreatedObjectUndo(go, "Create Stacker");   // complete: one undo step, nothing dangling
            Selection.activeObject = go;
        }
    }
}
