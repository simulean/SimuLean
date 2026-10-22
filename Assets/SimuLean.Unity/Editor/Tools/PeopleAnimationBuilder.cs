using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Builds the people's animation assets from the clip takes of
    /// <c>Art/Models/Operator/People_Animations.fbx</c> (made by <c>Tools/Blender/assets/people_animations.py</c>):
    /// <list type="number">
    /// <item>one looping <c>.anim</c> per take in <c>Operator/Animations/</c>, keeping the bone rotations (paths
    /// <c>Root/Torso/…</c>, below each look's armature) and dropping the armature object's own curves and the bone
    /// positions — except the pelvis of the sitting / lying clips — so the female looks, of other proportions, keep
    /// their own bone lengths;</item>
    /// <item><c>People.controller</c>: one state per clip (the driver cross-fades between them);</item>
    /// <item>a <see cref="PersonAnimator"/> with that controller on <c>Operator.prefab</c> (so on every role variant) and on
    /// the patient item.</item>
    /// </list>
    /// Run it again after re-exporting the clips (<i>Tools ▸ SimuLean ▸ Build People Animations</i>).
    /// </summary>
    public static class PeopleAnimationBuilder
    {
        public const string Fbx = "Assets/SimuLean.Unity/Art/Models/Operator/People_Animations.fbx";
        public const string ClipFolder = "Assets/SimuLean.Unity/Art/Models/Operator/Animations";
        public const string ControllerPath = "Assets/SimuLean.Unity/Art/Models/Operator/People.controller";
        public static readonly string[] People = { "Assets/SimuLean.Unity/Prefabs/ResourceMembers/Operator.prefab",
                                                   "Assets/SimuLean.Unity/Prefabs/Items/Patient.prefab" };
        /// <summary>The porters: their person pushes the carrier (PushWalk).</summary>
        public static readonly string[] Porters = { "Assets/SimuLean.Unity/Prefabs/ResourceMembers/BedPorter.prefab",
                                                    "Assets/SimuLean.Unity/Prefabs/ResourceMembers/StretcherPorter.prefab",
                                                    "Assets/SimuLean.Unity/Prefabs/ResourceMembers/WheelchairPorter.prefab" };
        /// <summary>Clips whose pelvis moves (the seat, the bed): their Torso position is kept.</summary>
        public static readonly string[] PelvisMoves = { PersonAnimator.SitIdle, PersonAnimator.LieOnBed };

        [MenuItem("Tools/SimuLean/Build People Animations", false, 60)]
        public static void BuildMenu() => Debug.Log("[SimuLean] " + Build());

        public static string Build()
        {
            var clips = BuildClips();
            var controller = BuildController(clips);
            int people = 0;
            foreach (var p in People) if (AddDriver(p, controller, false)) people++;
            foreach (var p in Porters) if (AddDriver(p, controller, true)) people++;
            AssetDatabase.SaveAssets();
            return $"{clips.Count} people clips, {ControllerPath}, driver on {people} prefab(s).";
        }

        public static List<AnimationClip> BuildClips()
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(Fbx)
                           ?? throw new FileNotFoundException("Export the clips first (Tools/Blender/assets/people_animations.py).", Fbx);
            if (importer.animationType != ModelImporterAnimationType.Generic || !importer.importAnimation)
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.importAnimation = true;
                importer.SaveAndReimport();
            }
            if (!AssetDatabase.IsValidFolder(ClipFolder))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(ClipFolder).Replace('\\', '/'), Path.GetFileName(ClipFolder));
            var result = new List<AnimationClip>();
            foreach (var source in AssetDatabase.LoadAllAssetsAtPath(Fbx).OfType<AnimationClip>())
            {
                if (source.name.StartsWith("__preview")) continue;
                string name = source.name.Contains("|") ? source.name.Substring(source.name.LastIndexOf('|') + 1) : source.name;
                var clip = new AnimationClip { name = name, frameRate = source.frameRate };
                bool pelvis = PelvisMoves.Contains(name);
                foreach (var b in AnimationUtility.GetCurveBindings(source))
                {
                    if (string.IsNullOrEmpty(b.path)) continue;                         // the armature object itself
                    if (b.propertyName.StartsWith("m_LocalScale")) continue;
                    if (b.propertyName.StartsWith("m_LocalPosition") && !(pelvis && b.path.EndsWith("/Torso"))) continue;
                    AnimationUtility.SetEditorCurve(clip, b, AnimationUtility.GetEditorCurve(source, b));
                }
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = true;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                string path = $"{ClipFolder}/{name}.anim";
                var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (existing != null) { EditorUtility.CopySerialized(clip, existing); result.Add(existing); }
                else { AssetDatabase.CreateAsset(clip, path); result.Add(clip); }
            }
            return result;
        }

        public static AnimatorController BuildController(List<AnimationClip> clips)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            var sm = controller.layers[0].stateMachine;
            foreach (var s in sm.states.ToArray()) sm.RemoveState(s.state);
            foreach (var clip in clips.OrderBy(c => c.name == PersonAnimator.Idle ? 0 : 1))
            {
                var state = sm.AddState(clip.name);
                state.motion = clip;
                state.writeDefaultValues = true;
            }
            sm.defaultState = sm.states.FirstOrDefault(s => s.state.name == PersonAnimator.Idle).state;
            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static bool AddDriver(string prefabPath, RuntimeAnimatorController controller, bool pushes)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var look = root.GetComponentInChildren<OperatorAppearance>(true);
                if (look == null) return false;
                var driver = look.GetComponent<PersonAnimator>();
                if (driver == null) driver = look.gameObject.AddComponent<PersonAnimator>();
                driver.controller = controller;
                driver.pushes = pushes;
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return true;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
