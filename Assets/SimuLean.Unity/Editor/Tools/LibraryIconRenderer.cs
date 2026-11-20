using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Renders the Library icons of the presets whose model alone does not say what they are (the
    /// Shipbuilding pack first: most of its stations share the block workspace). Each icon is a small set of library models
    /// — the station and the work it handles (a plate, a kit, a block, a person) — in an isolated preview scene, seen from
    /// the front (the model frame: front −Z) three-quarters above, on a transparent background, saved as a PNG under
    /// <see cref="Folder"/> that the preset's <c>[SimuLeanPresetIcon]</c> names. Rerun it (Tools ▸ SimuLean ▸ Developer)
    /// after a model changes; the open scene is not touched.
    /// </summary>
    public static class LibraryIconRenderer
    {
        public const string Folder = "Assets/SimuLean.Unity/Editor/Icons/Shipbuilding/";
        public const int Size = 256;
        private const string M = LibraryPresets.Models;

        /// <summary>One model of an icon: asset (under Art/Models), position, yaw / roll (degrees), scale (uniform, or
        /// <see cref="Length"/> along the model's own Z to shorten a long plate or bundle).</summary>
        public readonly struct Piece
        {
            public readonly string Model; public readonly Vector3 At; public readonly float Yaw, Roll, Scale, Length;
            public Piece(string model, float x, float y, float z, float yaw = 0f, float roll = 0f, float scale = 1f, float length = 1f)
            { Model = model; At = new Vector3(x, y, z); Yaw = yaw; Roll = roll; Scale = scale; Length = length; }
        }

        private const string Workspace = "BlockWorkspace/BlockWorkspace.fbx", Scaffold = "BlockWorkspace/BlockScaffold.fbx",
                             Curved = "ShipBlock/ShipBlockCurved.fbx", Flat = "ShipBlock/ShipBlockFlat.fbx",
                             Plate = "ShipItems/SteelPlate.fbx", Kit = "ShipItems/PlateKit.fbx", Profiles = "ShipItems/ProfileBundle.fbx",
                             Console = "WorkstationProcessor/WorkstationProcessor.fbx", Positioner = "WeldPositioner/WeldPositioner.fbx",
                             Booth = "PaintBooth/PaintBooth.fbx", Gate = "SinkStation/SinkStation.fbx",
                             Fitter = "ShipyardPeople/Fitter.fbx", Welder = "ShipyardPeople/Welder.fbx",
                             Outfitter = "ShipyardPeople/Outfitter.fbx", Painter = "ShipyardPeople/Painter.fbx";

        /// <summary>The Shipbuilding icons: file name → its pieces (blocks and plates lie along the flow, +X).</summary>
        public static readonly IReadOnlyDictionary<string, Piece[]> Shipbuilding = new Dictionary<string, Piece[]>
        {
            ["SteelStockyard"] = Concat(Pile(Plate, 8, 0.12f, 90f, -1.4f, 0.4f), Pile(Plate, 5, 0.12f, 90f, 1.4f, 0.4f)),
            ["ProfileRack"] = Concat(Pile(Profiles, 4, 0.28f, 90f, -0.8f, 0.4f), Pile(Profiles, 3, 0.28f, 90f, 0.8f, 0.4f)),
            ["PlateCutting"] = new[] { new Piece(Plate, 0, 0.75f, 0, 90f, 0f, 1f, 0.4f), new Piece(Console, -3.2f, 0, -1.6f), new Piece(Fitter, -1.6f, 0, -1.9f) },
            ["PanelLine"] = new[] { new Piece(Positioner, -3f, 0, 0), new Piece(Positioner, 0, 0, 0), new Piece(Positioner, 3f, 0, 0),
                                    new Piece(Welder, 0, 0, -1.4f) },
            ["SubAssemblyCell"] = new[] { new Piece(Workspace, 0, 0, 0), new Piece(Kit, 0, 1.2f, 0, 90f, 0f, 1.6f), new Piece(Fitter, -2f, 1.2f, -4f) },
            ["SubBlockWelding"] = new[] { new Piece(Positioner, 0, 0, 0, 0f, 0f, 2f), new Piece(Welder, 0.5f, 0, -1.8f) },
            ["TurningStation"] = new[] { new Piece(Workspace, 0, 0, 0), new Piece(Flat, 0, 8.2f, 0, 90f, 90f) },
            ["BlockAssemblyBay"] = new[] { new Piece(Workspace, 0, 0, 0), new Piece(Curved, 0, 1.2f, 0, 90f), new Piece(Scaffold, -5f, 1.2f, -7f, 0f, 0f, 1.5f) },
            ["OutfittingStation"] = new[] { new Piece(Workspace, 0, 0, 0), new Piece(Curved, 0, 1.2f, 0, 90f), new Piece(Outfitter, -3f, 1.2f, -7.5f, 0f, 0f, 2f),
                                            new Piece(Outfitter, 3f, 1.2f, -7.5f, 0f, 0f, 2f) },
            ["BlastingPaintingHall"] = new[] { new Piece(Booth, 0, 0, 0), new Piece(Painter, -1.6f, 0, -2.4f) },
            ["SubBlockYard"] = new[] { new Piece(Workspace, -8.5f, 0, 0, 90f, 0f, 0.8f), new Piece(Flat, -8.5f, 1f, 0, 90f, 0f, 0.6f),
                                       new Piece(Workspace, 8.5f, 0, 0, 90f, 0f, 0.8f), new Piece(Flat, 8.5f, 1f, 0, 90f, 0f, 0.6f) },
            ["BlockYard"] = new[] { new Piece(Workspace, -10f, 0, 0, 90f), new Piece(Curved, -10f, 1.2f, 0, 0f, 0f, 0.9f),
                                    new Piece(Workspace, 10f, 0, 0, 90f), new Piece(Curved, 10f, 1.2f, 0, 0f, 0f, 0.9f) },
            ["Slipway"] = new[] { new Piece(Curved, -16f, 0, 0, 90f), new Piece(Curved, 0, 0, 0, 90f), new Piece(Flat, 16f, 0, 0, 90f) },
            ["BuildingDock"] = new[] { new Piece(Curved, -8f, 0, -6f, 90f), new Piece(Curved, 8f, 0, -6f, 90f),
                                       new Piece(Curved, -8f, 0, 6f, 90f), new Piece(Flat, 8f, 0, 6f, 90f) },
            ["Launch"] = new[] { new Piece(Curved, -16f, 0, 0, 90f), new Piece(Curved, 0, 0, 0, 90f), new Piece(Curved, 16f, 0, 0, 90f),
                                 new Piece(Gate, 27f, 0, 0, 0f, 0f, 3.5f) },
            ["BlockProcess"] = new[] { new Piece(Workspace, -18f, 0, 0, 90f, 0f, 0.8f), new Piece(Flat, -18f, 1f, 0, 90f, 0f, 0.6f),
                                       new Piece(Workspace, 0, 0, 0, 90f, 0f, 0.8f), new Piece(Curved, 0, 1f, 0, 0f, 0f, 0.7f),
                                       new Piece(Booth, 18f, 0, 0, 0f, 0f, 2.2f) },
        };

        private static List<Piece> Pile(string model, int n, float step, float yaw, float z, float length = 1f)
        {
            var list = new List<Piece>();
            for (int i = 0; i < n; i++) list.Add(new Piece(model, 0, i * step, z, yaw, 0f, 1f, length));
            return list;
        }

        private static Piece[] Concat(List<Piece> a, List<Piece> b) { a.AddRange(b); return a.ToArray(); }

        [MenuItem("Tools/SimuLean/Developer/Render Shipbuilding Icons", priority = 240)]
        private static void RenderMenu()
        {
            int n = RenderAll(Shipbuilding, Folder);
            Debug.Log($"[SimuLean] {n} Shipbuilding icons rendered in {Folder}");
        }

        /// <summary>Renders every icon of <paramref name="icons"/> into <paramref name="folder"/>; returns how many.</summary>
        public static int RenderAll(IReadOnlyDictionary<string, Piece[]> icons, string folder)
        {
            Directory.CreateDirectory(folder);
            int n = 0;
            foreach (var kv in icons)
            {
                File.WriteAllBytes(Path.Combine(folder, kv.Key + ".png"), Render(kv.Value, Size).EncodeToPNG());
                n++;
            }
            AssetDatabase.Refresh();
            foreach (var kv in icons) MakeIcon(folder + kv.Key + ".png");
            return n;
        }

        /// <summary>An icon texture: no mipmaps, its alpha kept, not compressed (the Library draws it as it is).</summary>
        public static void MakeIcon(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) return;
            ti.textureType = TextureImporterType.GUI;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SaveAndReimport();
        }

        /// <summary>The pieces in a preview scene, framed from the front three-quarters above, on a transparent
        /// background (<paramref name="size"/> pixels square).</summary>
        public static Texture2D Render(IEnumerable<Piece> pieces, int size)
        {
            var preview = new PreviewRenderUtility();
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            var previous = RenderTexture.active;
            try
            {
                Bounds? bounds = null;
                foreach (var p in pieces)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(M + p.Model);
                    if (asset == null) { Debug.LogWarning($"[SimuLean] icon model {M + p.Model} not found"); continue; }
                    var go = Object.Instantiate(asset);
                    go.transform.SetPositionAndRotation(p.At, Quaternion.Euler(0f, p.Yaw, 0f) * Quaternion.Euler(0f, 0f, p.Roll) * asset.transform.rotation);
                    var s = asset.transform.localScale * p.Scale;
                    go.transform.localScale = new Vector3(s.x, s.y, s.z * p.Length);
                    preview.AddSingleGO(go);
                    foreach (var r in go.GetComponentsInChildren<Renderer>())
                    {
                        if (!r.enabled) continue;
                        if (bounds == null) bounds = r.bounds; else { var b = bounds.Value; b.Encapsulate(r.bounds); bounds = b; }
                    }
                }
                Bounds box = bounds ?? new Bounds(Vector3.zero, Vector3.one);
                const float fov = 24f, elevation = 28f, azimuth = 30f;
                float distance = box.extents.magnitude / Mathf.Sin(fov * 0.5f * Mathf.Deg2Rad) * 0.95f;
                var cam = preview.camera;
                cam.fieldOfView = fov;
                cam.transform.rotation = Quaternion.Euler(elevation, azimuth, 0f);
                cam.transform.position = box.center - cam.transform.forward * distance;
                cam.nearClipPlane = Mathf.Max(0.05f, distance - box.extents.magnitude * 2f);
                cam.farClipPlane = distance + box.extents.magnitude * 2f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                preview.lights[0].intensity = 1.2f;
                preview.lights[0].transform.rotation = Quaternion.Euler(45f, 20f, 0f);
                preview.lights[1].intensity = 0.6f;
                preview.ambientColor = new Color(0.45f, 0.45f, 0.5f);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                image.Apply();
                return image;
            }
            finally
            {
                preview.camera.targetTexture = null;
                RenderTexture.active = previous;
                preview.Cleanup();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
