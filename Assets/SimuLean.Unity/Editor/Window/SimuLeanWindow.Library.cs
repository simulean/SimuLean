using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor
{
    public partial class SimuLeanWindow
    {
        private const float TileW = 92f, TileH = 96f, Thumb = 52f;
        private readonly Dictionary<string, Texture2D> previews = new Dictionary<string, Texture2D>();
        private static GUIStyle tileName, tileGlyph;
        private Entry pressed;
        private Vector2 pressPos;

        private const string PrefLibraryTools = "SimuLean_LibraryTools";
        private SimuLeanProjectSettings packSettings;
        private HashSet<string> scenePacks = new HashSet<string>();
        private double scenePacksAt = -10;

        private Vector2 elementsScroll, toolsScroll;

        private void DrawLibrary()
        {
            bool tools = EditorPrefs.GetBool(PrefLibraryTools, false);
            EditorGUILayout.BeginHorizontal();
            int pick = GUILayout.Toolbar(tools ? 1 : 0, new[] { "Elements", "Tools" }, EditorStyles.miniButton, GUILayout.Width(150));
            if ((pick == 1) != tools) { tools = pick == 1; EditorPrefs.SetBool(PrefLibraryTools, tools); }
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22))) { search = ""; GUI.FocusControl(null); }
            EditorGUILayout.EndHorizontal();
            var shown = ShownPacks();
            DrawPacks(shown);
            EditorGUILayout.LabelField(tools ? "Model tools: lists, downtimes, logic and data."
                                             : "Click a tile to place it at the view's centre, or drag it into the Scene view.", EditorStyles.miniLabel);

            bool visuals = PackSettings().showVisuals;
            var visible = entries.Where(e => e.IsTool == tools && shown.Contains(e.Pack) && (visuals || !e.Visual)
                                             && Matches(e.Name, e.Description)).ToList();
            // each tab keeps its own scroll position (switching packs or tabs does not jump to the top)
            var scrollPos = tools ? toolsScroll : elementsScroll;
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            float width = position.width - 24;
            int cols = Mathf.Max(1, Mathf.FloorToInt(width / (TileW + 4)));
            if (tools)
                foreach (string group in LibraryGroups)
                    Section(group, group, visible.Where(e => e.Group == group).ToList(), width, cols);
            else
            {
                foreach (string section in CoreSections)
                    Section(section, section == "Resources & paths" ? "Resources" : "Flow",
                            visible.Where(e => e.Pack == SimuLeanPacks.Core && e.Section == section).ToList(), width, cols);
                Section("Visuals", "Presets", visible.Where(e => e.Pack == SimuLeanPacks.Core && e.Visual).ToList(), width, cols);
                foreach (string pack in SimuLeanPacks.Optional)
                {
                    var own = visible.Where(e => e.Pack == pack).ToList();
                    if (own.Count == 0) continue;
                    GroupHeader(pack, "Flow", $"{own.Count}");
                    Tiles(own.Where(e => !e.Visual).ToList(), width, cols);
                    var decor = own.Where(e => e.Visual).ToList();
                    if (decor.Count > 0)
                    {
                        EditorGUILayout.LabelField("Visuals", EditorStyles.miniBoldLabel);
                        Tiles(decor, width, cols);
                    }
                }
            }
            // a search also finds what is hidden (a pack, the visuals): say so, with a switch
            if (!string.IsNullOrEmpty(search))
            {
                foreach (var g in entries.Where(e => e.IsTool == tools && !shown.Contains(e.Pack) && Matches(e.Name, e.Description)).GroupBy(e => e.Pack))
                    HiddenNote($"{g.Count()} more in the {g.Key} pack (hidden)", g.ToList(), () => SetPackShown(g.Key, true));
                if (!visuals)
                {
                    var hiddenVisuals = entries.Where(e => e.IsTool == tools && e.Visual && shown.Contains(e.Pack) && Matches(e.Name, e.Description)).ToList();
                    if (hiddenVisuals.Count > 0) HiddenNote($"{hiddenVisuals.Count} more among the visuals (hidden)", hiddenVisuals, () => SetVisualsShown(true));
                }
            }
            EditorGUILayout.EndScrollView();
            if (tools) toolsScroll = scrollPos; else elementsScroll = scrollPos;
        }

        private void Section(string title, string colourGroup, List<Entry> list, float width, int cols)
        {
            if (list.Count == 0) return;
            GroupHeader(title, colourGroup, $"{list.Count}");
            Tiles(list, width, cols);
        }

        private void Tiles(List<Entry> list, float width, int cols)
        {
            for (int i = 0; i < list.Count; i += cols)
            {
                var row = GUILayoutUtility.GetRect(width, TileH + 4);
                for (int k = 0; k < cols && i + k < list.Count; k++)
                    DrawTile(new Rect(row.x + k * (TileW + 4), row.y, TileW, TileH), list[i + k]);
            }
        }

        private static void HiddenNote(string text, List<Entry> found, Action show)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{text}: {string.Join(", ", found.Take(4).Select(e => e.Name))}" + (found.Count > 4 ? "…" : ""), EditorStyles.miniLabel);
            if (GUILayout.Button("Show", EditorStyles.miniButton, GUILayout.Width(48))) show();
            EditorGUILayout.EndHorizontal();
        }

        // the pack switches: the optional packs that have entries; a pack an open scene uses is shown while it does
        private void DrawPacks(HashSet<string> shown)
        {
            var packs = SimuLeanPacks.Optional.Where(p => entries.Any(e => e.Pack == p)).ToList();
            var settings = PackSettings();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent("Packs", "Sector libraries shown in the Library. Core (elements, resources, transport, " +
                                                      "storage, building kit, tools) is always shown. A pack the open scene uses is shown " +
                                                      "while it does. The choice is the project's (ProjectSettings/SimuLeanSettings.json)."),
                                       EditorStyles.miniBoldLabel, GUILayout.Width(40));
            foreach (string p in packs)
            {
                bool on = settings.shownPacks.Contains(p);
                bool inScene = !on && scenePacks.Contains(p);
                var label = new GUIContent(inScene ? p + " (in scene)" : p, inScene ? $"Hidden, but the open scene uses it: shown while it does." : "");
                bool now = GUILayout.Toggle(on || inScene, label, EditorStyles.miniButton, GUILayout.ExpandWidth(false));
                if (now != (on || inScene)) SetPackShown(p, now);
            }
            GUILayout.Space(12);
            bool vis = PackSettings().showVisuals;
            bool nowVis = GUILayout.Toggle(vis, new GUIContent("Visuals", "Decoration only — buildings, furniture, fences, decor of every shown " +
                                                               "pack: it adds models, no element, and a run is the same without it."),
                                           EditorStyles.miniButton, GUILayout.ExpandWidth(false));
            if (nowVis != vis) SetVisualsShown(nowVis);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void SetVisualsShown(bool shown)
        {
            var settings = PackSettings();
            settings.showVisuals = shown;
            settings.Save();
        }

        private SimuLeanProjectSettings PackSettings() => packSettings ?? (packSettings = SimuLeanProjectSettings.Load());

        /// <summary>Shows or hides a pack in the Library (the project's settings file).</summary>
        private void SetPackShown(string pack, bool shown)
        {
            var settings = PackSettings();
            if (shown && !settings.shownPacks.Contains(pack)) settings.shownPacks.Add(pack);
            if (!shown) settings.shownPacks.Remove(pack);
            settings.Save();
        }

        /// <summary>The packs whose entries the Library shows: core, the project's choice, and the packs of the components
        /// in the open scenes (looked up every 2 s).</summary>
        private HashSet<string> ShownPacks()
        {
            if (EditorApplication.timeSinceStartup - scenePacksAt > 2)
            {
                scenePacksAt = EditorApplication.timeSinceStartup;
                scenePacks = ScenePacks(entries);
            }
            var shown = new HashSet<string>(PackSettings().shownPacks) { SimuLeanPacks.Core };
            shown.UnionWith(scenePacks);
            return shown;
        }

        /// <summary>The packs of the Library types present in the open scenes.</summary>
        internal static HashSet<string> ScenePacks(List<Entry> entries)
        {
            var byType = new Dictionary<Type, string>();
            foreach (var e in entries) if (e.Type != null && e.Pack != SimuLeanPacks.Core) byType[e.Type] = e.Pack;
            var used = new HashSet<string>();
            if (byType.Count == 0) return used;
            foreach (var c in UnityCompat.FindAll<MonoBehaviour>(true))
                if (c != null && c.gameObject.scene.IsValid() && byType.TryGetValue(c.GetType(), out string p)) used.Add(p);
            return used;
        }

        private void DrawTile(Rect r, Entry e)
        {
            if (tileName == null)
            {
                tileName = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperCenter, wordWrap = true, clipping = TextClipping.Clip };
                tileGlyph = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 22 };
            }
            Color col = GroupColor(e.Group);
            bool hover = r.Contains(Event.current.mousePosition);
            EditorGUI.DrawRect(r, EditorGUIUtility.isProSkin ? new Color(0.22f, 0.22f, 0.24f) : new Color(0.86f, 0.86f, 0.88f));
            if (hover) EditorGUI.DrawRect(r, new Color(1, 1, 1, 0.06f));
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 3), col);

            var thumbRect = new Rect(r.x + (r.width - Thumb) / 2, r.y + 8, Thumb, Thumb);
            var tex = Preview(e);
            if (tex != null) GUI.DrawTexture(thumbRect, tex, ScaleMode.ScaleToFit);
            else
            {
                EditorGUI.DrawRect(thumbRect, new Color(col.r, col.g, col.b, 0.18f));
                GUI.Label(thumbRect, Initials(e.Name), tileGlyph);
            }
            GUI.Label(new Rect(r.x + 2, r.y + 8 + Thumb + 2, r.width - 4, r.height - Thumb - 10), new GUIContent(e.Name, e.Description), tileName);
            GUI.Label(r, new GUIContent("", $"{e.Name}\n{e.Description}"));   // tooltip over the whole tile

            // click = place at the view's centre; drag = place where dropped
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(ev.mousePosition)) { pressed = e; pressPos = ev.mousePosition; ev.Use(); }
            else if (ev.type == EventType.MouseDrag && pressed == e && Vector2.Distance(ev.mousePosition, pressPos) > 6)
            {
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = new Object[0];
                DragAndDrop.SetGenericData("SimuLeanEntry", e);
                DragAndDrop.StartDrag("Place " + e.Name);
                pressed = null;
                ev.Use();
            }
            else if (ev.type == EventType.MouseUp && pressed == e)
            {
                pressed = null;
                if (r.Contains(ev.mousePosition)) Place(e, ScenePivot());
                ev.Use();
            }
        }

        private static string Initials(string name)
        {
            var words = name.Split(' ', '-', '(').Where(w => w.Length > 0).ToArray();
            return words.Length >= 2 ? $"{char.ToUpper(words[0][0])}{char.ToUpper(words[1][0])}" : name.Substring(0, Mathf.Min(2, name.Length));
        }

        // the element's prefab, else a preset's icon asset ([SimuLeanPresetIcon]: a model / prefab preview or a texture)
        private Texture2D Preview(Entry e)
        {
            string path = e.HasPrefab ? e.PrefabPath : e.IconPath;
            if (string.IsNullOrEmpty(path)) return null;
            if (previews.TryGetValue(path, out var cached) && cached != null) return cached;
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) return null;
            if (asset is Texture2D texture) { previews[path] = texture; return texture; }
            var p = AssetPreview.GetAssetPreview(asset);
            if (p != null) { previews[path] = p; return p; }
            if (AssetPreview.IsLoadingAssetPreview(UnityCompat.Entity(asset))) needsRepaint = true;
            return null;
        }

        // ── placing ──────────────────────────────────────────────────────────

        private void OnSceneGUI(SceneView view)
        {
            var ev = Event.current;
            if (ev.type != EventType.DragUpdated && ev.type != EventType.DragPerform) return;
            if (!(DragAndDrop.GetGenericData("SimuLeanEntry") is Entry e)) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (ev.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                DragAndDrop.SetGenericData("SimuLeanEntry", null);
                Place(e, GroundPoint(HandleUtility.GUIPointToWorldRay(ev.mousePosition)));
            }
            ev.Use();
        }

        private static Vector3 GroundPoint(Ray ray)
        {
            if (Physics.Raycast(ray, out RaycastHit hit)) return hit.point;
            float d = ray.direction.y != 0f ? -ray.origin.y / ray.direction.y : 0f;
            return d > 0f ? ray.origin + ray.direction * d : ray.origin + ray.direction * 10f;
        }

        private static Vector3 ScenePivot()
        {
            var sv = SceneView.lastActiveSceneView;
            return sv == null ? Vector3.zero : new Vector3(sv.pivot.x, 0f, sv.pivot.z);
        }

        /// <summary>Creates the entry at <paramref name="position"/> (it lands in its hierarchy group by itself, or under
        /// <paramref name="parent"/>) and returns it (null = nothing created).</summary>
        internal static GameObject Place(Entry e, Vector3 position, Transform parent = null)
        {
            GameObject go = null;
            if (e.MenuPath != null)
            {
                Selection.activeObject = null;                    // presets parent to the selection otherwise
                // the menu method is called directly: through ExecuteMenuItem a GameObject ▸ … item opens the
                // Hierarchy's rename of the new object, and that rename, committed later, renamed whatever row was
                // under it (e.g. the scene's light got the preset's name)
                var p = e.MenuMethod?.GetParameters();
                if (e.MenuMethod != null && e.MenuMethod.IsStatic && p.Length <= 1)
                    e.MenuMethod.Invoke(null, p.Length == 1 ? new object[] { new MenuCommand(null) } : null);
                else
                    EditorApplication.ExecuteMenuItem(e.MenuPath);
                go = Selection.activeGameObject;
            }
            else if (e.HasPrefab)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(e.PrefabPath);
                if (prefab != null) go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (go != null) Undo.RegisterCreatedObjectUndo(go, "Place " + e.Name);
            }
            else if (e.Tool != null && e.Tool.OnMainCamera)
            {
                var cam = Camera.main != null ? Camera.main : UnityCompat.FindAny<Camera>();
                if (cam == null) return null;
                var added = Undo.AddComponent(cam.gameObject, e.Type);
                Select(added);
                return cam.gameObject;
            }
            if (go == null && e.Type != null)
            {
                go = new GameObject(e.Name);
                Undo.RegisterCreatedObjectUndo(go, "Place " + e.Name);
                Undo.AddComponent(go, e.Type);
            }
            if (go == null) return null;
            if (parent != null) Undo.SetTransformParent(go.transform, parent, "Place " + e.Name);
            if (go.transform.parent == null || parent != null)
            {
                // into its group first, then a name unique among its new siblings (not among another scene's roots)
                if (parent == null && ModelHierarchy.AutoOrganize) ModelHierarchy.Place(go);
                go.name = "";
                go.name = GameObjectUtility.GetUniqueNameForSibling(go.transform.parent, e.Name);
            }
            // a resource team placed from the library starts with one member (otherwise its requests never get served)
            if (go.GetComponent<UnityResourceTeam>() is UnityResourceTeam team && team.members.Count == 0)
            {
                Undo.RecordObject(team, "Place " + e.Name);
                team.AddMembers(1);
            }
            Undo.RecordObject(go.transform, "Place " + e.Name);
            go.transform.position = position;
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return go;
        }
    }
}
