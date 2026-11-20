using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SimuLean.Optimization;
using SimuLean.Results;
using SimuLean.Serialization;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// SimuLean commands of the Unity pipeline / MCP: build a model in the open scene and study
    /// it headless without <c>eval</c> C#. Self-describing: <c>simulean_types</c> lists what can be placed (the Library's
    /// entries), <c>simulean_params</c> an element's fields (what <c>simulean_set</c> writes) and its catalog keys (what
    /// <c>simulean_run_headless --set</c> varies). Elements are named by their GameObject name or hierarchy path.
    /// Scene changes are undoable; runs never change the scene. Every answer carries <c>errors</c> and <c>warnings</c>:
    /// what was logged while the command ran (<see cref="AgentReply"/>).
    /// </summary>
    public static partial class SimuLeanCommands
    {
        private const string Tag = "simulean";

        // ── describing ───────────────────────────────────────────────────────

        [CliCommand("simulean_types", "List the SimuLean element types and tools that can be placed (the Library's entries: by " +
                                      "default the Core pack's, each other pack as one line; pack=<name> or pack=all lists them); with " +
                                      "'type', that type's fields (simulean_set), the keys of every distribution, the fields of its " +
                                      "lists' items and its catalog keys (simulean_run_headless factors; textFactors: the text settings, varied by texts). Compact by default: the groups " +
                                      "every element shares are counted, not listed (detail=full or fields=<group> lists them).", Tags = new[] { Tag })]
        public static object Types([CliArg("type", "A Library name, component type or engine type, in any case, with or without spaces / hyphens " +
                                               "(e.g. 'Multi-Server', 'MultiServer', 'UnityMultiServer'); several comma-separated ('Queue,Sink'); empty = all.")] string type = "",
                                   [CliArg("detail", "Fields shown: " + DetailHelp + ".")] string detail = "compact",
                                   [CliArg("fields", "Only the fields and catalog keys under these paths, comma-separated (e.g. 'processBy,outputStrategy').")] string fields = "",
                                   [CliArg("pack", "Without 'type': the entries listed — empty = the Core pack's (decoration left out) plus one line per other " +
                                                   "pack; a pack (Core, Manufacturing, Warehousing, Healthcare, Shipbuilding), a group (Flow, Presets, Resources, " +
                                                   "Transport…) or a module (Storage, Processors…); all = every entry.")] string pack = "")
            => AgentReply.Of(() =>
            {
                // several types in one call
                var names = (type ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
                if (names.Count == 0) return TypeList(pack);
                if (!string.IsNullOrWhiteSpace(pack)) throw new ArgumentException("pack filters the list (no type): give type or pack, not both.");
                if (names.Count <= 1) return TypesOf(names[0], detail, fields);
                // each type on its own: one unknown name does not lose the others; a type without a field under the
                // filter answers an empty list (the filter is the caller's question to every type, not an error)
                var types = new List<object>();
                int size = 0;
                var briefed = new List<string>();
                foreach (string n in names)
                {
                    object one;
                    try { one = TypesOf(n, detail, fields); }
                    catch (ArgumentException e) when (!string.IsNullOrWhiteSpace(fields) && e.Message.StartsWith("fields: no field under", StringComparison.Ordinal))
                    { one = new Dictionary<string, object> { ["name"] = n, ["fields"] = new object[0], ["note"] = e.Message.Substring("fields: ".Length) }; }
                    catch (ArgumentException e) { one = new { type = n, error = e.Message }; }
                    int length = AnswerSize(one);
                    // past the size an agent's tool takes, the rest by name and description only (a spilled answer is unread)
                    if (types.Count > 0 && size + length > MaxAnswer && one is Dictionary<string, object> d)
                    {
                        types.Add(new Dictionary<string, object> { ["name"] = d["name"], ["description"] = d.TryGetValue("description", out var x) ? x : null });
                        briefed.Add((string)d["name"]);
                        continue;
                    }
                    size += length;
                    types.Add(one);
                }
                if (briefed.Count == 0) return (object)new { types };
                return new { types, note = $"{string.Join(", ", briefed)}: name and description only (the answer would pass ~{MaxAnswer / 1000} KB) — " +
                                           "ask them one at a time or with fields=." };
            });

        /// <summary>The characters (indented, as an MCP client shows an answer) above which an answer is cut or paged, saying
        /// how to read the rest: a study's (<see cref="AnswerPages.Study"/>), a multi-type or long <c>simulean_types</c> list,
        /// <c>simulean_vocabulary</c> (an index of its sections). Above ~50 KB an agent's tool spills the answer to a file it
        /// may not read. A test lowers it.</summary>
        internal static int MaxAnswer = AnswerPages.MaxChars;

        /// <summary>The length of an answer as an MCP client shows it (indented JSON).</summary>
        internal static int AnswerSize(object answer) => Newtonsoft.Json.JsonConvert.SerializeObject(answer, Newtonsoft.Json.Formatting.Indented).Length;

        [Newtonsoft.Json.JsonObject(ItemNullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        private sealed class TypeRow
        {
            [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)] public string name, group, module, pack, component, variantOf, description, places;
            [Newtonsoft.Json.JsonIgnore] public bool decor;
            [Newtonsoft.Json.JsonIgnore] public SimuLeanWindow.Entry entry;
        }

        /// <summary>
        /// The list of <c>simulean_types</c> without a type (~130 entries, mostly sector and
        /// decoration presets, in every first call). Default: the Core pack's entries without decoration, each other pack and the
        /// decoration as one line (count and names); <paramref name="pack"/> = a pack, a group or a module lists those (with
        /// decoration), <c>all</c> every entry. A preset names the type it places (<c>variantOf</c>: Pallet Rack → Storage Rack).
        /// </summary>
        private static object TypeList(string pack)
        {
            var entries = SimuLeanWindow.CollectEntries();
            var rows = entries.Where(e => !e.Hidden).Select(e => new TypeRow
            {
                name = e.Name, group = e.Group, module = e.Subgroup, pack = e.Pack, component = e.Type?.Name, description = e.Description,
                variantOf = e.VariantOf != null ? entries.FirstOrDefault(x => x.Type == e.VariantOf && x.MenuPath == null)?.Name ?? e.VariantOf.Name : null,
                decor = e.Visual, entry = e,
            }).Concat(OtherTools(entries).Select(t => new TypeRow
            {
                name = t.info.DisplayName, group = t.info.Category, pack = string.IsNullOrEmpty(t.info.Pack) ? SimuLeanPacks.Core : t.info.Pack,
                component = t.type.Name, description = t.info.Description,
            })).ToList();
            string key = (pack ?? "").Trim();
            if (string.Equals(key, "all", StringComparison.OrdinalIgnoreCase)) return Listing(rows, null);
            if (key.Length > 0)
            {
                bool Is(string v) => string.Equals(v, key, StringComparison.OrdinalIgnoreCase);
                var chosen = rows.Where(r => Is(r.pack)).ToList();
                if (chosen.Count == 0) chosen = rows.Where(r => Is(r.group) || Is(r.module)).ToList();
                if (chosen.Count == 0)
                {
                    var options = rows.Select(r => r.pack).Concat(rows.Select(r => r.group)).Concat(rows.Select(r => r.module))
                                      .Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                    throw new ArgumentException($"pack: no pack, group or module '{pack}'." + SimuLean.Serialization.NameSuggestions.DidYouMean(key, options) +
                                                $" Options: all, {string.Join(", ", options)}.");
                }
                PresetSummaries(chosen);
                return Listing(chosen, key);
            }
            var core = rows.Where(r => r.pack == SimuLeanPacks.Core && !r.decor).ToList();
            var packs = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var g in rows.Where(r => r.pack != SimuLeanPacks.Core).GroupBy(r => r.pack))
            {
                var names = g.Where(r => !r.decor).Select(r => r.name).ToList();
                int decor = g.Count(r => r.decor);
                packs[g.Key] = $"{g.Count()} entries{(decor > 0 ? $" ({decor} decoration)" : "")}: " +
                               string.Join(", ", names.Take(10)) + (names.Count > 10 ? ", …" : "");
            }
            var coreDecor = rows.Where(r => r.pack == SimuLeanPacks.Core && r.decor).Select(r => r.name).ToList();
            if (coreDecor.Count > 0) packs["Core decoration"] = $"{coreDecor.Count} entries: {string.Join(", ", coreDecor)}";
            return new
            {
                types = core, packs,
                more = "pack=<name> lists a pack (or a group / module: pack=Presets), pack=all every entry; type=<name> describes one " +
                       "(any spelling; a preset's variantOf is the type whose fields it sets).",
            };
        }

        /// <summary>A listing of <c>simulean_types</c> (<c>pack=all</c>, a pack, a group): whole when it fits in
        /// <see cref="MaxAnswer"/>; else its first rows whole and the others by name, pack and group, with a note that says
        /// how to read them (a pack or a group at a time, a type by name).</summary>
        private static object Listing(List<TypeRow> rows, string pack)
        {
            var answer = new Dictionary<string, object>();
            if (pack != null) answer["pack"] = pack;
            answer["types"] = rows;
            if (AnswerSize(answer) <= MaxAnswer) return answer;
            var listed = new List<object>();
            var brief = new List<string>();
            int size = 0, room = MaxAnswer - 2000;   // the note and the brief rows
            foreach (var r in rows)
            {
                int length = AnswerSize(r);
                if (brief.Count == 0 && size + length <= room) { size += length; listed.Add(r); continue; }
                listed.Add(new Dictionary<string, object> { ["name"] = r.name, ["pack"] = r.pack, ["group"] = r.group });
                brief.Add(r.name);
            }
            answer["types"] = listed;
            answer["note"] = $"{brief.Count} of {rows.Count} entries by name, pack and group only (the whole list would pass ~{MaxAnswer / 1000} KB): " +
                             "pack=<pack> or pack=<group> lists them whole, type=<name> describes one.";
            return answer;
        }

        // ── what a preset places ───────────────────────────────

        // a preset's summary by its name, made once per editor session (placing it again gives the same values)
        private static readonly Dictionary<string, string> presetPlaces = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Each preset row of a pack listing gets <c>places</c>: the element type it places and its key defaults —
        /// <c>Multi-Server · capacity 2 · delayStrategy Triangular~5~10~20</c> (the Healthcare pack listed names
        /// only). The preset is placed once in a temporary additive scene (closed without saving; the open scenes are not
        /// touched) and its element read; decoration and composite pathways (several elements) are left out, and nothing
        /// is placed in Play or while the scene cannot get a companion scene.
        /// </summary>
        private static void PresetSummaries(List<TypeRow> rows)
        {
            var todo = rows.Where(r => r.entry != null && r.entry.MenuPath != null && !r.decor && r.entry.VariantOf != typeof(SElement)).ToList();
            if (todo.Count == 0) return;
            foreach (var r in todo) if (presetPlaces.TryGetValue(r.name, out string s)) r.places = s;
            var missing = todo.Where(r => r.places == null && !presetPlaces.ContainsKey(r.name)).ToList();
            if (missing.Count == 0 || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(active.path)) return;   // an untitled scene cannot have an untitled companion
            var selection = Selection.objects;
            UnityEngine.SceneManagement.Scene temp = default;
            bool log = Debug.unityLogger.logEnabled;
            try
            {
                temp = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(temp);
                Debug.unityLogger.logEnabled = false;
                foreach (var r in missing)
                {
                    string summary = null;
                    try
                    {
                        var go = SimuLeanWindow.Place(r.entry, Vector3.zero);
                        var elements = go != null ? go.GetComponentsInChildren<SElement>(true) : new SElement[0];
                        var teams = go != null ? go.GetComponentsInChildren<UnityResourceTeam>(true) : new UnityResourceTeam[0];
                        if (elements.Length == 1) summary = PresetSummary(elements[0]);
                        else if (elements.Length == 0 && teams.Length == 1) summary = PresetSummary(teams[0]);
                        if (go != null) Object.DestroyImmediate(go.transform.root.gameObject);
                    }
                    catch (Exception) { summary = null; }
                    presetPlaces[r.name] = summary;
                    r.places = summary;
                }
            }
            finally
            {
                Debug.unityLogger.logEnabled = log;
                if (temp.IsValid())
                {
                    if (active.IsValid()) UnityEngine.SceneManagement.SceneManager.SetActiveScene(active);
                    EditorSceneManager.CloseScene(temp, true);
                }
                Selection.objects = selection;
            }
        }

        // a team preset: its members, travel and speed
        private static string PresetSummary(UnityResourceTeam t)
            => Tidy($"Resource Team · members {t.members.Count} · travel {t.travel} · speed {t.speed.ToString(CultureInfo.InvariantCulture)} ({t.units})");

        // float fields read as doubles (0.10000000149011612): shown with 7 significant digits
        private static string Tidy(string text)
            => System.Text.RegularExpressions.Regex.Replace(text, @"\d+\.\d{8,}", m =>
                double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v.ToString("G7", CultureInfo.InvariantCulture) : m.Value);

        // the element's type (its Library name) and its key settings: capacity, its time / arrivals as a distribution text
        private static string PresetSummary(SElement e)
        {
            var entry = SimuLeanWindow.CollectEntries().FirstOrDefault(x => x.Type == e.GetType() && x.MenuPath == null);
            var parts = new List<string> { entry?.Name ?? e.GetType().Name };
            var fields = Collapsed(Fields(e));
            foreach (string key in new[] { "mode", "capacity", "delayStrategy", "interArrivalDistribution", "processingTime" })
            {
                var f = fields.FirstOrDefault(x => x.path == key);
                if (f != null && !string.IsNullOrEmpty(f.value)) parts.Add(key + " " + f.value);
            }
            if (e.processBy != null && e.processBy.IsUsed && e is IProcessResourceUser)
                parts.Add("processBy " + (e.processBy.EffectiveTeam != null ? e.processBy.EffectiveTeam.name : "?"));
            return Tidy(string.Join(" · ", parts));
        }

        private static object TypesOf(string type, string detail = "compact", string fields = "")
        {
            var entries = SimuLeanWindow.CollectEntries();
            var entry = TryFindEntry(entries, type);
            if (entry == null)
            {
                var tool = FindTool(type, entries);
                entry = new SimuLeanWindow.Entry { Name = tool.info.DisplayName, Description = tool.info.Description, Type = tool.type, PrefabPath = "" };
            }
            if (entry.Type == null) return new { name = entry.Name, description = entry.Description, note = "A preset: place it and inspect the objects it creates." };
            // a throwaway instance (hidden, never saved) gives the values the Library places: its prefab's when it has one
            // (a fresh component's defaults are not what the Library puts in the scene)
            var prefab = entry.HasPrefab ? AssetDatabase.LoadAssetAtPath<GameObject>(entry.PrefabPath) : null;
            var holder = prefab != null ? Object.Instantiate(prefab) : new GameObject("simulean_types");
            holder.hideFlags = HideFlags.HideAndDontSave;
            bool log = Debug.unityLogger.logEnabled;
            try
            {
                var c = holder.GetComponent(entry.Type) ?? holder.AddComponent(entry.Type);
                Debug.unityLogger.logEnabled = false;          // extraction warnings of an empty element
                var answer = new Dictionary<string, object> { ["name"] = entry.Name, ["component"] = entry.Type.Name, ["description"] = entry.Description };
                foreach (var kv in FieldsView(c, null, detail, fields)) answer[kv.Key] = kv.Value;
                answer["catalog"] = CatalogUnder(CatalogOf(holder, c, out var texts), fields);
                answer["textFactors"] = TextFactorsUnder(texts, fields);
                return answer;
            }
            finally
            {
                Debug.unityLogger.logEnabled = log;
                Object.DestroyImmediate(holder);
            }
        }

        [CliCommand("simulean_params", "An element's (or tool's) fields with their values — the keys simulean_set writes —, the keys " +
                                       "of every distribution, the fields of its lists' items and its catalog keys (the parameters a study " +
                                       "varies; textFactors: the text settings, varied by the texts given, with their current text). Compact by default: the groups every element shares are listed only where changed " +
                                       "(detail=full or fields=<group> lists them).", Tags = new[] { Tag })]
        public static object Params([CliArg("object", "The element or tool (GameObject name or hierarchy path).", Required = true)] string @object,
                                    [CliArg("detail", "Fields shown: " + DetailHelp + ".")] string detail = "compact",
                                    [CliArg("fields", "Only the fields and catalog keys under these paths, comma-separated (e.g. 'processBy,outputStrategy').")] string fields = "")
            => AgentReply.Of(() => ParamsOf(@object, detail, fields));

        private static object ParamsOf(string element, string detail = "compact", string fields = "")
        {
            var go = FindObject(element);
            var c = SimuLeanComponent(go);
            var answer = new Dictionary<string, object> { ["object"] = go.name, ["path"] = PathOf(go.transform), ["component"] = c.GetType().Name };
            // a fresh component of its type: the shared fields at their defaults are left out of a compact answer
            var holder = new GameObject("simulean_params") { hideFlags = HideFlags.HideAndDontSave };
            bool log = Debug.unityLogger.logEnabled;
            try
            {
                Debug.unityLogger.logEnabled = false;
                var fresh = holder.AddComponent(c.GetType());
                Debug.unityLogger.logEnabled = log;
                foreach (var kv in FieldsView(c, fresh, detail, fields)) answer[kv.Key] = kv.Value;
            }
            finally
            {
                Debug.unityLogger.logEnabled = log;
                Object.DestroyImmediate(holder);
            }
            answer["catalog"] = CatalogUnder(CatalogOf(null, c, out var texts), fields);
            answer["textFactors"] = TextFactorsUnder(texts, fields);
            return answer;
        }

        // ── building ─────────────────────────────────────────────────────────

        [CliCommand("simulean_add_element", "Place a Library entry (element, tool or preset) in the open scene, as the SimuLean " +
                                            "window does. Undoable.", Tags = new[] { Tag })]
        public static object AddElement(
            [CliArg("type", "A Library name or component type (simulean_types).", Required = true)] string type,
            [CliArg("name", "GameObject name (empty = the Library's, made unique).")] string name = "",
            [CliArg("x", "World X (x, y, z all 0 = not given: a List, Downtime or Reaction then goes beside the model, in a row behind it).")] float x = 0, [CliArg("y", "World Y.")] float y = 0, [CliArg("z", "World Z.")] float z = 0,
            [CliArg("parent", "Parent GameObject (name or path); empty = its hierarchy group.")] string parent = "")
            => AgentReply.Of(() => AddElementOf(type, name, x, y, z, parent));

        private static object AddElementOf(string type, string name, float x, float y, float z, string parent)
        {
            var entries = SimuLeanWindow.CollectEntries();
            Transform under = string.IsNullOrEmpty(parent) ? null : FindObject(parent).transform;
            if (!string.IsNullOrEmpty(name)) EnsureFreeName(name);          // before anything is placed
            GameObject go;
            string placedAt = null;
            if (TryFindEntry(entries, type) is SimuLeanWindow.Entry entry)
                go = SimuLeanWindow.Place(entry, new Vector3(x, y, z), under)
                     ?? throw new InvalidOperationException($"'{entry.Name}' could not be placed.");
            else
                go = PlaceTool(FindTool(type, entries), new Vector3(x, y, z), under);
            // a list / downtime / reaction without a position: beside the model, not at the origin on an element
            if (x == 0 && y == 0 && z == 0 && go.GetComponent<SElement>() == null && go.transform.position == Vector3.zero
                && go.GetComponents<MonoBehaviour>().Any(c => c != null && ModelLayout.IsMarkerTool(c.GetType())))
            {
                go.transform.position = ModelLayout.FreeSpotForTool(go);
                placedAt = "no position given: beside the model, behind it (+Z), in a row with the other lists, downtimes and " +
                           "reactions (its place does not change a run; move it with x / z)";
            }
            // the Library makes a name unique among its siblings only: an element in another group may carry it already
            string wanted = string.IsNullOrEmpty(name) ? (IsTaken(go.name, go) ? FreeName(go.name, go) : null) : name;
            if (wanted != null && wanted != go.name)
            {
                Undo.RegisterCompleteObjectUndo(go, "Rename " + wanted);
                go.name = wanted;
            }
            EditorSceneManager.MarkSceneDirty(go.scene);
            return Describe(go, placedAt);
        }

        [CliCommand("simulean_place_model", "Place a 3D model of the library (decoration: furniture, beds, walls…) in the open scene: " +
                                            "a model folder name (e.g. HospitalBed), a file under it (WaitingChairs/Seat_Table) or an asset " +
                                            "path (.fbx / .prefab). Empty asset = the list of library models. Undoable.", Tags = new[] { Tag })]
        public static object PlaceModel(
            [CliArg("asset", "Library 3D asset (folder, folder/file) or asset path; empty = list them.")] string asset = "",
            [CliArg("name", "GameObject name (empty = the model's).")] string name = "",
            [CliArg("x", "World X.")] float x = 0, [CliArg("y", "World Y.")] float y = 0, [CliArg("z", "World Z.")] float z = 0,
            [CliArg("rotY", "Rotation about the vertical axis (degrees).")] float rotY = 0,
            [CliArg("scale", "Uniform scale.")] float scale = 1,
            [CliArg("parent", "Parent GameObject (name or path); empty = a root 'Decor' group.")] string parent = "")
            => AgentReply.Of(() => PlaceModelOf(asset, name, x, y, z, rotY, scale, parent));

        private static object PlaceModelOf(string model, string name, float x, float y, float z, float rotY, float scale, string parent)
        {
            if (string.IsNullOrWhiteSpace(model))
                return new { models = AssetDatabase.FindAssets("t:Model", new[] { LibraryPresets.Models.TrimEnd('/') })
                    .Select(AssetDatabase.GUIDToAssetPath).Select(p => p.Substring(LibraryPresets.Models.Length)).OrderBy(p => p).ToList() };
            var asset = ModelAsset(model) ?? throw new ArgumentException($"No library model '{model}' (simulean_place_model without a model lists them).");
            Transform under = string.IsNullOrEmpty(parent) ? DecorGroup() : FindObject(parent).transform;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, under != null ? under.gameObject.scene : EditorSceneManager.GetActiveScene());
            if (under != null) go.transform.SetParent(under, true);
            // the model's own root rotation / scale (a Z-up import turned about X) are kept, as the station models do
            go.transform.SetPositionAndRotation(new Vector3(x, y, z), Quaternion.Euler(0f, rotY, 0f) * asset.transform.localRotation);
            go.transform.localScale = asset.transform.localScale * (scale > 0 ? scale : 1f);
            if (!string.IsNullOrEmpty(name)) go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Place " + go.name);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return new { name = go.name, path = PathOf(go.transform), asset = AssetDatabase.GetAssetPath(asset) };
        }

        // a library model by folder (its <Folder>.fbx, else its only model), folder/file, or an asset path
        internal static GameObject ModelAsset(string model)
        {
            string m = model.Trim().Replace('\\', '/');
            if (m.StartsWith("Assets/")) return AssetDatabase.LoadAssetAtPath<GameObject>(m);
            string folder = LibraryPresets.Models + m;
            foreach (var ext in new[] { "", ".fbx", ".prefab" })
            {
                var direct = AssetDatabase.LoadAssetAtPath<GameObject>(folder + ext);
                if (direct != null) return direct;
            }
            if (!AssetDatabase.IsValidFolder(folder)) return null;
            string leaf = m.Contains('/') ? m.Substring(m.LastIndexOf('/') + 1) : m;
            var own = AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/{leaf}.fbx");
            if (own != null) return own;
            var all = AssetDatabase.FindAssets("t:Model", new[] { folder });
            return all.Length == 1 ? AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(all[0])) : null;
        }

        private static Transform DecorGroup()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var existing = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Decor");
            if (existing != null) return existing.transform;
            var go = new GameObject("Decor");
            Undo.RegisterCreatedObjectUndo(go, "Create Decor");
            return go.transform;
        }

        [CliCommand("simulean_connect", "Connect two elements: 'to' is added to the Next Elements of 'from' (item flow). Two transport " +
                                        "nodes: a two-way track of their network (paths of walking items and vehicles). Undoable.", Tags = new[] { Tag })]
        public static object Connect(
            [CliArg("from", "Upstream element (name or path).", Required = true)] string from,
            [CliArg("to", "Downstream element (name or path).", Required = true)] string to)
            => AgentReply.Of(() => ConnectOf(from, to));

        private static object ConnectOf(string from, string to)
        {
            // two transport nodes: a two-way track of their network (the walking items' and vehicles' paths)
            var na = FindObject(from).GetComponent<UnityTransportNode>();
            var nb = FindObject(to).GetComponent<UnityTransportNode>();
            if (na != null && nb != null) return ConnectNodes(na, nb);
            var a = FindObject(from).GetComponent<SElement>() ?? throw new ArgumentException($"'{from}' is not a SimuLean element.");
            var b = FindObject(to).GetComponent<SElement>() ?? throw new ArgumentException($"'{to}' is not a SimuLean element.");
            if (a == b) throw new ArgumentException($"'{from}' cannot connect to itself.");
            if (!a.HasOutput) throw new ArgumentException($"'{a.name}' ({a.GetType().Name}) has no output: nothing leaves it.");
            if (!TakesConnections(b)) throw new ArgumentException($"'{b.name}' ({b.GetType().Name}) takes no input.");
            var warnings = new List<string>();
            Undo.RegisterCompleteObjectUndo(a, "Connect " + a.name);
            if (a.nextElements == null) a.nextElements = new List<SElement>();
            if (a.nextElements.Contains(b)) warnings.Add($"'{a.name}' already sends to '{b.name}': nothing changed.");
            else
            {
                a.nextElements.Add(b);
                if (b is UnityCombiner comb)   // as the Connect tool: connection order = input order (the first is the main)
                {
                    Undo.RegisterCompleteObjectUndo(comb, "Connect " + a.name);
                    comb.NoteConnected(a);
                    EditorUtility.SetDirty(comb);
                }
            }
            // as the editor's Connect tool: a connection replaces sending to a list (else Next Elements are not used)
            if (a.sendTo == SElement.SendMode.List)
            {
                warnings.Add($"'{a.name}' sent its items to list '{(a.pushToList != null ? a.pushToList.name : "")}': it now sends to its Next Elements.");
                a.sendTo = SElement.SendMode.Elements;
            }
            EditorUtility.SetDirty(a);
            EditorSceneManager.MarkSceneDirty(a.gameObject.scene);
            return new { from = a.name, next = a.nextElements.Where(e => e != null).Select(e => e.name).ToList(), warnings };
        }

        /// <summary>Item flow can reach <paramref name="b"/> through an upstream's Next Elements: an element with an input, and the
        /// Combiner (no input strategy, but its inputs are the incoming connections;
        /// simulean_connect refused what Next Elements allows).</summary>
        internal static bool TakesConnections(SElement b) => b.TakesConnections;

        /// <summary>A key with list indices (<c>actions[1].kind</c>, a reaction's actions) as a serialized path
        /// (<c>actions.Array.data[1].kind</c>); each list grows to hold the index, the new items with their type's defaults.
        /// Null when a list is not there.</summary>
        private static string ItemPath(SerializedObject so, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Matches(key, @"\[(\d+)\]");
            if (m.Count == 0) return key;
            var sb = new System.Text.StringBuilder();
            int at = 0;
            foreach (System.Text.RegularExpressions.Match x in m)
            {
                sb.Append(key, at, x.Index - at);
                var list = so.FindProperty(sb.ToString());
                if (list == null || !list.isArray) return null;
                int i = int.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (list.arraySize <= i)
                {
                    int old = list.arraySize;
                    list.arraySize = i + 1;
                    for (int j = old; j <= i; j++) ResetToDefaults(list.GetArrayElementAtIndex(j));
                }
                sb.Append(".Array.data[").Append(i).Append(']');
                at = x.Index + x.Length;
            }
            sb.Append(key, at, key.Length - at);
            return sb.ToString();
        }

        // a list item added by index starts from its type's field defaults, not from a zeroed item or a copy of the last
        // one (Unity's own growth): a reaction action added as 'actions[3].kind' must not inherit the colour, condition or
        // target of the action before it
        private static void ResetToDefaults(SerializedProperty item)
        {
            if (item.propertyType != SerializedPropertyType.Generic) return;
            var type = item.boxedValue?.GetType();
            if (type == null || type.GetConstructor(Type.EmptyTypes) == null) return;
            item.boxedValue = Activator.CreateInstance(type);
        }

        [CliCommand("simulean_set", "Set a field of an element or tool (simulean_params lists them): a name or path such as " +
                                    "'capacity', 'delayStrategy.constantValue', 'processBy.team' (object fields take a GameObject " +
                                    "name, or an asset path such as 'Assets/…/Doctor.prefab'), 'mode' (enums by name), lists comma-separated; a list item's " +
                                    "field as 'actions[0].kind' (an index past the end adds items), or items by id or pattern in place of the index: " +
                                    "'lanes[B0_S0_L0].node', 'lanes[B0_*].node' (* any text, ? one character, | alternatives; answers matched / items); an element's user property as 'properties.NAME' " +
                                    "(created when missing; a number, or else a text). An expression field is checked as it is set: a text that does not " +
                                    "parse, or reads a root / function its place does not serve, is refused; an element it names that the scene " +
                                    "does not have is a warning. Undoable.", Tags = new[] { Tag })]
        public static object Set(
            [CliArg("object", "The element or tool (GameObject name or hierarchy path).", Required = true)] string @object,
            [CliArg("key", "Field name or path.", Required = true)] string key,
            [CliArg("value", "The value as text (required); \"\" clears the field: an empty text or list, no object reference.")] string value = NoValue)
            => AgentReply.Of(() => SetOf(@object, key, GivenValue(value)));

        /// <summary>The default of <c>simulean_set</c>'s <c>value</c>: the MCP server refuses an empty required argument, so
        /// <c>value</c> is optional and this marks it missing; an empty text (or null) is a value, the one that clears.
        /// Printable text: the Unity CLI drops from the MCP tool list a command whose schema default holds a control
        /// character, which would drop simulean_set.</summary>
        internal const string NoValue = "(no value)";

        private static string GivenValue(string value)
        {
            if (value == NoValue) throw new ArgumentException("value is required (value=\"\" clears the field).");
            return value ?? "";
        }

        /// <summary>The answer of <c>simulean_set</c> on one field (serialized as it is: the names and order are the answer's).</summary>
        private sealed class SetAnswer
        {
            public string @object;
            public string component;
            public string key;
            public string value;
            public string alsoSet;
            public List<string> ruleParameters;
            public string rebuilt;
            public List<string> warnings;
        }

        /// <param name="session">The <c>simulean_apply</c> the edit belongs to (its follow-ups wait for its end); null = a
        /// single edit.</param>
        private static object SetOf(string element, string key, string value, EditSession session = null)
        {
            var go = FindObject(element);
            if (key.StartsWith(PropertyRecords.Key + ".", StringComparison.Ordinal)) return SetProperty(go, key.Substring(PropertyRecords.Key.Length + 1), value);
            // list items chosen by their id: lanes[B0_*].node, lanes[B0_S0_L0].node, stops[*].node
            if (Selector.IsMatch(key)) return SetSelected(go, key, value, session);
            return SetField(go, key, value, session);
        }

        private static SetAnswer SetField(GameObject go, string key, string value, EditSession session)
        {
            foreach (var c in go.GetComponents<MonoBehaviour>().OrderBy(c => IsSimuLean(c) ? 0 : 1))
            {
                if (c == null) continue;
                var so = new SerializedObject(c);
                string path = ItemPath(so, key);
                var p = path != null ? so.FindProperty(path) : null;
                if (p == null) continue;
                Assign(p, value);
                // a value whose switch is off is used from now on (transportBy.loadTime = 0 set without
                // overrideLoadTimes was not exported, so the rack's zero-handling warning could not be acknowledged)
                string switchedOn = SwitchOn(so, path);
                // an expression field is checked as it is entered: a syntax error refuses the edit, an unknown element warns
                var exprWarnings = new List<string>();
                var expr = Editor.ExpressionField.AttributeOf(c.GetType(), path);
                if (expr != null)
                    foreach (string text in Editor.ExpressionField.Texts(p))
                    {
                        var check = Editor.ExpressionField.Check(text, expr.Context, expr.Records, freshNames: true);
                        if (check.Severity == Editor.ExpressionField.Severity.Error)
                            throw new ArgumentException($"'{go.name}'.{key}: '{text}' is not a valid expression: {check.Message}");
                        if (check.Severity == Editor.ExpressionField.Severity.Warning) exprWarnings.Add($"'{go.name}'.{key}: {check.Message}");
                    }
                // a rule set: its parameters filled in and listed
                var ruleWarnings = new List<string>();
                var ruleParameters = FillRuleParameters(so, path, ruleWarnings);
                // the whole object first: a property-diff undo reverted in a simulean_apply that fails restored the object
                // references set by the previous command as none; the complete record restores them
                Undo.RegisterCompleteObjectUndo(c, "Set " + key);
                so.ApplyModifiedProperties();      // registers the undo
                EditorSceneManager.MarkSceneDirty(go.scene);
                // what the edit sets off (SimuLeanPostSetHooks): rebuilds first, the answer's own checks, then the hooks' checks
                var edit = new FieldEdit { Owner = go, Component = c, Key = key, Path = path, Expression = expr, Session = session };
                RunPostSetHooks(edit, rebuild: true);
                var warnings = new List<string> { InactiveDistributionWarning(new SerializedObject(c), key) };
                warnings.AddRange(exprWarnings);
                warnings.AddRange(ruleWarnings);
                RunPostSetHooks(edit, rebuild: false);
                warnings.AddRange(edit.Warnings);
                return new SetAnswer
                {
                    @object = go.name, component = c.GetType().Name, key = key, value =ValueOf(new SerializedObject(c).FindProperty(path)),
                    alsoSet = switchedOn != null ? switchedOn + " = true (the value is used)" : null,
                    ruleParameters = ruleParameters, rebuilt = edit.Rebuilt, warnings = warnings.Where(w => w != null).ToList(),
                };
            }
            var known = Fields(SimuLeanComponent(go)).Select(f => f.path).ToList();
            throw new ArgumentException($"'{go.name}' has no field '{key}'." + (FieldHint(key, known) ?? NameSuggestions.DidYouMean(key, known)) +
                                        $" Fields: {string.Join(", ", known)}.");
        }

        // Names other simulation tools give a field, which the did-you-mean (by spelling) cannot connect to SimuLean's.
        private static string FieldHint(string key, List<string> known)
        {
            string k = Normalised(key);
            if (known.Contains("capacity") && new[] { "servers", "numberofservers", "processquantity", "parallel", "slots", "machines", "size" }.Contains(k))
                return " The number of parallel slots (servers) is 'capacity'.";
            if (key.Trim().StartsWith("rule.", StringComparison.Ordinal))
                return " A rule's parameter is set in its site's list: outputStrategy.ruleParameters = 'FreeWeight = 0.5' (a routing rule; a Queue's " +
                       "release rule: ruleParameters; a team's: the team's ruleParameters) - rule.<Field> is a study factor's form.";
            if (new[] { "x", "y", "z", "position", "pos", "location.x", "transform" }.Contains(key.Trim().ToLowerInvariant()))
                return " An element's place on the floor is not a field: move it with simulean_move object=… x=… z=… (or simulean_apply's " +
                       "'move'). The place changes a run only through travel: a Straight team walks between the elements' places (a Network " +
                       "team between its nodes).";
            return null;
        }

        // sendTo / receiveFrom name a mode; given the list itself, the mode is List and the list goes in its own field
        private static string ListModeHint(string path, string value)
        {
            string field = path == "sendTo" ? "pushToList" : path == "receiveFrom" ? "pullFromList" : null;
            if (field == null || string.IsNullOrWhiteSpace(value) || !UnityCompat.FindAll<UnitySimList>().Any(l => l != null && l.name == value.Trim())) return null;
            return $" '{value.Trim()}' is the list: set {path} = List and {field} = {value.Trim()}.";
        }

        // a list index that is not a number: an item id or a pattern (lanes[B0_*])
        private static readonly System.Text.RegularExpressions.Regex Selector = new System.Text.RegularExpressions.Regex(@"\[(?!\d+\])([^\]]+)\]");

        /// <summary>The fields that name a list item, in the order looked for (a rack lane's <c>laneId</c>, a milk run stop's
        /// <c>name</c>…); a list of texts is matched by the texts.</summary>
        private static readonly string[] ItemIdFields = { "laneId", "id", "name", "sku" };

        private static string ItemIdOf(SerializedProperty item)
        {
            if (item.propertyType == SerializedPropertyType.String) return item.stringValue;
            foreach (string f in ItemIdFields)
            {
                var p = item.FindPropertyRelative(f);
                if (p != null && p.propertyType == SerializedPropertyType.String) return p.stringValue;
            }
            return null;
        }

        /// <summary>
        /// The keys of a key whose list index is an item id or a pattern (a rack's 36 lanes were set one index at a
        /// time): <c>lanes[B0_*].node</c> = every lane whose id starts with <c>B0_</c>, <c>lanes[B0_S0_L0].node</c> = that lane,
        /// <c>lanes[*]</c> = every item; <c>*</c> any text, <c>?</c> one character, <c>|</c> between alternatives
        /// (<c>lanes[B0_*|B1_*]</c>). An item's id is its <see cref="ItemIdFields"/> text. Returns (concrete key, item id) pairs.
        /// </summary>
        private static List<(string key, string id)> SelectedKeys(SerializedObject so, string key)
        {
            var m = Selector.Match(key);
            if (!m.Success) return new List<(string, string)> { (key, null) };
            string prefix = key.Substring(0, m.Index), rest = key.Substring(m.Index + m.Length), pattern = m.Groups[1].Value.Trim();
            string listPath = prefix.Contains("[") ? ItemPath(so, prefix) : prefix;
            var list = listPath != null ? so.FindProperty(listPath) : null;
            if (list == null || !list.isArray || list.propertyType == SerializedPropertyType.String) return null;
            var alternatives = pattern.Split('|').Select(a => new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(a.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$")).ToList();
            var keys = new List<(string, string)>();
            var ids = new List<string>();
            for (int i = 0; i < list.arraySize; i++)
            {
                string id = ItemIdOf(list.GetArrayElementAtIndex(i));
                if (id == null) throw new ArgumentException($"'{prefix}': its items have no id ({string.Join(", ", ItemIdFields)}): choose them by index, {prefix}[0].");
                ids.Add(id);
                if (!alternatives.Any(r => r.IsMatch(id))) continue;
                foreach (var (k, inner) in SelectedKeys(so, $"{prefix}[{i}]{rest}") ?? new List<(string, string)>())
                    keys.Add((k, inner ?? id));
            }
            if (keys.Count == 0)
                throw new ArgumentException($"No item of '{prefix}' matches '{pattern}' (its {ids.Count} ids: {IdRange(ids)}; * = any text, ? = one character, | between alternatives).");
            return keys;
        }

        /// <summary>A list of ids in short: all of them up to 6, else the first three, … the last, (count).</summary>
        internal static string IdRange(IList<string> ids)
            => ids.Count == 0 ? "none" : ids.Count <= 6 ? string.Join(", ", ids) : $"{ids[0]}, {ids[1]}, {ids[2]} … {ids[ids.Count - 1]} ({ids.Count})";

        // simulean_set on the items a selector chooses: each set as by its index, one answer
        private static object SetSelected(GameObject go, string key, string value, EditSession session)
        {
            List<(string key, string id)> keys = null;
            foreach (var c in go.GetComponents<MonoBehaviour>().OrderBy(c => IsSimuLean(c) ? 0 : 1))
            {
                if (c == null) continue;
                keys = SelectedKeys(new SerializedObject(c), key);
                if (keys != null) break;
            }
            if (keys == null)
            {
                string list = key.Substring(0, Selector.Match(key).Index);
                var known = Fields(SimuLeanComponent(go)).Select(f => f.path).ToList();
                throw new ArgumentException($"'{go.name}' has no list '{list}'." + NameSuggestions.DidYouMean(list, known));
            }
            var warnings = new List<string>();
            var alsoSet = new List<string>();
            var rebuilt = new List<string>();
            string last = null;
            foreach (var (k, _) in keys)
            {
                var one = SetField(go, k, value, session);
                last = one.value;
                foreach (string x in one.warnings) if (!warnings.Contains(x)) warnings.Add(x);
                if (one.alsoSet != null && !alsoSet.Contains(one.alsoSet)) alsoSet.Add(one.alsoSet);
                if (one.rebuilt != null) rebuilt.Add(one.rebuilt);
            }
            var ids = keys.Select(x => x.id).Distinct().ToList();
            return new { @object = go.name, key, value = last, matched = ids.Count, items = IdRange(ids),
                         alsoSet = alsoSet.Count > 0 ? string.Join("; ", alsoSet) : null,
                         rebuilt = rebuilt.Count > 0 ? rebuilt[rebuilt.Count - 1] : null, warnings };
        }

        /// <summary>
        /// The switch of a value set: setting <c>X</c> (or one of its keys) turns on the bool beside it that says
        /// whether <c>X</c> is used — <c>useX</c> (<c>usePieceTime</c>, <c>useChangeTime</c>, <c>useLeadTime</c>…),
        /// <c>overrideLoadTimes</c> for a use's <c>loadTime</c> / <c>unloadTime</c> (<c>overrideTimes</c> for a milk-run flow's), <c>overrideEmptiesTimes</c> for a milk run's
        /// <c>emptiesLoadTime</c> / <c>emptiesUnloadTime</c>. Returns the switch's path when it was off (null otherwise).
        /// </summary>
        internal static string SwitchOn(SerializedObject so, string path)
        {
            var parts = path.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                string parent = i == 0 ? "" : string.Join(".", parts, 0, i) + ".";
                string name = parts[i];
                if (name.Length == 0 || name.Contains("[")) continue;
                var candidates = new List<string> { parent + "use" + char.ToUpperInvariant(name[0]) + name.Substring(1) };
                // a use's transportBy has overrideLoadTimes, a milk-run flow its own overrideTimes
                if (name == "loadTime" || name == "unloadTime") { candidates.Add(parent + "overrideLoadTimes"); candidates.Add(parent + "overrideTimes"); }
                if (name == "emptiesLoadTime" || name == "emptiesUnloadTime") candidates.Add(parent + "overrideEmptiesTimes");
                foreach (string s in candidates)
                {
                    var sw = so.FindProperty(s);
                    if (sw == null || sw.propertyType != SerializedPropertyType.Boolean || sw.boolValue) continue;
                    sw.boolValue = true;
                    return s;
                }
            }
            return null;
        }

        // a field of the rack's own (bays, slotsPerBay, levels, depth, pitches, style…), not the lanes it generates nor the
        // storage's shared settings: the layout the lanes are built from
        private static bool IsRackLayoutField(string path)
        {
            string top = path.Split('.', '[')[0];
            return typeof(UnityStorageRack).GetField(top, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                                                         | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly) != null;
        }

        /// <summary>Rebuilds a rack's lanes and visuals as its inspector button does (one undo step with the edit).</summary>
        internal static string RebuildRack(UnityStorageRack rack)
        {
            Undo.RegisterFullObjectHierarchyUndo(rack.gameObject, "Rebuild Storage Locations");
            var filter = Debug.unityLogger.filterLogType;
            // its "Rebuilt storage layout" line: the answer says it; its warnings (e.g. a lane deeper than its style) still
            // reach the answer, naming the rack (the logger was switched off whole)
            Debug.unityLogger.filterLogType = LogType.Warning;
            try { rack.Rebuild(); }
            finally { Debug.unityLogger.filterLogType = filter; }
            EditorUtility.SetDirty(rack);
            EditorSceneManager.MarkSceneDirty(rack.gameObject.scene);
            // the extent it was built to (so pick faces are not placed inside the rack)
            var size = ModelLayout.SizeOf(rack.gameObject);
            // the lane ids, which are 0-based (not 1-based bays), and how to set one or a group
            var ids = rack.lanes.Where(l => l != null).Select(l => l.laneId).ToList();
            string lanes = ids.Count == 0 ? "" : $" Lanes {IdRange(ids)}, 0-based ({(rack.style == StorageRackStyle.FloorStorage ? "B<bay>_R<row>" : "B<bay>_S<slot>_L<level>")}): " +
                                                 $"set one with lanes[{ids[0]}].node, a group with lanes[{ids[0].Split('_')[0]}_*].node.";
            return rack.GetLayoutSummary() + (size != null ? $" Built extent {size[0].ToString("0.#", CultureInfo.InvariantCulture)} × {size[1].ToString("0.#", CultureInfo.InvariantCulture)} m (x × z)." : "") + lanes;
        }

        // 'properties.NAME' (the key a study varies): the element's user property (the SElementProperties component, added when missing), a
        // number or else a text; created when the element has none of that name (otherwise an agent has to add the component
        // and write its list through the generic tools, and a key goes in with its quotes)
        private static object SetProperty(GameObject go, string name, string value)
        {
            name = (name ?? "").Trim().Trim('"', '\'').Trim();
            if (name.Length == 0 || name.Contains("|")) throw new ArgumentException($"'properties.{name}': give a property name without '|' (e.g. properties.beds).");
            if (go.GetComponent<SElement>() == null) throw new ArgumentException($"'{go.name}' is not a model element: only elements have properties.");
            var props = go.GetComponent<SElementProperties>();
            bool added = props == null;
            if (added) props = Undo.AddComponent<SElementProperties>(go);
            var so = new SerializedObject(props);
            var list = so.FindProperty("properties");
            int index = -1;
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).FindPropertyRelative("key").stringValue == name) index = i;
            bool created = index < 0;
            if (created) { index = list.arraySize; list.arraySize++; }
            var pair = list.GetArrayElementAtIndex(index);
            pair.FindPropertyRelative("key").stringValue = name;
            string v = (value ?? "").Trim();
            bool number = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d);
            pair.FindPropertyRelative("value").doubleValue = number ? d : 0;
            pair.FindPropertyRelative("text").stringValue = number ? "" : v;
            pair.FindPropertyRelative("reference").objectReferenceValue = null;
            if (!added) Undo.RegisterCompleteObjectUndo(props, "Set property " + name);   // one undo record per property, as SetOf
            so.ApplyModifiedProperties();      // registers the undo
            props.Rebuild();  // the lookup the component keeps (after a list change, as its inspector does)
            EditorSceneManager.MarkSceneDirty(go.scene);
            var warnings = new List<string>();
            if (!number) warnings.Add($"'{v}' is not a number: the property is a text (a study can vary only number properties).");
            return new { @object = go.name, component = nameof(SElementProperties), key = PropertyRecords.Key + "." + name,
                         value = number ? (object)d : v, created, componentAdded = added, warnings };
        }

        // Next Elements written by simulean_set get the checks of simulean_connect, as warnings
        private static IEnumerable<string> FlowWarnings(SElement a, string key)
        {
            if (!key.StartsWith("nextElements", StringComparison.Ordinal) || a.nextElements == null) yield break;
            if (!a.HasOutput) yield return $"'{a.name}' has no output: its Next Elements are not used.";
            foreach (var b in a.nextElements.Where(b => b != null && !TakesConnections(b)))
                yield return $"'{b.name}' takes no input: the connection from '{a.name}' is not used.";
            if (a.sendTo == SElement.SendMode.List)
                yield return $"'{a.name}' sends its items to a list: its Next Elements are not used (set sendTo to Elements).";
        }

        [CliCommand("simulean_add_team", "Place a resource team (operators, cranes, vehicles…) with n members. Pass its kind " +
                                         "(operator, agv, forklift, tugger, pallet truck, stacker crane, hall crane, robot…): the " +
                                         "members get that kind's 3D model and realistic speed (m/s), as the Library's team presets. " +
                                         "A kind is a preset over the team component's defaults: it sets only the members' 3D model, " +
                                         "speed = the kind's m/s and units = Physical (a robot also gets its arm cell); every other field " +
                                         "keeps the component default. " +
                                         "Without a kind, a team named after one (AGVs, Forklifts…) is of that kind; otherwise its members get the look of their travel (Network: an AGV, Axes: a crane, else an " +
                                         "operator) and the component default: speed 1 with units = Model (1 distance unit per model time " +
                                         "unit, not m/s). So kind=operator gives 1.2 m/s, no kind 1 distance unit per time unit. Assign it with simulean_set <station> processBy.team " +
                                         "<team> (or transportBy.team). The answer gives speed, units and speedFrom (the kind, the name or " +
                                         "the default). Undoable.", Tags = new[] { Tag })]
        public static object AddTeam(
            [CliArg("name", "Team name.", Required = true)] string name,
            [CliArg("members", "Number of members.")] int members = 1,
            [CliArg("travel", "Travel mode (TeamTravel: Straight — the default: straight lines between the elements —, Abstract — no travel —, Network, Grid, Matrix, Axes…); empty = the default.")] string travel = "",
            [CliArg("x", "World X (x, y, z all 0 = not given: the team goes in front of the model, not on an element).")] float x = 0, [CliArg("y", "World Y.")] float y = 0, [CliArg("z", "World Z.")] float z = 0,
            [CliArg("parent", "Parent GameObject (name or path); empty = its hierarchy group.")] string parent = "",
            [CliArg("kind", "Member kind: its 3D model and speed in m/s — operator, doctor, nurse, fitter, welder, painter, outfitter (1.2); bed / stretcher / wheelchair porter, pallet truck, agv, hall crane, level luffing crane (1.0); forklift (2.5); tugger, stacker crane (2.0); robot, vacuum robot, welding robot (1.5); goliath crane (0.6); spmt (0.8); welding gantry (0.5). Plurals and aliases accepted; setters, mechanics and other walking staff: operator. A kind sets the model, that speed and units = Physical (m/s), nothing else. Empty = the look of its travel and the component default: speed 1, units = Model (1 distance unit per model time unit).")] string kind = "")
            => AgentReply.Of(() => AddTeamOf(name, members, travel, x, y, z, parent, kind));

        private static object AddTeamOf(string name, int members, string travel, float x, float y, float z, string parent, string kind)
        {
            // checked before the scene changes; no kind: a team named after one ('AGVs', 'Forklifts') is of that kind
            var memberKind = LibraryPresets.KindOf(kind) ?? LibraryPresets.TryKindOf(name);
            bool kindByName = memberKind != null && LibraryPresets.KindOf(kind) == null;
            var entry = SimuLeanWindow.CollectEntries().FirstOrDefault(e => e.Type == typeof(UnityResourceTeam))
                        ?? throw new InvalidOperationException("No resource team in the Library.");
            // every argument checked before the scene changes (a bad travel left a placed team behind)
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A team needs a name.");
            EnsureFreeName(name);
            if (members < 1) throw new ArgumentException($"members: {members} — a team has at least 1 member.");
            TeamTravel? mode = null;
            if (!string.IsNullOrWhiteSpace(travel))
            {
                var names = Enum.GetNames(typeof(TeamTravel));
                string match = names.FirstOrDefault(n => string.Equals(n, travel.Trim(), StringComparison.OrdinalIgnoreCase))
                               ?? throw new ArgumentException($"travel: '{travel}' is not one of {string.Join(", ", names)}." + NameSuggestions.DidYouMean(travel, names));
                mode = (TeamTravel)Enum.Parse(typeof(TeamTravel), match);
            }
            Transform under = string.IsNullOrEmpty(parent) ? null : FindObject(parent).transform;
            // no position given: in front of the model, not at the origin on top of an element
            bool placed = x == 0 && y == 0 && z == 0;
            if (placed) { var spot = ModelLayout.FreeSpotForTeam(); x = spot.x; z = spot.z; }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            try
            {
                var go = SimuLeanWindow.Place(entry, new Vector3(x, y, z), under)
                         ?? throw new InvalidOperationException("The resource team could not be placed.");
                var team = go.GetComponent<UnityResourceTeam>();
                Undo.RegisterCompleteObjectUndo(go, "Add team " + name);
                Undo.RegisterCompleteObjectUndo(team, "Add team " + name);
                go.name = name;
                // the members are named after the team's final name (the first was 'Resource Team_1')
                team.members.Clear();
                team.AddMembers(members);
                if (mode.HasValue) team.travel = mode.Value;
                // the members' look: the kind asked (with its speed, as the team presets), else the travel's default — the
                // Library placed the default of Straight, before the travel was set (teams placed this way were drawn as boxes)
                if (memberKind != null) LibraryPresets.ApplyKind(team, memberKind);
                else LibraryPresets.ApplyLook(team, LibraryPresets.DefaultKind(team.travel));
                EditorUtility.SetDirty(team);
                EditorSceneManager.MarkSceneDirty(go.scene);
                var overlaps = ModelLayout.Overlaps(ModelLayout.Elements(), new[] { go.name });
                // where the speed came from, so the caller can tell which speed a team got
                string unitName = UnitySimClock.Instance != null ? SimuLean.Results.ResultMetrics.TimeUnitName(UnitySimClock.Instance.TimeUnitSeconds) : "model time unit";
                string speedFrom = memberKind == null
                    ? $"no kind: the team's default, {team.speed.ToString("0.###", CultureInfo.InvariantCulture)} distance unit per model time unit " +
                      $"(units Model: m per {unitName}); give a kind, or set speed and units = Physical for m/s"
                    : (kindByName ? $"the team's name: kind '{memberKind.Name}'" : $"kind '{memberKind.Name}'") +
                      $" — {memberKind.Speed.ToString("0.###", CultureInfo.InvariantCulture)} m/s (units Physical, converted by the clock's time unit)";
                return new { team = go.name, speedFrom, path = PathOf(go.transform), members = team.members.Select(m => m.name).ToList(), travel = team.travel.ToString(),
                             kind = (memberKind ?? LibraryPresets.DefaultKind(team.travel)).Name,
                             model = team.memberPrefab != null ? team.memberPrefab.name : null, speed = team.speed, units = team.units.ToString(),
                             at = new[] { (float)Math.Round(x, 1), (float)Math.Round(z, 1) },
                             placedAt = placed ? "no position given: in front of the model (move it with x / z, or give the team a homeNode)" : null,
                             overlaps };
            }
            catch
            {
                TeamMemberPreviews.Revert(group);
                throw;
            }
            finally { Undo.CollapseUndoOperations(group); }
        }

        [CliCommand("simulean_load_table", "Give a Schedule Source its arrivals (columns Time, Name, Q, labels), a RateTable Source " +
                                           "its periods (columns Start, Rate, Name, labels: a table with Start and Rate and no Time " +
                                           "puts the source in RateTable mode), or a station with a setup its setup matrix (columns " +
                                           "from, to, time), from a table file (CSV, .data or Excel): a Data Table named after the " +
                                           "source / '<station> setup matrix' reads the file (created or reused) and is loaded; with " +
                                           "readAtRun the runs read the file as it is when they start.", Tags = new[] { Tag })]
        public static object LoadTable(
            [CliArg("object", "The Source, or a station with a setup (name or path).", Required = true)] string @object,
            [CliArg("file", "Table file: absolute, or relative to the project or to Assets/ — keep it in the model's folder (Assets/SimuLeanModels/<Model>/file.csv; a bare name is not looked up there). Read into the scene, not copied.", Required = true)] string file,
            [CliArg("sheet", "Excel sheet (empty = the first).")] string sheet = "",
            [CliArg("readAtRun", "Read the file at each run start (else the loaded copy).")] bool readAtRun = false)
            => AgentReply.Of(() => LoadTableOf(@object, file, sheet, readAtRun));

        private static object LoadTableOf(string source, string file, string sheet, bool readAtRun)
        {
            var go = FindObject(source);
            var src = go.GetComponent<UnitySource>();
            var station = src == null ? go.GetComponent<ISetupUser>() as SElement : null;
            if (src == null && station == null)
                throw new ArgumentException($"'{source}' is neither a Source nor a station with a setup (Multi-Server, Combiner, " +
                                            "Picking Station, Assembly Station, Stacker, Erection Dock).");
            // one undo group, reverted when the table cannot be loaded: nothing is left half done
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            try { return src != null ? LoadTableInto(src, file, sheet, readAtRun) : LoadMatrixInto(station, file, sheet, readAtRun); }
            catch
            {
                TeamMemberPreviews.Revert(group);
                throw;
            }
            finally { Undo.CollapseUndoOperations(group); }
        }

        private static object LoadTableInto(UnitySource src, string file, string sheet, bool readAtRun)
        {
            Undo.RegisterCompleteObjectUndo(src, "Load table " + src.name);
            var table = src.dataTable;
            if (table == null)
            {
                var go = new GameObject(src.name + " table");
                Undo.RegisterCreatedObjectUndo(go, "Load table " + src.name);
                table = Undo.AddComponent<UnityDataTable>(go);
                table.tableName = src.name;
                if (ModelHierarchy.AutoOrganize) ModelHierarchy.Place(go);
                src.dataTable = table;
            }
            Undo.RegisterCompleteObjectUndo(table, "Load table " + src.name);
            table.source = SimuLean.Serialization.DataSourceKind.File;
            table.fileName = file;
            table.sheetName = sheet ?? "";
            table.readFileAtRun = readAtRun;
            if (table.ResolveFile() == null) throw new ArgumentException($"Table file '{file}' not found.");
            table.Load(ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot));
            var rows = table.Rows;
            if (rows.Count == 0) throw new InvalidOperationException($"'{file}' gave no table.");
            // a rate table: the columns Start and Rate without Time, or a source already in RateTable mode
            bool rates = src.mode == SourceMode.RateTable
                         || (rows.ContainsKey(SimuLean.Serialization.RateTables.StartColumn) && rows.ContainsKey(SimuLean.Serialization.RateTables.RateColumn) && !rows.ContainsKey("Time"));
            var rateNotes = new List<string>();
            if (rates && src.ratePeriods != null && src.ratePeriods.Count > 0)
            {
                rateNotes.Add($"The source's {src.ratePeriods.Count} inline rate period(s) were cleared: the table gives the periods now.");
                src.ratePeriods.Clear();
            }
            src.mode = rates ? SourceMode.RateTable : SourceMode.Schedule;
            EditorUtility.SetDirty(table);
            EditorUtility.SetDirty(src);
            EditorSceneManager.MarkSceneDirty(src.gameObject.scene);
            if (rates) return RateTableRead(src, table, rows, rateNotes);
            // how the rows are read: Time, Name and Q by those exact column names, every other column a label
            int count = rows.Values.First().Count;
            var labels = rows.Keys.Where(k => k != "Time" && k != "Name" && k != "Q").ToList();
            var notes = new List<string>();
            foreach (string col in new[] { "Time", "Name", "Q" })
            {
                string near = rows.Keys.FirstOrDefault(k => k != col && string.Equals(k, col, StringComparison.OrdinalIgnoreCase));
                if (near != null) notes.Add($"Column '{near}' is read as a label, not as {col}: the column names Time, Name and Q are case-sensitive.");
            }
            if (!rows.ContainsKey("Time")) notes.Add("No column Time: every row arrives at once, at the start of the run.");
            string times = rows.TryGetValue("Time", out var t) && t.Count > 0 ? $"Time {t[0]} … {t[t.Count - 1]}" : "no Time column";
            // the arrivals as the source counts them: Q items per row (an integer; missing or not one = 1)
            int arrivals = 0;
            rows.TryGetValue("Q", out var qs);
            for (int i = 0; i < count; i++)
                arrivals += qs != null && i < qs.Count && int.TryParse(qs[i], System.Globalization.NumberStyles.Integer,
                                                                       System.Globalization.CultureInfo.InvariantCulture, out int q) ? Math.Max(0, q) : 1;
            if (qs != null && qs.Exists(v => !int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)))
                notes.Add("Some Q cells are not whole numbers: those rows give 1 item.");
            string read = $"{count} rows, {arrivals} arrivals" +
                          (rows.ContainsKey("Q") ? " (Q items per row)" : " (one per row: no Q)") + $": {times}; " +
                          (rows.ContainsKey("Name") ? "Name = the item's name; " : "") +
                          (labels.Count > 0 ? "labels " + string.Join(", ", labels) : "no label columns");
            return new { source = src.name, table = table.TableName, rows = count, arrivals, columns = rows.Keys.ToList(), read, notes };
        }

        // a rate table's reading: one line per period, and what happens after the last one
        private static object RateTableRead(UnitySource src, UnityDataTable table, Dictionary<string, List<string>> rows, List<string> notes)
        {
            List<SimuLean.RatePeriod> periods;
            try { periods = SimuLean.Serialization.RateTables.PeriodsOf(rows, src.name); }
            catch (FormatException e) { throw new ArgumentException(e.Message); }
            var lines = new List<string>();
            for (int i = 0; i < periods.Count && i < 12; i++)
            {
                var p = periods[i];
                string end = i + 1 < periods.Count ? periods[i + 1].Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "end";
                lines.Add($"{p.Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} → {end}: rate " +
                          p.Rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " per time unit" +
                          (p.Rate == 0 ? " (none)" : "") + (p.ItemName != null ? $", items '{p.ItemName}'" : ""));
            }
            if (periods.Count > 12) lines.Add($"… {periods.Count - 12} more");
            var labels = rows.Keys.Where(k => k != "Start" && k != "Rate" && k != "Name").ToList();
            var last = periods[periods.Count - 1];
            notes.Add(last.Rate == 0
                ? $"After {last.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)} the rate is 0: the source stops (a run that ends when empty ends at the last exit)."
                : $"The last rate ({last.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}) holds to the end of the run: end with a period of rate 0 to close.");
            if (periods[0].Start > 0) notes.Add($"No arrivals before {periods[0].Start.ToString(System.Globalization.CultureInfo.InvariantCulture)} (the first period's start).");
            return new { source = src.name, mode = "RateTable", table = table.TableName, rows = periods.Count, columns = rows.Keys.ToList(),
                         read = lines, labels, notes };
        }

        // a station's setup matrix: a Data Table '<station> setup matrix' with the columns from, to, time
        private static object LoadMatrixInto(SElement station, string file, string sheet, bool readAtRun)
        {
            var setup = ((ISetupUser)station).Setup ?? throw new InvalidOperationException($"'{station.name}' has no setup.");
            Undo.RegisterCompleteObjectUndo(station, "Load setup matrix " + station.name);
            var table = setup.matrix;
            if (table == null)
            {
                var go = new GameObject(station.name + " setup matrix");
                Undo.RegisterCreatedObjectUndo(go, "Load setup matrix " + station.name);
                table = Undo.AddComponent<UnityDataTable>(go);
                table.tableName = station.name + " setup matrix";
                if (ModelHierarchy.AutoOrganize) ModelHierarchy.Place(go);
                setup.matrix = table;
            }
            Undo.RegisterCompleteObjectUndo(table, "Load setup matrix " + station.name);
            table.source = SimuLean.Serialization.DataSourceKind.File;
            table.fileName = file;
            table.sheetName = sheet ?? "";
            table.readFileAtRun = readAtRun;
            if (table.ResolveFile() == null) throw new ArgumentException($"Table file '{file}' not found.");
            table.Load(ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot));
            var rows = table.Rows;
            if (rows.Count == 0) throw new InvalidOperationException($"'{file}' gave no table.");
            foreach (string col in new[] { "from", "to", "time" })
                if (!rows.ContainsKey(col))
                    throw new ArgumentException($"'{file}': a setup matrix needs the columns from, to, time (it has {string.Join(", ", rows.Keys)}).");
            EditorUtility.SetDirty(table);
            EditorUtility.SetDirty(station);
            EditorSceneManager.MarkSceneDirty(station.gameObject.scene);
            var warnings = new List<string>();
            if (setup.when == SetupWhen.None) warnings.Add($"'{station.name}' sets up never (setup.when = None): set setup.when to use the matrix.");
            // how the rows were read: one line each, the first setup and the wildcard named
            var read = SimuLean.SetupMatrix.Describe(rows["from"], rows["to"], rows["time"]);
            return new { station = station.name, table = table.TableName, rows = rows.Values.First().Count, columns = rows.Keys.ToList(),
                         label = setup.label, read, lookup = SimuLean.SetupMatrix.LookupText, notes = warnings };
        }

        // ── reading and checking the whole model ────────────────

        [CliCommand("simulean_model", "The scene's model in one compact answer, as a run builds it: the clock (time unit, horizon, " +
                                      "warm-up, end condition, seed), every element (type, next elements in order, parameters under " +
                                      "the keys a study varies), the other connections, teams (members, labels, parameters), lists, " +
                                      "downtimes, reactions, data tables and the transport network (nodes with their positions and the " +
                                      "elements there, tracks with their lengths, the teams on it) — all by name; each team and downtime with its metrics " +
                                      "(the study metric path, the statistic names, a downtime's per-target paths downtime:Id@<target> " +
                                      "with a team target's members by name); info = the model card (what the " +
                                      "model represents, its KPIs, parameters worth varying, assumptions) when the scene has one. " +
                                      "section=logic answers instead every decision as one plain sentence (routing, admission, " +
                                      "release order, a team's request order and member choice, list queries and orders, waits, " +
                                      "reactions; a setting its strategy does not use is left out, a default is marked) with its raw " +
                                      "settings, what it reads and writes, and which property writes wake an admission; in Play also " +
                                      "its run-time counters (live: items per output and the last choice with the item's labels, " +
                                      "entries / refused checks of an admission, wake-ups, releases, requests served, a reaction's " +
                                      "fires, a rule's calls). Read-only.", Tags = new[] { Tag })]
        public static object Model(
            [CliArg("object", "Only this element / team / list / downtime / reaction / table, or the transport network (network, its name or one of its nodes) (empty = the whole model).")] string @object = "",
            [CliArg("parameters", "Include the parameters (false = the structure only: smaller).")] bool parameters = true,
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "",
            [CliArg("section", "What to answer: model (the default: structure and parameters) or logic (the decisions: one sentence each with its settings, what it reads / writes, the admission wake-ups and notes).")] string section = "model",
            [CliArg("detail", "compact (default: empty texts and the scene's automatic points left out, a long or generated list such as a rack's lanes summarised per field) or full (every value).")] string detail = "compact")
            => AgentReply.Of(() =>
            {
                string only = string.IsNullOrWhiteSpace(@object) ? null : @object.Trim();
                string part = string.IsNullOrWhiteSpace(section) ? "model" : section.Trim().ToLowerInvariant();
                if (part != "model" && part != "logic")
                    throw new System.ArgumentException($"section: '{section}' is not a section (model or logic).");
                string level = string.IsNullOrWhiteSpace(detail) ? "compact" : detail.Trim().ToLowerInvariant();
                if (level != "compact" && level != "full")
                    throw new System.ArgumentException($"detail: '{detail}' is not compact or full.");
                var rootObject = string.IsNullOrEmpty(root) ? null : FindObject(root);
                // the network by its GameObject's name: the description names it 'network' (a model has one)
                if (only != null && UnityCompat.FindAll<UnityTransportNetwork>().Any(n => n != null && n.name == only))
                    only = SimuLean.Optimization.ModelDescription.NetworkKey;
                var model = SceneStudy.Of(rootObject).Model;
                if (part == "logic")
                {
                    var logic = SimuLean.Logic.ModelLogic.Of(model);
                    // in Play: the running model's run-time counters beside its decisions (read-only)
                    var running = Application.isPlaying ? UnityCompat.FindAny<UnitySimClock>()?.Model : null;
                    if (running != null) SimuLean.Logic.ModelLogic.Live(logic, running);
                    return logic.ToData(only);
                }
                var description = SimuLean.Optimization.ModelDescription.Of(model, parameters, only, full: level == "full");
                ModelLayout.AddFootprints(description, rootObject);   // where each element stands and its size on the floor
                var info = string.IsNullOrWhiteSpace(@object) ? CardInfo() : null;   // the model card (simulean_card)
                if (info == null) return description;
                var withInfo = new Dictionary<string, object> { ["info"] = info };
                foreach (var kv in description) withInfo[kv.Key] = kv.Value;
                return withInfo;
            });

        [CliCommand("simulean_validate", "Check the scene's model without running it: extract it, build it (each scenario of the " +
                                         "optional study: factors / metrics as simulean_run_headless) and resolve the metrics. Answers ok " +
                                         "plus the errors and warnings a run would meet (extractor, build, study), with \"did you mean\"; a choice rule " +
                                         "without a Why, or used where the primitive it equals would do, is a warning.",
                     Tags = new[] { Tag })]
        public static object Validate(
            [CliArg("factors", "Catalog overrides 'path=v1|v2;…' or JSON to check too (as simulean_run_headless).")] string factors = "",
            [CliArg("metrics", "Named metrics 'name=path;…' or JSON to check too.")] string metrics = "",
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "",
            [CliArg("scenarios", "Explicit scenarios instead of factors (JSON): [{\"name\": \"A\", \"Buf.capacity\": 2, \"replications\": 5}, ...] - every scenario gives every factor.")] string scenarios = "",
            [CliArg("where", "Keep only the combinations of the factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _).")] string where = "",
            [CliArg("derived", "Metrics computed per replication from the others and the study's factors (named as in where: B1.capacity -> B1_capacity), 'name=expression;...' (e.g. 'days=floor(span/24)', 'profit=10*out-100*B1_capacity'); reported as mean +- half-width like them.")] string derived = "",
            [CliArg("confidence", "Confidence level of the half-widths (0 = 0.95).")] double confidence = 0,
            [CliArg("warmup", "Warm-up time: statistics start after it (-1 = the model's).")] double warmup = -1,
            [CliArg("noProgress", "End condition: stop a run when no item has moved for this time while items wait (end reason NoProgress: a stall beside a periodic signal or reaction; 0 = off, -1 = the model's).")] double noProgress = -1,
            [CliArg("compareTo", "A scenario (its name or its 1-based position) the others are compared with: each other scenario gets paired = per metric the mean difference to it over the runs of the same seed and its half-width (common random numbers: far tighter than comparing two means).")] string compareTo = "",
            [CliArg("experimenter", "Run a scene Experimenter's study (its factors, scenarios, metrics and settings); horizon, replications other than 1, seed other than 1 and the metrics given here override / are added.")] string experimenter = "",
            [CliArg("horizon", "Horizon the run would use, echoed in settings (-1 = the model's).")] double horizon = -1,
            [CliArg("replications", "Replications the run would use, echoed in settings (a scenario's own replications win).")] int replications = 1,
            [CliArg("seed", "First seed the run would use, echoed in settings.")] int seed = 1)
        {
            var answer = AgentReply.With(AgentReply.Of(() =>
            {
                var spec = Spec(horizon, replications, seed, factors, metrics, "", scenarios, where, derived, confidence, warmup, noProgress, compareTo);
                var study = MakeStudy(spec, root, experimenter, out var options);
                var check = study.Check();
                var m = study.Model;
                // footprints that overlap on the floor: information, not an error (the run does not care; the picture does)
                var info = check.Info.Concat(ModelLayout.Overlaps(ModelLayout.Elements(string.IsNullOrEmpty(root) ? null : FindObject(root)))).ToList();
                return new
                {
                    // the run settings a run of this study would use (the time unit made visible:
                    // seconds and minutes are easy to confuse)
                    settings = StudySpec.Settings(study),
                    elements = m.Elements.Count, connections = m.Connections.Count, teams = m.ResourceTeams.Count,
                    scenarios = check.Ok ? study.Plan().Count : (int?)null, metrics = study.Metrics.Count,
                    errors = check.Errors, warnings = check.Warnings.Concat(options.Warnings).ToList(), info,
                };
            }), null, null);
            answer["ok"] = ((Newtonsoft.Json.Linq.JArray)answer["errors"]).Count == 0;
            return answer;
        }

        // ── running ──────────────────────────────────────────────────────────

        [CliCommand("simulean_run_headless", "Run the scene's model headless (the scene is not changed) and return per scenario " +
                                             "the end reasons counted, the named metrics (6 significant digits) and, when a run deadlocked or stalled, the " +
                                             "model block (makespan, deadlock and stall times…); with detail=full (the default without " +
                                             "metrics) every digit, the model block, the elements' (input, output, content, stay, " +
                                             "utilisation) and resources' KPIs and each run's end reason and " +
                                             "metric values (perRun). An answer over ~40 KB is a table of the first scenarios plus page: the " +
                                             "arguments (top, offset, sort) that read every scenario, a page at a time, with simulean_results action=read.", Tags = new[] { Tag })]
        public static object RunHeadless(
            [CliArg("horizon", "Horizon in model time units (-1 = the model's).")] double horizon = -1,
            [CliArg("replications", "Replications (seeds seed, seed+1, …).")] int replications = 1,
            [CliArg("seed", "First seed.")] int seed = 1,
            [CliArg("factors", "Catalog overrides 'path=value;…' (e.g. 'Buffer.capacity=4;team:Crew.members[skill=weld]=3'); " +
                               "a value list 'path=1|2|4' makes one scenario per level. Or JSON: {\"Buffer.capacity\": [2, 4], " +
                           "\"Src.tableSource\": \"sql: SELECT a; b\"} (values may then hold ; = |).")] string factors = "",
            [CliArg("metrics", "Named metrics 'name=path;…' (e.g. 'span=model.makespan;out=Sink.input'), or JSON " +
                               "{\"span\": \"model.makespan\", \"out\": \"Sink.input\"}.")] string metrics = "",
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "",
            [CliArg("detail", "'metrics' = the named metrics per scenario to 6 significant digits, the model block when a run " +
                              "stalled (the default when metrics are given: small enough for an agent's context); 'full' = every digit, the " +
                              "model block, every element's and resource's KPIs and perRun (each run's end reason and metric values).")] string detail = "",
            [CliArg("scenarios", "Explicit scenarios instead of factors (JSON): [{\"name\": \"A\", \"Buf.capacity\": 2, \"replications\": 5}, ...] - every scenario gives every factor.")] string scenarios = "",
            [CliArg("where", "Keep only the combinations of the factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _).")] string where = "",
            [CliArg("derived", "Metrics computed per replication from the others and the study's factors (named as in where: B1.capacity -> B1_capacity), 'name=expression;...' (e.g. 'days=floor(span/24)', 'profit=10*out-100*B1_capacity'); reported as mean +- half-width like them.")] string derived = "",
            [CliArg("confidence", "Confidence level of the half-widths (0 = 0.95).")] double confidence = 0,
            [CliArg("warmup", "Warm-up time: statistics start after it (-1 = the model's).")] double warmup = -1,
            [CliArg("noProgress", "End condition: stop a run when no item has moved for this time while items wait (end reason NoProgress: a stall beside a periodic signal or reaction; 0 = off, -1 = the model's).")] double noProgress = -1,
            [CliArg("compareTo", "A scenario (its name or its 1-based position) the others are compared with: each other scenario gets paired = per metric the mean difference to it over the runs of the same seed and its half-width (common random numbers: far tighter than comparing two means).")] string compareTo = "",
            [CliArg("experimenter", "Run a scene Experimenter's study (its factors, scenarios, metrics and settings); horizon, replications other than 1, seed other than 1 and the metrics given here override / are added.")] string experimenter = "",
            [CliArg("top", "A page of the answer: how many scenarios it gives (0 = 25); an answer over ~40 KB gives a table of the first ones and the arguments of its pages.")] int top = 0,
            [CliArg("sort", "A page's order: a metric (ascending), -metric or 'metric desc' descending (empty = the plan's order; a large answer's table: the first metric).")] string sort = "",
            [CliArg("offset", "A page's first scenario, from 0 (the answer's page.next gives the next page).")] int offset = 0)
            => AgentReply.Of(() =>
            {
                var spec = Spec(horizon, replications, seed, factors, metrics, detail, scenarios, where, derived, confidence, warmup, noProgress, compareTo);
                var study = BuildStudy(spec, root, experimenter, out var options, out var warnings);
                var settings = StudySpec.Settings(study);
                var executor = ExternalRunner.Prepare();
                var results = executor.Run(study);
                settings["ranIn"] = executor.Description;
                options.Finish(results);
                var last = LastStudyOf(Kept(results, ResultsOutput.ProjectRoot, UnityEngine.SceneManagement.SceneManager.GetActiveScene().name),
                                       spec.Full, warnings, settings, study.Model, spec.CompareTo);
                return AnswerPages.Study(last.Answer(spec.Full), top, sort, offset, PagesOf("last", spec.Full), MaxAnswer);
            });

        /// <summary>
        /// The last study the commands ran (<c>simulean_run_headless</c>, <c>simulean_run_start</c>) with what its answer is
        /// made of — its result set (<see cref="ResultsOutput.Last"/>), warnings, settings, model and reference scenario —, so
        /// that <c>simulean_results action=read resultSet=last</c> gives the same answer, a page at a time, without running
        /// it again. Written by the worker of a background run, read on the main thread: one reference, replaced whole.
        /// </summary>
        private sealed class StudyAnswerSource
        {
            public SimuLean.Results.ResultSet Set;
            public bool Full;
            public List<string> Warnings;
            public Dictionary<string, object> Settings;
            public SimuLean.Serialization.SimulationConfig Model;
            public string CompareTo;

            public Dictionary<string, object> Answer(bool full)
                => StudySpec.Answer(Set, full, Warnings, Settings == null ? null : new Dictionary<string, object>(Settings), Model, CompareTo);
        }
        private static volatile StudyAnswerSource lastStudy;

        private static StudyAnswerSource LastStudyOf(SimuLean.Results.ResultSet set, bool full, List<string> warnings, Dictionary<string, object> settings,
                                                     SimuLean.Serialization.SimulationConfig model, string compareTo)
        {
            var last = new StudyAnswerSource { Set = set, Full = full, Warnings = warnings.ToList(), Settings = settings, Model = model, CompareTo = compareTo };
            lastStudy = last;
            return last;
        }

        /// <summary>The command that reads the pages of a result set's answer (<see cref="AnswerPages.Study"/>'s <c>more</c>).</summary>
        private static string PagesOf(string resultSet, bool full)
            => $"simulean_results action=read resultSet={resultSet}" + (full ? " detail=full" : "");

        /// <summary>The study of a command's arguments as data (the engine reads them, as the runner does).</summary>
        private static StudySpec Spec(double horizon, int replications, int seed, string factors, string metrics, string detail,
                                      string scenarios, string where, string derived, double confidence, double warmup, double noProgress,
                                      string compareTo = "")
            => new StudySpec
            {
                Horizon = horizon, Replications = replications, Seed = seed, Factors = factors ?? "", Metrics = metrics ?? "", Detail = detail ?? "",
                Scenarios = scenarios ?? "", Where = where ?? "", Derived = derived ?? "", Confidence = confidence, Warmup = warmup,
                NoProgress = noProgress, CompareTo = compareTo ?? "",
            };

        // ── long runs in the background ──────────────────────

        private sealed class BackgroundRun
        {
            public System.Threading.Tasks.Task<Dictionary<string, object>> Task;
            /// <summary>Whether the study answers in full (detail=full): its pages say so.</summary>
            public bool Full;
            public System.Threading.CancellationTokenSource Cancel;
            public int Done, Total;
            public DateTime Started;
            /// <summary>What is logged while the runs go on (worker threads included), for the completed answer.</summary>
            public AgentReply.LogCapture Log;
        }
        private static BackgroundRun background;

        /// <summary>Blocks until the background run of <c>simulean_run_start</c> has ended (its status is no longer
        /// running), at most <paramref name="seconds"/>; true when it ended (or there is none). Its work runs on worker
        /// threads, so blocking the main thread does not hold it up.</summary>
        internal static bool WaitForBackgroundRun(double seconds)
        {
            var run = background;
            if (run == null) return true;
            try { return run.Task.Wait(TimeSpan.FromSeconds(seconds)); }
            catch (AggregateException) { return true; }   // it failed: ended all the same (the status says how)
        }

        [CliCommand("simulean_run_start", "Start simulean_run_headless in the background (same arguments) and return at once: a long " +
                                          "study no longer blocks the editor or times the MCP out. Poll simulean_run_status; one run " +
                                          "at a time.", Tags = new[] { Tag })]
        public static object RunStart(
            [CliArg("horizon", "Horizon in model time units (-1 = the model's).")] double horizon = -1,
            [CliArg("replications", "Replications (seeds seed, seed+1, …).")] int replications = 1,
            [CliArg("seed", "First seed.")] int seed = 1,
            [CliArg("factors", "Catalog overrides 'path=v1|v2;…' or JSON, as simulean_run_headless.")] string factors = "",
            [CliArg("metrics", "Named metrics 'name=path;…' or JSON, as simulean_run_headless.")] string metrics = "",
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "",
            [CliArg("detail", "'metrics' or 'full', as simulean_run_headless.")] string detail = "",
            [CliArg("scenarios", "Explicit scenarios instead of factors (JSON): [{\"name\": \"A\", \"Buf.capacity\": 2, \"replications\": 5}, ...] - every scenario gives every factor.")] string scenarios = "",
            [CliArg("where", "Keep only the combinations of the factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _).")] string where = "",
            [CliArg("derived", "Metrics computed per replication from the others and the study's factors (named as in where: B1.capacity -> B1_capacity), 'name=expression;...' (e.g. 'days=floor(span/24)', 'profit=10*out-100*B1_capacity'); reported as mean +- half-width like them.")] string derived = "",
            [CliArg("confidence", "Confidence level of the half-widths (0 = 0.95).")] double confidence = 0,
            [CliArg("warmup", "Warm-up time: statistics start after it (-1 = the model's).")] double warmup = -1,
            [CliArg("noProgress", "End condition: stop a run when no item has moved for this time while items wait (end reason NoProgress: a stall beside a periodic signal or reaction; 0 = off, -1 = the model's).")] double noProgress = -1,
            [CliArg("compareTo", "A scenario (its name or its 1-based position) the others are compared with: each other scenario gets paired = per metric the mean difference to it over the runs of the same seed and its half-width (common random numbers: far tighter than comparing two means).")] string compareTo = "",
            [CliArg("experimenter", "Run a scene Experimenter's study (its factors, scenarios, metrics and settings); horizon, replications other than 1, seed other than 1 and the metrics given here override / are added.")] string experimenter = "")
            => AgentReply.Of(() => RunStartOf(Spec(horizon, replications, seed, factors, metrics, detail, scenarios, where, derived, confidence, warmup, noProgress, compareTo),
                                              root, experimenter));

        private static object RunStartOf(StudySpec spec, string root, string experimenter)
        {
            if (background != null && !background.Task.IsCompleted)
                throw new InvalidOperationException("A background run is going on: poll simulean_run_status (or cancel it there).");
            // the scene is read here, on the main thread; the runs go on a worker (the engine never touches Unity)
            var study = BuildStudy(spec, root, experimenter, out var options, out var warnings);
            bool full = spec.Full;
            string compareTo = spec.CompareTo;
            var settings = StudySpec.Settings(study);
            var startSettings = new Dictionary<string, object>(settings);
            var startWarnings = warnings.ToList();
            study.Threads = 0;                                   // all cores: the editor's main thread is free anyway
            var executor = ExternalRunner.Prepare();             // main thread: the runner outside the editor, or here
            var run = new BackgroundRun { Cancel = new System.Threading.CancellationTokenSource(), Started = DateTime.Now, Log = new AgentReply.LogCapture(job: true), Full = full };
            string projectRoot = ResultsOutput.ProjectRoot, scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            // the run's scope flows into its worker (and the parallel runs it starts): its messages go to it alone
            using (run.Log.Enter(detached: true))
            run.Task = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var results = executor.Run(study, run.Cancel.Token, (d, t) => { run.Done = d; run.Total = t; });
                    settings["ranIn"] = executor.Description;
                    options.Finish(results);
                    // the answer whole: simulean_run_status gives it, or a page of it
                    return LastStudyOf(Kept(results, projectRoot, scene), full, warnings, settings, study.Model, compareTo).Answer(full);
                }
                finally { run.Log.Dispose(); }
            });
            background = run;
            // copies: the worker writes settings["ranIn"] (and the answer reads warnings) while this answer is serialised on
            // the main thread — a short study ended during it ("Collection was modified", the intermittent
            // PipelineCommandsTests failure)
            return new { status = "running", runs = study.Plan().Sum(p => Math.Max(1, p.Replications > 0 ? p.Replications : study.Replications)),
                         settings = new Dictionary<string, object>(startSettings), warnings = startWarnings };
        }

        [CliCommand("simulean_run_status", "The background run of simulean_run_start: 'running' (runs done / in all), then 'completed' " +
                                           "with the same answer as simulean_run_headless (over ~40 KB a table and its pages: top, offset, sort), or " +
                                           "'failed'. cancel=true stops it; wait=s answers as soon as the run ends (at most 55 s).", Tags = new[] { Tag })]
        public static System.Threading.Tasks.Task<object> RunStatus(
            [CliArg("cancel", "Stop the run (the runs done so far are kept).")] bool cancel = false,
            [CliArg("wait", "Seconds to wait for the job to end (0 = answer at once; at most 55): the answer comes as soon as it ends, so poll with wait instead of sleeping.")] double wait = 0,
            [CliArg("top", "A page of the answer: how many scenarios it gives (0 = 25); an answer over ~40 KB gives a table of the first ones and the arguments of its pages.")] int top = 0,
            [CliArg("sort", "A page's order: a metric (ascending), -metric or 'metric desc' descending (empty = the plan's order; a large answer's table: the first metric).")] string sort = "",
            [CliArg("offset", "A page's first scenario, from 0 (the answer's page.next gives the next page).")] int offset = 0)
        {
            if (cancel) AgentReply.Of(() => RunStatusOf(true, top, sort, offset));
            return JobWait.Until(() => AgentReply.Run(() => RunStatusOf(false, top, sort, offset)), wait);
        }

        private static object RunStatusOf(bool cancel, int top = 0, string sort = "", int offset = 0)
        {
            var run = background;
            if (run == null) return new { status = "none" };
            if (cancel && !run.Task.IsCompleted) run.Cancel.Cancel();
            if (!run.Task.IsCompleted)
                return new { status = "running", done = run.Done, total = run.Total, seconds = Math.Round((DateTime.Now - run.Started).TotalSeconds, 1) };
            if (run.Task.IsFaulted)
                return new { status = "failed", error = run.Task.Exception?.GetBaseException().Message,
                             errors = new[] { run.Task.Exception?.GetBaseException().Message }.Concat(run.Log.Errors).ToList(), warnings = run.Log.Warnings };
            var page = AnswerPages.Study(run.Task.Result, top, sort, offset, PagesOf("last", run.Full), MaxAnswer);
            return new { status = "completed", seconds = Math.Round((DateTime.Now - run.Started).TotalSeconds, 1),
                         result = AgentReply.With(page, run.Log.Errors, run.Log.Warnings) };
        }

        /// <summary>The study of <paramref name="spec"/> on the scene's model (or a scene Experimenter's), checked on the built
        /// model before any run (<see cref="Study.Check"/>): an error refuses the run (all its errors in the message);
        /// <paramref name="warnings"/> go into the answer.</summary>
        private static SimuLean.Optimization.Study BuildStudy(StudySpec spec, string root, string experimenter,
                                                              out StudyOptions options, out List<string> warnings)
        {
            var study = MakeStudy(spec, root, experimenter, out options);
            warnings = new List<string>(StudySpec.Checked(study));
            warnings.AddRange(options.Warnings);
            return study;
        }

        /// <summary>The study of <paramref name="spec"/> on the scene's model (extracted once), not checked.</summary>
        private static SimuLean.Optimization.Study MakeStudy(StudySpec spec, string root, string experimenter, out StudyOptions options)
        {
            SimuLean.Optimization.Study study;
            bool designed = !string.IsNullOrWhiteSpace(experimenter);
            if (designed)
            {
                // a designed experiment saved in the scene runs in one call
                var component = FindObject(experimenter).GetComponent<SimuLean.Experimentation.Controller.Experimenter>()
                                ?? throw new ArgumentException($"'{experimenter}' has no Experimenter component.");
                study = component.PrepareStudy();
            }
            else study = SceneStudy.Of(string.IsNullOrEmpty(root) ? null : FindObject(root));
            options = spec.ApplyTo(study, designed);   // deterministic (one thread), and the editor stays responsive enough
            return study;
        }

        // a study of the MCP is published (the Results tab shows it) and never stored: agents' runs are working runs; the
        // user keeps one with Save in the Results tab
        private static SimuLean.Results.ResultSet Kept(SimuLean.Results.ResultSet set, string projectRoot, string scene)
        {
            set.Model = scene;
            ResultsOutput.Publish(set, projectRoot);
            return set;
        }

        [CliCommand("simulean_test", "Start the SimuLean EditMode tests (SimuLeanTestRunner; filter = part of a test name, empty = " +
                                     "all); poll simulean_test_status until 'completed'.", Tags = new[] { Tag, "tests" })]
        public static string Test([CliArg("filter", "Part of a test's full name (empty = all).")] string filter = "")
            => SimuLeanTestRunner.Start(filter ?? "");

        [CliCommand("simulean_test_status", "Status of the SimuLean EditMode tests started with simulean_test (JSON: status, passed, " +
                                            "failed, failures); wait=s answers as soon as the run ends (at most 55 s).", Tags = new[] { Tag, "tests" })]
        public static System.Threading.Tasks.Task<object> TestStatus([CliArg("wait", "Seconds to wait for the job to end (0 = answer at once; at most 55): the answer comes as soon as it ends, so poll with wait instead of sleeping.")] double wait = 0)
            => JobWait.Until(() => JobWait.Json(SimuLeanTestRunner.Status()), wait);

        // ── helpers ──────────────────────────────────────────────────────────

        private static SimuLeanWindow.Entry FindEntry(List<SimuLeanWindow.Entry> entries, string type)
            => TryFindEntry(entries, type)
               ?? throw new ArgumentException($"No Library entry '{type}'. Entries: {string.Join(", ", entries.Select(e => e.Name))}.");

        private static SimuLeanWindow.Entry TryFindEntry(List<SimuLeanWindow.Entry> entries, string type)
            => entries.FirstOrDefault(e => string.Equals(e.Name, type, StringComparison.OrdinalIgnoreCase))
               ?? entries.FirstOrDefault(e => e.Type != null && string.Equals(e.Type.Name, type, StringComparison.OrdinalIgnoreCase))
               ?? NormalisedEntry(entries, type);

        /// <summary>A name in any case, without spaces, hyphens or underscores ("MultiServer",
        /// which simulean_model prints, was refused): the entry whose Library name, component type, component type without
        /// its <c>Unity</c> prefix or engine type matches it, when one does (or several of one component: the plain element
        /// before its presets). Null when none or several differ.</summary>
        private static SimuLeanWindow.Entry NormalisedEntry(List<SimuLeanWindow.Entry> entries, string type)
        {
            string key = Normalised(type);
            if (key.Length == 0) return null;
            var registry = SimuLean.Serialization.ElementExtractorRegistry.CreateDefault();
            var matches = entries.Where(e => AliasesOf(e, registry).Contains(key)).ToList();
            if (matches.Count == 0) return null;
            if (matches.Select(e => (object)e.Type ?? e.Name).Distinct().Count() > 1) return null;
            return matches.FirstOrDefault(e => e.Type != null && Normalised(e.Name) == key)
                   ?? matches.FirstOrDefault(e => e.Type != null && !e.HasPrefab) ?? matches[0];
        }

        private static HashSet<string> AliasesOf(SimuLeanWindow.Entry e, SimuLean.Serialization.ElementExtractorRegistry registry)
        {
            var set = new HashSet<string> { Normalised(e.Name) };
            if (e.Type != null)
            {
                set.Add(Normalised(e.Type.Name));
                if (e.Type.Name.StartsWith("Unity", StringComparison.Ordinal)) set.Add(Normalised(e.Type.Name.Substring(5)));
                string engine = registry.EngineTypeOf(e.Type.Name);
                if (!string.IsNullOrEmpty(engine)) set.Add(Normalised(engine));
            }
            set.Remove("");
            return set;
        }

        private static string Normalised(string name)
            => new string((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // The textbook names of other simulation tools (a server, a router, a conveyor) are not spelt like any Library
        // entry, so the did-you-mean found nothing for them: name the entry, or the mechanism, that plays that part.
        private static readonly (string[] words, string hint)[] GenericTypes =
        {
            (new[] { "server", "singleserver", "machine", "station", "workstation", "workcenter", "processor", "process" },
             " A server or machine is a Multi-Server: its 'capacity' is the number of parallel slots (1 = a single server)."),
            (new[] { "router", "splitter", "decide", "decision", "branch", "switch", "dispatcher" },
             " Routing is not an element: every element sends to its Next Elements by its 'outputStrategy' (Probability, " +
             "FirstAvailable, BestScore, Expression, a rule); a Queue in front of the branches is the usual router."),
            (new[] { "conveyor", "accumulatingconveyor", "belt", "transporter" },
             " A conveyor is a Length-Limited Lane (items accumulate by their length) or a Multi-Server / Delay with the transit time."),
            (new[] { "buffer", "store", "waitingline", "waitingroom" }, " A buffer is a Queue ('capacity' = its places)."),
            (new[] { "exit", "drain", "dispose" }, " Items leave the model at a Sink."),
            (new[] { "generator", "arrivals", "create" }, " Items enter the model at a Source."),
            (new[] { "assembly", "assemble", "join", "match" }, " Parts are joined by a Combiner (or an Assembly Station)."),
        };

        private static string GenericTypeHint(string normalised)
        {
            foreach (var (words, hint) in GenericTypes)
                if (words.Contains(normalised)) return hint;
            return null;
        }

        // the tools of the window's other tabs (Visualization, Analysis, Experiments…: labels, arrows, speed profile,
        // recorder, dashboard) — the scene views a model is shown with
        private static List<(Type type, SimuLeanToolAttribute info)> OtherTools(List<SimuLeanWindow.Entry> entries)
            => SimuLeanWindow.CollectTools().Where(t => entries.All(e => e.Type != t.type) && !t.info.OnMainCamera).ToList();

        private static (Type type, SimuLeanToolAttribute info) FindTool(string type, List<SimuLeanWindow.Entry> entries)
        {
            var tools = OtherTools(entries);
            var tool = tools.FirstOrDefault(t => string.Equals(t.info.DisplayName, type, StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(t.type.Name, type, StringComparison.OrdinalIgnoreCase));
            if (tool.type == null)
            {
                string key = Normalised(type);
                var near = tools.Where(t => Normalised(t.info.DisplayName) == key || Normalised(t.type.Name) == key).ToList();
                if (near.Count == 1) return near[0];
            }
            if (tool.type == null)
            {
                // a short did-you-mean (the error listed all ~150 entries); the names that share the most
                string key = Normalised(type);
                var registry = SimuLean.Serialization.ElementExtractorRegistry.CreateDefault();
                var ambiguous = key.Length == 0 ? new List<string>() : entries.Where(e => AliasesOf(e, registry).Contains(key)).Select(e => e.Name).Distinct().ToList();
                if (ambiguous.Count > 1)
                    throw new ArgumentException($"'{type}' names several Library entries: {string.Join(", ", ambiguous)} — give one of them.");
                var names = entries.Select(e => e.Name).Concat(tools.Select(t => t.info.DisplayName))
                                   .Concat(entries.Where(e => e.Type != null).Select(e => e.Type.Name)).Distinct().ToList();
                var contains = key.Length < 3 ? new List<string>() : names.Where(n => Normalised(n).Contains(key) || key.Contains(Normalised(n)) && Normalised(n).Length >= 4).Take(6).ToList();
                throw new ArgumentException($"No Library entry or tool '{type}'." + (GenericTypeHint(key) ?? NameSuggestions.DidYouMean(type, names) +
                                            (contains.Count > 0 ? $" Close: {string.Join(", ", contains)}." : "")) +
                                            " simulean_types (no type) lists them all.");
            }
            return tool;
        }

        private static GameObject PlaceTool((Type type, SimuLeanToolAttribute info) tool, Vector3 position, Transform under)
        {
            if (tool.info.Singleton && UnityCompat.FindAll(tool.type, true).OfType<Component>().FirstOrDefault(c => c.gameObject.scene.IsValid()) is Component existing)
                return existing.gameObject;
            var go = new GameObject(GameObjectUtility.GetUniqueNameForSibling(under, tool.info.DisplayName));
            Undo.RegisterCreatedObjectUndo(go, "Create " + tool.info.DisplayName);
            if (under != null) go.transform.SetParent(under, false);
            go.transform.position = position;
            Undo.AddComponent(go, tool.type);
            if (under == null) ModelHierarchy.Place(go);   // in its hierarchy group, as the Library places
            return go;
        }

        /// <summary>A scene object by hierarchy path ('/A/B' or 'A/B') or by name (unique among the loaded scenes).</summary>
        internal static GameObject FindObject(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) throw new ArgumentException("No object named.");
            string path = nameOrPath.Trim().TrimStart('/');
            var all = UnityCompat.FindAll<Transform>(true).Where(t => t != null && t.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(t)).ToList();
            var matches = path.Contains('/') ? all.Where(t => PathOf(t) == path).ToList() : all.Where(t => t.name == path).ToList();
            if (matches.Count == 1) return matches[0].gameObject;
            if (matches.Count == 0)
                throw new ArgumentException($"No object '{nameOrPath}' in the open scenes." + NameSuggestions.DidYouMean(path,
                    all.Where(t => (t.gameObject.hideFlags & HideFlags.DontSave) == 0 && t.GetComponents<MonoBehaviour>().Any(IsSimuLean)).Select(t => t.name)));
            throw new ArgumentException($"Several objects are named '{nameOrPath}': use the path ({string.Join(", ", matches.Select(PathOf))}).");
        }

        // ── unique names ─────────────────────────────────────────

        /// <summary>The SimuLean objects (elements, teams, lists, tables, tools) of the open scenes named
        /// <paramref name="name"/>, other than <paramref name="except"/> (edit-time previews left out).</summary>
        private static List<Transform> NamedLike(string name, GameObject except)
            => UnityCompat.FindAll<Transform>(true)
                .Where(t => t != null && t.gameObject != except && t.name == name && t.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(t)
                            && (t.gameObject.hideFlags & HideFlags.DontSave) == 0 && t.GetComponents<MonoBehaviour>().Any(IsSimuLean))
                .ToList();

        private static bool IsTaken(string name, GameObject except) => NamedLike(name, except).Count > 0;

        /// <summary>
        /// Refuses a name another SimuLean object already has: studies, metrics and the commands find
        /// elements, teams and lists by name, so two with one name made a factor or a metric take the first silently.
        /// </summary>
        internal static void EnsureFreeName(string name, GameObject except = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An empty name.");
            if (name.Contains('/')) throw new ArgumentException($"'{name}': a name cannot contain '/' (it separates a hierarchy path).");
            var taken = NamedLike(name, except);
            if (taken.Count > 0)
                throw new ArgumentException($"'{name}' is already the name of '{PathOf(taken[0])}' " +
                                            $"({taken[0].GetComponents<MonoBehaviour>().First(IsSimuLean).GetType().Name}): elements, teams " +
                                            $"and lists are found by name in studies and metrics, so each name is used once. Choose another, " +
                                            $"e.g. '{FreeName(name, except)}'.");
        }

        /// <summary>"Queue" → "Queue (2)", the first free.</summary>
        private static string FreeName(string name, GameObject except)
        {
            for (int k = 2; ; k++)
                if (!IsTaken($"{name} ({k})", except)) return $"{name} ({k})";
        }

        internal static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

        internal static bool IsSimuLean(Component c) => c != null && (c.GetType().Namespace ?? "").StartsWith("SimuLean");

        internal static Component SimuLeanComponent(GameObject go)
            => (Component)go.GetComponent<SElement>() ?? go.GetComponents<MonoBehaviour>().FirstOrDefault(IsSimuLean)
               ?? throw new ArgumentException($"'{go.name}' has no SimuLean component.");

        private static object Describe(GameObject go, string placedAt = null)
        {
            var c = go.GetComponents<MonoBehaviour>().FirstOrDefault(IsSimuLean);
            return new { name = go.name, path = PathOf(go.transform), component = c != null ? c.GetType().Name : null,
                         position = new[] { go.transform.position.x, go.transform.position.y, go.transform.position.z },
                         size = ModelLayout.SizeOf(go),   // its footprint [x, z] in metres
                         placedAt };                      // set when it was placed beside the model
        }

        private sealed class FieldInfo { public string path, type, value; }

        /// <summary>The visible serialized fields, nested ones by path (list elements left out).</summary>
        private static List<FieldInfo> Fields(Component c)
        {
            var list = new List<FieldInfo>();
            var so = new SerializedObject(c);
            var p = so.GetIterator();
            bool enter = true;
            while (p.NextVisible(enter))
            {
                enter = p.propertyType == SerializedPropertyType.Generic && !p.isArray && p.depth < 2;
                if (p.propertyPath == "m_Script" || p.propertyPath.Contains(".Array.")) continue;
                if (p.propertyType == SerializedPropertyType.Generic && !p.isArray) continue;   // its fields follow
                list.Add(new FieldInfo { path = p.propertyPath, type = TypeOf(p), value = ValueOf(p) });
            }
            return ActiveDistributionOnly(list);
        }

        // A distribution (RandomProcessConfig: strategyType + the fields of every type) shows only the fields of its active
        // type: 'delay.strategyType = Uniform' keeps delay.uniformMin / uniformMax, not the ~30 others.
        private static readonly Dictionary<string, string> DistributionPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Constant"] = "constant", ["Uniform"] = "uniform", ["Normal"] = "normal", ["Exponential"] = "exponential",
            ["Triangular"] = "triangular", ["Expression"] = "expression", ["Gamma"] = "gamma", ["Weibull"] = "weibull",
            ["LogNormal"] = "lognormal", ["Beta"] = "beta", ["ChiSquare"] = "chisquare", ["StudentT"] = "studentt",
            ["FDistribution"] = "fdist", ["Poisson"] = "poisson", ["Binomial"] = "binomial", ["DiscreteUniform"] = "discreteuniform",
        };

        /// <summary>
        /// A field of a distribution that is not the active one (e.g. <c>delay.constantValue</c> while
        /// <c>delay.strategyType</c> is Uniform) changes nothing in a run — say so, naming the active type. Null otherwise.
        /// </summary>
        internal static string InactiveDistributionWarning(SerializedObject so, string key)
        {
            int dot = key.LastIndexOf('.');
            string parent = dot >= 0 ? key.Substring(0, dot + 1) : "", leaf = dot >= 0 ? key.Substring(dot + 1) : key;
            if (leaf == "strategyType") return null;
            var type = so.FindProperty(parent + "strategyType");
            if (type == null || type.propertyType != SerializedPropertyType.Enum || type.enumValueIndex < 0) return null;
            string active = type.enumNames[type.enumValueIndex];
            if (!DistributionPrefixes.TryGetValue(active, out string prefix)) return null;
            bool distributionField = DistributionPrefixes.Values.Any(v => leaf.StartsWith(v, StringComparison.OrdinalIgnoreCase));
            if (!distributionField || leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !(prefix == "uniform" && leaf.StartsWith("discreteuniform", StringComparison.OrdinalIgnoreCase))) return null;
            return $"'{key}' belongs to another distribution: the active one is {active}, so a run does not use it. " +
                   $"Set '{parent}strategyType' too.";
        }

        private static List<FieldInfo> ActiveDistributionOnly(List<FieldInfo> list)
        {
            var active = new Dictionary<string, string>();   // parent path → active field prefix
            foreach (var f in list)
            {
                if (!f.path.EndsWith("strategyType") || !f.type.StartsWith("enum(Constant|")) continue;
                string parent = f.path.Length > "strategyType".Length ? f.path.Substring(0, f.path.Length - "strategyType".Length) : "";
                if (DistributionPrefixes.TryGetValue(f.value ?? "", out string prefix)) active[parent] = prefix;
            }
            if (active.Count == 0) return list;
            return list.FindAll(f =>
            {
                int dot = f.path.LastIndexOf('.');
                string parent = dot >= 0 ? f.path.Substring(0, dot + 1) : "";
                string leaf = dot >= 0 ? f.path.Substring(dot + 1) : f.path;
                if (!active.TryGetValue(parent, out string prefix) || leaf == "strategyType") return true;
                bool distributionField = DistributionPrefixes.Values.Any(v => leaf.StartsWith(v, StringComparison.OrdinalIgnoreCase));
                return !distributionField || leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                       && !(prefix == "uniform" && leaf.StartsWith("discreteuniform", StringComparison.OrdinalIgnoreCase));
            });
        }

        /// <summary>The element's config keys and values (what the catalog / a study varies); empty for a tool.</summary>
        /// <summary>The catalog keys under the <c>fields</c> paths (<c>fields=lanes</c> still answered ~130
        /// per-lane keys of the whole catalog): a key equal to a path or continuing it with <c>.</c> or <c>[</c>; no filter =
        /// every key.</summary>
        internal static Dictionary<string, string> CatalogUnder(Dictionary<string, string> catalog, string fields)
        {
            var prefixes = SplitList(fields).Select(x => x.Trim().TrimEnd('.')).Where(x => x.Length > 0).ToList();
            if (prefixes.Count == 0) return catalog;
            bool Under(string key) => prefixes.Any(p => key == p || key.StartsWith(p, StringComparison.Ordinal) && key.Length > p.Length
                                                                    && (key[p.Length] == '.' || key[p.Length] == '['));
            return catalog.Where(kv => Under(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        /// <summary>The text settings of a catalog list (<see cref="ParameterInfo.IsText"/>): key → its kind and current text —
        /// the keys a study varies by texts (a priority expression, a request order written in place), never by a range.</summary>
        private static SortedDictionary<string, object> TextFactors(IEnumerable<ParameterInfo> catalog)
        {
            var d = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in catalog) if (p.IsText) d[p.Key] = new Dictionary<string, object> { ["kind"] = "text", ["now"] = p.CurrentText };
            return d;
        }

        private static SortedDictionary<string, object> TextFactorsUnder(SortedDictionary<string, object> texts, string fields)
        {
            var keep = CatalogUnder(texts.Keys.ToDictionary(k => k, k => k), fields);
            return new SortedDictionary<string, object>(texts.Where(kv => keep.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        }

        /// <param name="texts">The text settings among its catalog keys (an element's, or a team's for a resource team).</param>
        private static Dictionary<string, string> CatalogOf(GameObject holder, Component c, out SortedDictionary<string, object> texts)
        {
            var result = new Dictionary<string, string>();
            texts = new SortedDictionary<string, object>(StringComparer.Ordinal);
            if (c is UnityResourceTeam team) { texts = TextFactors(ModelParameterCatalog.ForTeam(UnityResourceTeamExtractor.Extract(team, team.TeamName))); return result; }
            if (!(c is SElement element)) return result;
            var x = SceneExtractor.Extract(holder);
            if (!x.ElementIds.TryGetValue(element, out string id)) return result;
            var config = x.Config.Elements.Find(e => e.Id == id);
            if (config == null) return result;
            texts = TextFactors(ModelParameterCatalog.ForElement(config));
            // a value that is a team's, list's or element's internal id (Setter_576743:2304) is shown by its name
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in x.Config.ResourceTeams) if (!string.IsNullOrEmpty(t?.Id) && !string.IsNullOrEmpty(t.Name)) names[t.Id] = t.Name;
            foreach (var l in x.Config.Lists) if (!string.IsNullOrEmpty(l?.Id) && !string.IsNullOrEmpty(l.Name)) names[l.Id] = l.Name;
            foreach (var e in x.Config.Elements) if (!string.IsNullOrEmpty(e?.Id) && !string.IsNullOrEmpty(e.Name)) names[e.Id] = e.Name;
            foreach (var (path, value) in config.Values())
                result[path] = value is string s && names.TryGetValue(s, out string name) ? name : Text(value);
            return result;
        }

        private static string Text(object v)
        {
            switch (v)
            {
                case null: return "";
                case string s: return s;
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                case System.Collections.IDictionary d: return $"({d.Count} entries)";
                case System.Collections.IEnumerable e: return "[" + string.Join(", ", e.Cast<object>().Select(Text)) + "]";
                default: return v.ToString();
            }
        }

        private static string TypeOf(SerializedProperty p)
        {
            if (p.isArray && p.propertyType != SerializedPropertyType.String) return "list";
            switch (p.propertyType)
            {
                case SerializedPropertyType.Enum: return "enum(" + string.Join("|", p.enumNames) + ")";
                case SerializedPropertyType.ObjectReference: return "object(" + ObjectTypeName(p) + ")";
                default: return p.propertyType.ToString().ToLowerInvariant();
            }
        }

        private static string ObjectTypeName(SerializedProperty p)
        {
            string t = p.type;                                      // "PPtr<$UnityResourceTeam>"
            int a = t.IndexOf('$'), b = t.LastIndexOf('>');
            return a >= 0 && b > a ? t.Substring(a + 1, b - a - 1) : t;
        }

        private static string ValueOf(SerializedProperty p)
        {
            if (p == null) return null;
            if (IsDistribution(p)) return DistributionText(p);   // Triangular~3~4~6, not (RandomProcessConfig)
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                var items = new List<string>();
                bool records = p.arraySize > 0 && p.GetArrayElementAtIndex(0).propertyType == SerializedPropertyType.Generic;
                int shown = Math.Min(p.arraySize, records ? 20 : 50);
                for (int i = 0; i < shown; i++)
                {
                    var item = p.GetArrayElementAtIndex(i);
                    // an empty object slot reads (none), not a blank between commas (inputOrder = [, ])
                    items.Add(item.propertyType == SerializedPropertyType.Generic ? ItemText(item, i)
                              : item.propertyType == SerializedPropertyType.ObjectReference && item.objectReferenceValue == null ? "(none)"
                              : ValueOf(item));
                }
                return "[" + string.Join(records ? "; " : ", ", items) + (p.arraySize > shown ? $"{(records ? ";" : ",")} … ({p.arraySize} items)" : "") + "]";
            }
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float: return p.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum: return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.intValue.ToString();
                case SerializedPropertyType.ObjectReference: return p.objectReferenceValue != null ? p.objectReferenceValue.name : "";
                case SerializedPropertyType.Vector3: return Invariant(p.vector3Value);
                case SerializedPropertyType.Vector2: return $"{p.vector2Value.x.ToString(CultureInfo.InvariantCulture)},{p.vector2Value.y.ToString(CultureInfo.InvariantCulture)}";
                case SerializedPropertyType.Color: return "#" + ColorUtility.ToHtmlStringRGBA(p.colorValue);
                default: return "(" + p.type + ")";
            }
        }

        /// <summary>
        /// A list item that is a record, readably (items printed as their type,
        /// <c>(RuleParameterValue)</c>): a rule parameter as <c>name = value</c>; a team member by its name (the generated one,
        /// <c>Operators_1</c>, when empty) and its properties; any other record as its set fields, <c>name</c> / <c>id</c>
        /// first, at most six, nested records and empty values left out (<c>simulean_params fields=…</c> or
        /// <c>detail=full</c> do not change this: the item's fields are listed under <c>listItems</c>).
        /// </summary>
        private static string ItemText(SerializedProperty item, int index)
        {
            if (item.type == nameof(RuleParameterValue))
                return $"{item.FindPropertyRelative("name")?.stringValue?.Trim()} = {item.FindPropertyRelative("value")?.stringValue}";
            if (item.type == nameof(ResourceMemberSpec))
            {
                string n = item.FindPropertyRelative("name")?.stringValue?.Trim();
                if (string.IsNullOrEmpty(n))
                    n = (item.serializedObject.targetObject is UnityResourceTeam t ? t.TeamName : "member") + "_" + (index + 1);
                var props = item.FindPropertyRelative("properties");
                string ps = props != null && props.arraySize > 0 ? ValueOf(props) : null;
                return ps != null ? $"{n} {ps}" : n;
            }
            string[] first = { "name", "id", "laneId", "sku", "kind" };
            // the item's values and a fresh item's: a field at its default is left out (an action lists what it sets)
            object current = null, fresh = null;
            try
            {
                current = item.boxedValue;
                if (current != null && current.GetType().GetConstructor(Type.EmptyTypes) != null) fresh = Activator.CreateInstance(current.GetType());
            }
            catch (Exception) { fresh = null; }
            bool AtDefault(string field)
            {
                if (fresh == null || Array.IndexOf(first, field) >= 0) return false;
                var f = current.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f == null) return false;
                object a = f.GetValue(current), b = f.GetValue(fresh);
                if (a is System.Collections.ICollection ca && b is System.Collections.ICollection cb) return ca.Count == 0 && cb.Count == 0;
                return Equals(a, b) || (a is Object ua && b is Object ub ? ua == ub : false);
            }
            var parts = new List<(string key, string value)>();
            var child = item.Copy();
            var end = item.GetEndProperty();
            bool enter = true;
            while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
            {
                enter = false;
                if (child.depth != item.depth + 1 || AtDefault(child.name)) continue;
                string v;
                if (child.isArray && child.propertyType != SerializedPropertyType.String)
                    v = child.arraySize == 0 ? null
                      : child.GetArrayElementAtIndex(0).propertyType == SerializedPropertyType.Generic ? $"({child.arraySize} items)" : ValueOf(child);
                else if (child.propertyType == SerializedPropertyType.Generic) v = null;   // a nested group
                else v = ValueOf(child);
                if (string.IsNullOrEmpty(v) || v == "[]") continue;
                if (v.Length > 40) v = v.Substring(0, 37) + "…";
                parts.Add((child.name, v));
            }
            var ordered = parts.OrderBy(x => Array.IndexOf(first, x.key) is int k && k >= 0 ? k : first.Length).ToList();
            if (ordered.Count == 0) return "{}";
            return "{" + string.Join(", ", ordered.Take(6).Select(x => x.key + " = " + x.value)) + (ordered.Count > 6 ? ", …" : "") + "}";
        }

        private static string Invariant(Vector3 v)
            => $"{v.x.ToString(CultureInfo.InvariantCulture)},{v.y.ToString(CultureInfo.InvariantCulture)},{v.z.ToString(CultureInfo.InvariantCulture)}";

        private static void Assign(SerializedProperty p, string value)
        {
            value = value ?? "";
            if (IsDistribution(p)) { AssignDistribution(p, value); return; }   // 'Triangular~0.6~1~1.6'
            if (IsRuleParameterList(p)) { AssignRuleParameters(p, value); return; }   // 'FreeWeight = 0.5'
            if (p.propertyType == SerializedPropertyType.Generic && p.type == nameof(SimuLean.Collections.ListQuerySpec))
            { AssignQuery(p, value); return; }   // a pull query as one text: "sku == 'A'; qty > 0 | due desc"
            if (p.isArray && p.arrayElementType == nameof(ResourceMemberSpec) && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            { ResizeMembers(p, count); return; }   // a team's size: 'members = 3'
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                var parts = SplitList(value);
                p.arraySize = parts.Count;
                for (int i = 0; i < parts.Count; i++) Assign(p.GetArrayElementAtIndex(i), parts[i]);
                return;
            }
            var inv = CultureInfo.InvariantCulture;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (!long.TryParse(value.Trim(), NumberStyles.Integer, inv, out long l)) throw Bad(p, value, "a whole number", "4");
                    p.longValue = l;
                    break;
                case SerializedPropertyType.Float:
                    if (!double.TryParse(value.Trim(), NumberStyles.Float, inv, out double d)) throw Bad(p, value, "a number with '.' as the decimal point", "2.5");
                    p.doubleValue = d;
                    break;
                case SerializedPropertyType.Boolean:
                    if (!bool.TryParse(value.Trim(), out bool b)) throw Bad(p, value, "true or false", "true");
                    p.boolValue = b;
                    break;
                case SerializedPropertyType.String: p.stringValue = value; break;
                case SerializedPropertyType.Enum:
                    int index = Array.FindIndex(p.enumNames, n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
                    if (index < 0 && !int.TryParse(value, out index))
                        throw new ArgumentException($"'{Friendly(p.propertyPath)}': '{value}' is not one of {string.Join(", ", p.enumNames)}." +
                                                    (ListModeHint(p.propertyPath, value) ?? NameSuggestions.DidYouMean(value, p.enumNames)));
                    p.enumValueIndex = index;
                    break;
                case SerializedPropertyType.ObjectReference:
                    p.objectReferenceValue = string.IsNullOrEmpty(value) || value == "null" ? null
                                           : IsAssetPath(value) ? AssetReferenceFor(p, value.Trim()) : ReferenceFor(p, FindObject(value));
                    break;
                case SerializedPropertyType.Vector3:
                    var v = Floats(p, value, 3, "x,y,z", "0,1.5,0");
                    p.vector3Value = new Vector3(v[0], v[1], v[2]);
                    break;
                case SerializedPropertyType.Vector2:
                    var v2 = Floats(p, value, 2, "x,y", "340,210");
                    p.vector2Value = new Vector2(v2[0], v2[1]);
                    break;
                case SerializedPropertyType.Color:
                    if (!ColorUtility.TryParseHtmlString(value, out Color col)) throw new ArgumentException($"'{Friendly(p.propertyPath)}': a colour is '#RRGGBB'.");
                    p.colorValue = col;
                    break;
                case SerializedPropertyType.ManagedReference:
                    p.managedReferenceValue = ManagedValue(p, value.Trim());
                    break;
                default:
                    throw new ArgumentException($"'{Friendly(p.propertyPath)}' ({p.type}) is not set by text: set its fields (e.g. '{Friendly(p.propertyPath)}.<field>').");
            }
        }

        /// <summary>A team's member list set by a number: the first <paramref name="count"/> members are kept; a new one copies
        /// the last member's properties and load / unload times (not its spawn point) and continues its numbered name
        /// (<c>Operator 2</c> → <c>Operator 3</c>; else <c>&lt;Team&gt;_&lt;n&gt;</c>), so a study's <c>team:T.members</c> and the
        /// scene agree on who the members are.</summary>
        private static void ResizeMembers(SerializedProperty p, int count)
        {
            if (count < 1) throw new ArgumentException($"'{Friendly(p.propertyPath)}': {count} — a team has at least 1 member.");
            int before = p.arraySize;
            string last = before > 0 ? p.GetArrayElementAtIndex(before - 1).FindPropertyRelative("name").stringValue : "";
            var numbered = Regex.Match(last ?? "", @"^(.*?)(\d+)$");
            string team = (p.serializedObject.targetObject as UnityResourceTeam)?.TeamName ?? "Member";
            p.arraySize = count;   // Unity copies the last element into each new one
            for (int i = before; i < count; i++)
            {
                var m = p.GetArrayElementAtIndex(i);
                m.FindPropertyRelative("name").stringValue = numbered.Success
                    ? numbered.Groups[1].Value + (int.Parse(numbered.Groups[2].Value, CultureInfo.InvariantCulture) + i - before + 1)
                    : $"{team}_{i + 1}";
                m.FindPropertyRelative("spawnPoint").objectReferenceValue = null;
                if (before == 0) { m.FindPropertyRelative("properties").arraySize = 0; m.FindPropertyRelative("loadTime").floatValue = 0; m.FindPropertyRelative("unloadTime").floatValue = 0; }
            }
        }

        /// <summary>A list query (<c>pullQuery</c>) from its text: conditions <c>field op value</c> (or an expression) joined by
        /// <c>;</c>, then <c>|</c> and the order (<c>due desc</c>, <c>prio desc, age</c>); as the headless config writes it
        /// (<see cref="SimuLean.Collections.ListQuerySpec.Decode"/>).</summary>
        private static void AssignQuery(SerializedProperty p, string value)
        {
            SimuLean.Collections.ListQuerySpec spec;
            try { spec = SimuLean.Collections.ListQuerySpec.Decode((value ?? "").Trim()); }
            catch (FormatException e) { throw new ArgumentException($"'{Friendly(p.propertyPath)}': {e.Message} Example: sku == 'A'; qty > 0 | due desc."); }
            var conditions = p.FindPropertyRelative("conditions");
            conditions.arraySize = spec.conditions.Count;
            for (int i = 0; i < spec.conditions.Count; i++)
            {
                var c = conditions.GetArrayElementAtIndex(i);
                c.FindPropertyRelative("field").stringValue = spec.conditions[i].field;
                c.FindPropertyRelative("op").enumValueIndex = Array.IndexOf(Enum.GetValues(typeof(SimuLean.Collections.ListOp)), spec.conditions[i].op);
                c.FindPropertyRelative("value").stringValue = spec.conditions[i].value;
            }
            p.FindPropertyRelative("orderBy").stringValue = spec.orderBy ?? "";
            p.FindPropertyRelative("descending").boolValue = spec.descending;
        }

        /// <summary>
        /// The items of a list value: <c>[a, b]</c> or <c>a, b</c>, split only at the commas outside parentheses, brackets,
        /// braces and quotes (an exit label <c>pick(0.67, 'A', 0.33, 'B')</c> was cut into four broken items).
        /// One pair of brackets around the whole value is dropped; empty items are skipped; each item is trimmed.
        /// </summary>
        internal static List<string> SplitList(string value)
        {
            string s = (value ?? "").Trim();
            if (s.Length >= 2 && s[0] == '[' && s[s.Length - 1] == ']' && ClosingOf(s, 0) == s.Length - 1)
                s = s.Substring(1, s.Length - 2);
            var parts = new List<string>();
            int depth = 0, start = 0;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') quote = c;
                else if (c == '(' || c == '[' || c == '{') depth++;
                else if ((c == ')' || c == ']' || c == '}') && depth > 0) depth--;
                else if (c == ',' && depth == 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            parts.Add(s.Substring(start));
            return parts.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        // the index of the bracket that closes the one at 'open' (quotes respected), -1 when none
        private static int ClosingOf(string s, int open)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') quote = c;
                else if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') { if (--depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>
        /// A <c>[SerializeReference]</c> field set by type (the Optimizer's <c>algorithm</c>, which otherwise needs
        /// <c>eval</c>): a new instance of the concrete type named — its class name with or without its base type's
        /// suffix (<c>GeneticAlgorithm</c>, <c>RandomSearch</c>), or the start of it; <c>null</c> clears it. Its fields then
        /// take their defaults and are set by path (<c>algorithm.generations</c>).
        /// </summary>
        private static object ManagedValue(SerializedProperty p, string value)
        {
            if (value.Length == 0 || value == "null") return null;
            string[] declared = p.managedReferenceFieldTypename.Split(' ');   // "Assembly Namespace.Type"
            Type baseType = declared.Length == 2 ? Type.GetType(declared[1] + ", " + declared[0]) : null;
            if (baseType == null) throw new ArgumentException($"'{Friendly(p.propertyPath)}': its declared type '{p.managedReferenceFieldTypename}' is not found.");
            var types = TypeCache.GetTypesDerivedFrom(baseType).Append(baseType)
                                 .Where(t => !t.IsAbstract && !t.IsGenericType && t.GetConstructor(Type.EmptyTypes) != null).Distinct().ToList();
            string Short(Type t) => t.Name.EndsWith(baseType.Name) && t.Name.Length > baseType.Name.Length
                ? t.Name.Substring(0, t.Name.Length - baseType.Name.Length) : t.Name;
            string n = value.Replace(" ", "");
            var match = types.Where(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(Short(t), n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0) match = types.Where(t => t.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count != 1)
                throw new ArgumentException($"'{Friendly(p.propertyPath)}': '{value}' is {(match.Count == 0 ? "not" : "more than")} one of " +
                                            string.Join(", ", types.Select(Short)) + "." + NameSuggestions.DidYouMean(value, types.Select(Short)));
            return Activator.CreateInstance(match[0]);
        }

        /// <summary>A parse error that names the field, the value given, what was expected and an example (.NET's
        /// "Input string was not in a correct format" named none of them).</summary>
        private static ArgumentException Bad(SerializedProperty p, string value, string expected, string example)
            => new ArgumentException($"'{Friendly(p.propertyPath)}' ({TypeOf(p)}): '{value}' is not {expected} (e.g. '{example}').");

        private static float[] Floats(SerializedProperty p, string value, int n, string form, string example)
        {
            var parts = value.Split(',');
            var v = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) throw Bad(p, value, $"'{form}' (numbers with '.')", example);
            if (v.Length != n) throw Bad(p, value, $"'{form}' ({n} numbers)", example);
            return v;
        }

        /// <summary>The object of <paramref name="go"/> an object field takes (the GameObject, its transform or a component of
        /// the field's type).</summary>
        private static Object ReferenceFor(SerializedProperty p, GameObject go)
        {
            string type = ObjectTypeName(p);
            if (type == "GameObject") return go;
            if (type == "Transform") return go.transform;
            var c = go.GetComponents<Component>().FirstOrDefault(x => x != null && TypeChain(x.GetType()).Any(t => t.Name == type));
            return c != null ? c : throw new ArgumentException($"'{go.name}' has no {type} for '{Friendly(p.propertyPath)}'.");
        }

        // a two-way travel-path track between two nodes, in the network that holds them (else the scene's)
        private static object ConnectNodes(UnityTransportNode a, UnityTransportNode b)
        {
            var net = a.GetComponentInParent<UnityTransportNetwork>() ?? b.GetComponentInParent<UnityTransportNetwork>()
                      ?? UnityCompat.FindAny<UnityTransportNetwork>()
                      ?? throw new ArgumentException("No Transport Network in the scene: place one (simulean_add_element 'Transport Network') and the nodes under it.");
            Undo.RegisterCompleteObjectUndo(net, "Connect " + a.name);
            if (!net.edges.Any(e => (e.nodeA == a && e.nodeB == b) || (e.nodeA == b && e.nodeB == a)))
                net.edges.Add(new UnityTransportNetwork.EdgeDefinition { nodeA = a, nodeB = b, bidirectional = true });
            EditorUtility.SetDirty(net);
            EditorSceneManager.MarkSceneDirty(net.gameObject.scene);
            return new { network = net.name, track = a.name + " <-> " + b.name, length = Vector3.Distance(a.transform.position, b.transform.position) };
        }

        private static bool IsAssetPath(string value)
        {
            string v = value.Trim();
            return (v.StartsWith("Assets/", StringComparison.Ordinal) || v.StartsWith("Packages/", StringComparison.Ordinal))
                   && System.IO.Path.HasExtension(v);
        }

        // an asset for an object field (a member prefab, an output strategy asset…): the asset itself, or the prefab's
        // component of the field's type
        private static Object AssetReferenceFor(SerializedProperty p, string path)
        {
            string type = ObjectTypeName(p);
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (assets == null || assets.Length == 0) throw new ArgumentException($"No asset '{path}'.");
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main is GameObject prefab && type != "GameObject")
                return type == "Transform" ? prefab.transform : ReferenceFor(p, prefab);
            var match = new[] { main }.Concat(assets).FirstOrDefault(a => a != null && TypeChain(a.GetType()).Any(t => t.Name == type));
            return match != null ? match : throw new ArgumentException($"'{path}' is not a {type} for '{Friendly(p.propertyPath)}'.");
        }

        private static IEnumerable<Type> TypeChain(Type t)
        {
            for (; t != null; t = t.BaseType) yield return t;
        }

        private static double? Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
    }
}
