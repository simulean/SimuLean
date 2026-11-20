using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Moves scenes and prefabs saved before the library's model frame (front −Z, flow +X; architecture
    /// §4.6.1) to it, so they look the same. The frame turned some element types' views (their axes) and some library
    /// models (their FBX); every object whose content turned gets the opposite turn on its own rotation, and every child
    /// laid out in its parent's former axes is turned with it, so nothing moves in the world:
    /// <c>local' = R(parent's turn) · local · R(−own turn)</c> (a quarter turn also swaps the X / Z scale). The element
    /// fields that are offsets in its own axes are turned too. A library model's instance is one piece (its inside is the
    /// model's); a prefab's own children follow their prefab (only a changed position / rotation of them is turned).
    /// Each <see cref="Step"/> is what turned in one change (<see cref="Stations"/>, <see cref="Hospital"/> and
    /// <see cref="QueueLines"/>); run each once, in order, over the scenes and prefabs saved before it (<i>Tools ▸ SimuLean ▸ Developer</i>).
    /// </summary>
    public static class ModelFrameMigration
    {
        /// <summary>One migration: what turned on a date. Each runs once over the scenes saved before it.</summary>
        public sealed class Step
        {
            public string Name, Since;
            /// <summary>Element types whose view axes turned (degrees about the vertical: new axes = R(turn) · former
            /// ones); <see cref="ElementTurn"/> may refine it for one element (e.g. by its layout).</summary>
            public IReadOnlyDictionary<Type, float> ElementTurns = new Dictionary<Type, float>();
            /// <summary>A turn for one element (null = its type's in <see cref="ElementTurns"/>, if any).</summary>
            public Func<SElement, float?> ElementTurn = _ => null;
            /// <summary>Library models whose FBX turned (asset path → degrees).</summary>
            public IReadOnlyDictionary<string, float> ModelTurns = new Dictionary<string, float>();
            /// <summary>Element fields that are offsets in the element's own axes.</summary>
            public IReadOnlyDictionary<Type, string[]> LocalVectors = new Dictionary<Type, string[]>();
        }

        /// <summary>The model frame: the stations, the views of sources, sinks, lanes, stackers and the
        /// station elements. The element turns are those the Library used to give them when placed.</summary>
        public static readonly Step Stations = new Step
        {
            Name = "Model Frame", Since = "2026-11-02",
            ElementTurns = new Dictionary<Type, float>
            {
                { typeof(UnitySource), -90f }, { typeof(UnitySink), -90f }, { typeof(UnityStacker), -90f },
                { typeof(UnityLengthLimitedQueue), 90f },
                { typeof(UnityMultiServer), 180f }, { typeof(UnityCombiner), 180f }, { typeof(UnityAssemblyStation), 180f },
                { typeof(UnitySeparator), 180f }, { typeof(UnityPickingStation), 180f }, { typeof(UnitySequencingStation), 180f },
            },
            // a sink that lays its kept items by their position labels keeps its axes: the labels are coordinates in them
            ElementTurn = e => e is UnitySink sink && sink.keepItems && !string.IsNullOrWhiteSpace(sink.positionLabels) ? 0f : (float?)null,
            ModelTurns = StationModelTurns(),
            LocalVectors = new Dictionary<Type, string[]>
            {
                { typeof(UnitySeparator), new[] { "firstSlot", "slotStep" } },
                { typeof(UnitySequencingStation), new[] { "carrierOffset", "pickOffset", "orderOffset", "orderStep" } },
                { typeof(UnityStacker), new[] { "firstPosition", "positionStep", "pickOffset" } },
            },
        };

        /// <summary>The hospital furniture and the Paced Line in the model frame: the reception desk, the
        /// consultation box and the hospital door export turned 180° (their front — public side, curtain, the building's
        /// inside — on −Z); a waiting room's patients face its −Z (a queue in the Seats layout with seats); a straight Paced
        /// Line runs along its +X (it ran along +Z).</summary>
        public static readonly Step Hospital = new Step
        {
            Name = "Hospital Furniture and Paced Line", Since = "2026-11-03",
            ElementTurns = new Dictionary<Type, float> { { typeof(UnityPacedLine), 90f } },
            ElementTurn = e => e is UnityQueue q && q.layoutMode == QueueLayoutMode.Seats && q.Seats().Count > 0 ? 180f : (float?)null,
            ModelTurns = new Dictionary<string, float>
            {
                { LibraryPresets.Models + "ConsultationBox/ConsultationBox.fbx", 180f },
                { LibraryPresets.Models + "ReceptionDesk/ReceptionDesk.fbx", 180f },
                { LibraryPresets.Models + "HospitalDoor/HospitalDoor.fbx", 180f },
            },
        };

        /// <summary>The queues' Line and Grid layouts in the model frame: a line runs along +X (it ran along +Z)
        /// — a Line queue turns −90° so its line stays where it was; a grid fills from its exit side, +X (a Grid queue
        /// keeps its axes: only its items' order and heading change).</summary>
        public static readonly Step QueueLines = new Step
        {
            Name = "Queue Lines", Since = "2026-11-03",
            ElementTurn = e => e is UnityQueue q && q.layoutMode == QueueLayoutMode.Line ? -90f : (float?)null,
        };

        private static Dictionary<string, float> StationModelTurns()
        {
            var d = new Dictionary<string, float>(HandMadeModelFrame.Turns);
            foreach (var m in new[] { "AssemblyBench", "CncMachine", "InspectionStation", "PaintBooth", "Palletizer", "WeldPositioner" })
                d[$"{LibraryPresets.Models}{m}/{m}.fbx"] = 180f;
            return d;
        }

        [MenuItem("Tools/SimuLean/Developer/Migrate Scenes to the Model Frame", priority = 230)]
        private static void MigrateMenu() => Menu(Stations);

        [MenuItem("Tools/SimuLean/Developer/Migrate Scenes: Hospital Furniture and Paced Line", priority = 231)]
        private static void MigrateHospitalMenu() => Menu(Hospital);

        [MenuItem("Tools/SimuLean/Developer/Migrate Scenes: Queue Lines", priority = 232)]
        private static void MigrateQueueLinesMenu() => Menu(QueueLines);

        private static void Menu(Step step)
        {
            if (!EditorUtility.DisplayDialog("Migrate: " + step.Name,
                    "Turns every scene under Assets/Scenes and every prefab under Assets into the library's model frame " +
                    $"(front −Z, flow +X) for what changed on {step.Since}, so they look the same. Run it once, on scenes " +
                    $"saved before {step.Since}. Continue?", "Migrate", "Cancel")) return;
            Debug.Log("[SimuLean] " + MigrateAll(step, true));
        }

        /// <summary>Migrates by <paramref name="step"/> (or, with <paramref name="apply"/> false, only lists) every prefab
        /// under <c>Assets</c> and every scene under <paramref name="sceneFolder"/>; the open scene is reopened afterwards.
        /// Returns the report.</summary>
        public static string MigrateAll(Step step, bool apply, string sceneFolder = "Assets/Scenes")
        {
            var report = new StringBuilder();
            string open = EditorSceneManager.GetActiveScene().path;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith(LibraryPresets.Models)) continue;
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var lines = new List<string>();
                    if (ModelRoot(root.transform, out _)) continue;   // a variant of a library model: the model's own
                    // the asset's root stays (its instances are turned in their scenes); its content turns
                    float own = OwnTurn(step, root.transform);
                    if (own != 0f) TurnFields(step, root.transform, own, apply, lines);
                    foreach (Transform child in root.transform) Process(step, child, own, apply, lines);
                    if (lines.Count > 0)
                    {
                        report.AppendLine($"{path}: {lines.Count}");
                        foreach (var l in lines) report.AppendLine("  " + l);
                        if (apply) PrefabUtility.SaveAsPrefabAsset(root, path);
                    }
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { sceneFolder }))
                MigrateScene(step, AssetDatabase.GUIDToAssetPath(guid), apply, report);
            if (!string.IsNullOrEmpty(open)) EditorSceneManager.OpenScene(open, OpenSceneMode.Single);
            return report.Length == 0 ? "nothing to migrate" : report.ToString();
        }

        /// <summary>Migrates (or lists) one scene by <paramref name="step"/>: opens it, turns it and saves it when anything
        /// changed. Returns the number of objects turned; the lines go to <paramref name="report"/>.</summary>
        public static int MigrateScene(Step step, string path, bool apply, StringBuilder report = null)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var lines = new List<string>();
            foreach (var go in scene.GetRootGameObjects()) Process(step, go.transform, 0f, apply, lines);
            if (lines.Count == 0) return 0;
            report?.AppendLine($"{path}: {lines.Count}");
            foreach (var l in lines) report?.AppendLine("  " + l);
            if (apply)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            return lines.Count;
        }

        // the turn of t's own content: its element type's, else (an instance of a library model) the model's
        private static float OwnTurn(Step step, Transform t)
        {
            foreach (var c in t.GetComponents<SElement>())
            {
                if (c == null) continue;
                float? one = step.ElementTurn(c);
                if (one.HasValue) return one.Value;
                if (step.ElementTurns.TryGetValue(c.GetType(), out float y)) return y;
            }
            if (ModelRoot(t, out string asset) && step.ModelTurns.TryGetValue(asset, out float m)) return m;
            return 0f;
        }

        // the outermost object of an instance of a library model (an FBX)
        private static bool ModelRoot(Transform t, out string asset)
        {
            asset = null;
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) return false;
            asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
            return asset != null && asset.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        private static void Process(Step step, Transform t, float parentTurn, bool apply, List<string> lines)
        {
            float own = OwnTurn(step, t);
            // a child owned by a prefab instance follows its prefab, unless its pose is changed on the instance
            bool owned = PrefabUtility.IsPartOfPrefabInstance(t.gameObject) && !PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)
                         && !PrefabUtility.IsAddedGameObjectOverride(t.gameObject);
            bool poseChanged = !owned || PoseOverridden(t);
            if ((parentTurn != 0f || own != 0f) && poseChanged)
            {
                lines.Add($"{Path(t)}: parent {parentTurn:0}°, own {own:0}°");
                if (apply)
                {
                    Undo.RecordObject(t, "Model frame");
                    Quaternion p = Quaternion.Euler(0f, parentTurn, 0f);
                    t.localPosition = Round(p * t.localPosition);
                    t.localRotation = p * t.localRotation * Quaternion.Inverse(ContentTurn(t, own));
                    if (Mathf.Abs(Mathf.DeltaAngle(own, 90f)) < 1f || Mathf.Abs(Mathf.DeltaAngle(own, -90f)) < 1f)
                    {
                        var s = t.localScale;
                        t.localScale = new Vector3(s.z, s.y, s.x);
                    }
                    EditorUtility.SetDirty(t);
                }
            }
            if (own != 0f) TurnFields(step, t, own, apply, lines);
            if (ModelRoot(t, out _)) return;                      // the model's inside is its own
            foreach (Transform child in t) Process(step, child, own, apply, lines);
        }

        // the turn of t's content in t's own axes: an element's about its vertical; a library model's about the model's
        // vertical (HandMadeModelFrame turns it in the axes of the model's root, which may lay a Z-up model down)
        private static Quaternion ContentTurn(Transform t, float own)
        {
            Quaternion q = Quaternion.Euler(0f, own, 0f);
            if (own == 0f || !ModelRoot(t, out string asset)) return q;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            if (model == null) return q;
            Quaternion r = model.transform.localRotation;
            return Quaternion.Inverse(r) * q * r;
        }

        private static bool PoseOverridden(Transform t)
        {
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(t);
            if (root == null || source == null) return false;
            var mods = PrefabUtility.GetPropertyModifications(root);
            return mods != null && mods.Any(m => m.target == source &&
                                              (m.propertyPath.StartsWith("m_LocalPosition") || m.propertyPath.StartsWith("m_LocalRotation")));
        }

        private static void TurnFields(Step step, Transform t, float own, bool apply, List<string> lines)
        {
            foreach (var c in t.GetComponents<SElement>())
            {
                if (c == null || !step.LocalVectors.TryGetValue(c.GetType(), out var fields)) continue;
                var so = new SerializedObject(c);
                Quaternion q = Quaternion.Euler(0f, own, 0f);
                bool instance = PrefabUtility.IsPartOfPrefabInstance(c);
                foreach (var f in fields)
                {
                    var prop = so.FindProperty(f);
                    if (prop == null || prop.propertyType != SerializedPropertyType.Vector3) continue;
                    if (instance && !prop.prefabOverride) continue;     // its prefab's value, turned with the prefab
                    Vector3 turned = q * prop.vector3Value;
                    if (Approximately(turned, prop.vector3Value)) continue;
                    lines.Add($"{Path(t)}.{f}: {prop.vector3Value} → {turned}");
                    prop.vector3Value = Round(turned);
                }
                if (apply) so.ApplyModifiedProperties();
            }
        }

        private static bool Approximately(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-10f;

        // a quarter / half turn of round numbers stays round (no 1e-7 residues in the scene files; 0.01 mm)
        private static Vector3 Round(Vector3 v)
            => new Vector3((float)Math.Round(v.x, 5), (float)Math.Round(v.y, 5), (float)Math.Round(v.z, 5));

        private static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
    }
}
