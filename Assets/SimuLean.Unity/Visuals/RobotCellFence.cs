using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A safety fence around a robot team: off by default; switched on, a square of the library's fence
    /// panels (<c>SafetyFence/</c>) is built round the team's position at the robot's reach plus a clearance, with a door
    /// on its front side (+Z). Built in the editor as the child <c>Safety Fence</c> (four <see cref="UnityModularStrip"/>s),
    /// removed when switched off. Purely visual: the model is unchanged. Added by the robot team presets.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("SimuLean/Visuals/Robot Cell Fence")]
    public class RobotCellFence : MonoBehaviour
    {
        public const string Child = "Safety Fence";
        private const string Kit = "Assets/SimuLean.Unity/Art/Models/SafetyFence/";

        [Tooltip("Build the fence round the robot.")]
        public bool fence;
        [Tooltip("Reach of the robot arm (m): the fence stands this far from the team's position plus the clearance.")]
        [Min(0.5f)] public float reach = 2.7f;
        [Tooltip("Distance between the robot's reach and the fence (m).")]
        [Min(0f)] public float clearance = 0.5f;
        [Tooltip("A door in the middle of the front side (+Z).")]
        public bool door = true;

        /// <summary>Half the side of the fenced square (m).</summary>
        public float HalfSide => reach + clearance;

        /// <summary>The fence (the <c>Safety Fence</c> child), or null.</summary>
        public Transform Built => transform.Find(Child);

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying) return;
            UnityEditor.EditorApplication.delayCall += () => { if (this != null) Rebuild(); };
        }

        /// <summary>Builds the fence (or removes it when <see cref="fence"/> is off). Editor only.</summary>
        [SimuLeanAction("Rebuild the fence round the robot")]
        public void Rebuild()
        {
            if (Application.isPlaying) return;
            var old = Built;
            if (old != null) UnityEditor.Undo.DestroyObjectImmediate(old.gameObject);
            if (!fence) return;
            var panel = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Kit + "Fence_Panel.fbx");
            var cap = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Kit + "Fence_End.fbx");
            var gate = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Kit + "Fence_Door.fbx");
            if (panel == null) { Debug.LogWarning("[SimuLean] The fence panels (" + Kit + ") were not found."); return; }
            var root = new GameObject(Child);
            UnityEditor.Undo.RegisterCreatedObjectUndo(root, "Robot cell fence");
            root.transform.SetParent(transform, false);
            float h = HalfSide;
            // each side starts at a corner and runs along its +Z, going round: front (+Z), right, back, left
            Side(root.transform, "Front", new Vector3(-h, 0f, h), 90f, 2f * h, panel, cap, door ? gate : null);
            Side(root.transform, "Right", new Vector3(h, 0f, h), 180f, 2f * h, panel, cap, null);
            Side(root.transform, "Back", new Vector3(h, 0f, -h), 270f, 2f * h, panel, cap, null);
            Side(root.transform, "Left", new Vector3(-h, 0f, -h), 0f, 2f * h, panel, cap, null);
        }

        private static void Side(Transform parent, string name, Vector3 start, float yaw, float length,
                                 GameObject panel, GameObject cap, GameObject middle)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = start;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            var strip = go.AddComponent<UnityModularStrip>();
            strip.segmentLength = 1f;
            strip.length = length;
            strip.segment = panel;
            strip.cap = cap;
            strip.center = middle;
            strip.Rebuild();
        }
#endif
    }
}
