using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        [CliCommand("simulean_types", "List the SimuLean element types and tools that can be placed (the Library's entries); with " +
                                      "'type', that type's fields (simulean_set) and catalog keys (simulean_run_headless --set).", Tags = new[] { Tag })]
        public static object Types([CliArg("type", "A Library name or component type (e.g. 'Queue', 'UnityQueue'); empty = all.")] string type = "")
            => AgentReply.Of(() => TypesOf(type));

        private static object TypesOf(string type)
        {
            var entries = SimuLeanWindow.CollectEntries();
            if (string.IsNullOrEmpty(type))
                return new { types = entries.Select(e => new
                {
                    name = e.Name, group = e.Group, module = e.Subgroup, pack = e.Pack, component = e.Type?.Name, description = e.Description,
                }).Concat(OtherTools(entries).Select(t => new
                {
                    name = t.info.DisplayName, group = t.info.Category, module = (string)null, pack = string.IsNullOrEmpty(t.info.Pack) ? SimuLeanPacks.Core : t.info.Pack, component = t.type.Name, description = t.info.Description,
                })).ToList() };
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
                return new { name = entry.Name, component = entry.Type.Name, description = entry.Description, fields = Fields(c), catalog = CatalogOf(holder, c) };
            }
            finally
            {
                Debug.unityLogger.logEnabled = log;
                Object.DestroyImmediate(holder);
            }
        }

        [CliCommand("simulean_params", "An element's (or tool's) fields with their values — the keys simulean_set writes — and its " +
                                       "catalog keys (the parameters a study varies).", Tags = new[] { Tag })]
        public static object Params([CliArg("element", "GameObject name or hierarchy path.", Required = true)] string element)
            => AgentReply.Of(() => ParamsOf(element));

        private static object ParamsOf(string element)
        {
            var go = FindObject(element);
            var c = SimuLeanComponent(go);
            return new { element = go.name, path = PathOf(go.transform), component = c.GetType().Name, fields = Fields(c), catalog = CatalogOf(null, c) };
        }

        // ── building ─────────────────────────────────────────────────────────

        [CliCommand("simulean_add_element", "Place a Library entry (element, tool or preset) in the open scene, as the SimuLean " +
                                            "window does. Undoable.", Tags = new[] { Tag })]
        public static object AddElement(
            [CliArg("type", "A Library name or component type (simulean_types).", Required = true)] string type,
            [CliArg("name", "GameObject name (empty = the Library's, made unique).")] string name = "",
            [CliArg("x", "World X.")] float x = 0, [CliArg("y", "World Y.")] float y = 0, [CliArg("z", "World Z.")] float z = 0,
            [CliArg("parent", "Parent GameObject (name or path); empty = its hierarchy group.")] string parent = "")
            => AgentReply.Of(() => AddElementOf(type, name, x, y, z, parent));

        private static object AddElementOf(string type, string name, float x, float y, float z, string parent)
        {
            var entries = SimuLeanWindow.CollectEntries();
            Transform under = string.IsNullOrEmpty(parent) ? null : FindObject(parent).transform;
            if (!string.IsNullOrEmpty(name)) EnsureFreeName(name);          // before anything is placed
            GameObject go;
            if (TryFindEntry(entries, type) is SimuLeanWindow.Entry entry)
                go = SimuLeanWindow.Place(entry, new Vector3(x, y, z), under)
                     ?? throw new InvalidOperationException($"'{entry.Name}' could not be placed.");
            else
                go = PlaceTool(FindTool(type, entries), new Vector3(x, y, z), under);
            // the Library makes a name unique among its siblings only: an element in another group may carry it already
            string wanted = string.IsNullOrEmpty(name) ? (IsTaken(go.name, go) ? FreeName(go.name, go) : null) : name;
            if (wanted != null && wanted != go.name)
            {
                Undo.RecordObject(go, "Rename " + wanted);
                go.name = wanted;
            }
            EditorSceneManager.MarkSceneDirty(go.scene);
            return Describe(go);
        }

        [CliCommand("simulean_place_model", "Place a 3D model of the library (decoration: furniture, beds, walls…) in the open scene: " +
                                            "a model folder name (e.g. HospitalBed), a file under it (WaitingChairs/Seat_Table) or an asset " +
                                            "path (.fbx / .prefab). Empty model = the list of library models. Undoable.", Tags = new[] { Tag })]
        public static object PlaceModel(
            [CliArg("model", "Library model (folder, folder/file) or asset path; empty = list them.")] string model = "",
            [CliArg("name", "GameObject name (empty = the model's).")] string name = "",
            [CliArg("x", "World X.")] float x = 0, [CliArg("y", "World Y.")] float y = 0, [CliArg("z", "World Z.")] float z = 0,
            [CliArg("rotY", "Rotation about the vertical axis (degrees).")] float rotY = 0,
            [CliArg("scale", "Uniform scale.")] float scale = 1,
            [CliArg("parent", "Parent GameObject (name or path); empty = a root 'Decor' group.")] string parent = "")
            => AgentReply.Of(() => PlaceModelOf(model, name, x, y, z, rotY, scale, parent));

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
            Undo.RecordObject(a, "Connect " + a.name);
            if (a.nextElements == null) a.nextElements = new List<SElement>();
            if (a.nextElements.Contains(b)) warnings.Add($"'{a.name}' already sends to '{b.name}': nothing changed.");
            else a.nextElements.Add(b);
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
        /// Simulean_connect refused what Next Elements allows).</summary>
        internal static bool TakesConnections(SElement b) => b.TakesConnections;

        /// <summary>A key with list indices (<c>actions[1].kind</c>, a reaction's actions) as a serialized path
        /// (<c>actions.Array.data[1].kind</c>); each list grows to hold the index. Null when a list is not there.</summary>
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
                if (list.arraySize <= i) list.arraySize = i + 1;
                sb.Append(".Array.data[").Append(i).Append(']');
                at = x.Index + x.Length;
            }
            sb.Append(key, at, key.Length - at);
            return sb.ToString();
        }

        [CliCommand("simulean_set", "Set a field of an element or tool (simulean_params lists them): a name or path such as " +
                                    "'capacity', 'delayStrategy.constantValue', 'processBy.team' (object fields take a GameObject " +
                                    "name, or an asset path such as 'Assets/…/Doctor.prefab'), 'mode' (enums by name), lists comma-separated; a list item's " +
                                    "field as 'actions[0].kind' (an index past the end adds items); an element's user property as 'properties.NAME' " +
                                    "(created when missing; a number, or else a text). Undoable.", Tags = new[] { Tag })]
        public static object Set(
            [CliArg("element", "GameObject name or hierarchy path.", Required = true)] string element,
            [CliArg("key", "Field name or path.", Required = true)] string key,
            [CliArg("value", "The value as text.", Required = true)] string value)
            => AgentReply.Of(() => SetOf(element, key, value));

        private static object SetOf(string element, string key, string value)
        {
            var go = FindObject(element);
            if (key.StartsWith(PropertyRecords.Key + ".", StringComparison.Ordinal)) return SetProperty(go, key.Substring(PropertyRecords.Key.Length + 1), value);
            foreach (var c in go.GetComponents<MonoBehaviour>().OrderBy(c => IsSimuLean(c) ? 0 : 1))
            {
                if (c == null) continue;
                var so = new SerializedObject(c);
                string path = ItemPath(so, key);
                var p = path != null ? so.FindProperty(path) : null;
                if (p == null) continue;
                Assign(p, value);
                so.ApplyModifiedProperties();      // registers the undo
                EditorSceneManager.MarkSceneDirty(go.scene);
                var warnings = new List<string> { InactiveDistributionWarning(new SerializedObject(c), key) };
                if (c is SElement se) warnings.AddRange(FlowWarnings(se, key));
                return new { element = go.name, component = c.GetType().Name, key, value = ValueOf(so.FindProperty(path)),
                             warnings = warnings.Where(w => w != null).ToList() };
            }
            var known = Fields(SimuLeanComponent(go)).Select(f => f.path).ToList();
            throw new ArgumentException($"'{go.name}' has no field '{key}'." + NameSuggestions.DidYouMean(key, known) +
                                        $" Fields: {string.Join(", ", known)}.");
        }

        // 'properties.NAME' (the key a study varies): the element's user property (SElementLabels, added when missing), a
        // number or else a text; created when the element has none of that name (CASE2 v2 run: the agent had to add the
        // component and write its list through the generic tools, and a key went in with its quotes)
        private static object SetProperty(GameObject go, string name, string value)
        {
            name = (name ?? "").Trim().Trim('"', '\'').Trim();
            if (name.Length == 0 || name.Contains("|")) throw new ArgumentException($"'properties.{name}': give a property name without '|' (e.g. properties.beds).");
            if (go.GetComponent<SElement>() == null) throw new ArgumentException($"'{go.name}' is not a model element: only elements have properties.");
            var labels = go.GetComponent<SElementLabels>();
            bool added = labels == null;
            if (added) labels = Undo.AddComponent<SElementLabels>(go);
            var so = new SerializedObject(labels);
            var list = so.FindProperty("labelPairs");
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
            so.ApplyModifiedProperties();      // registers the undo
            labels.RebuildLabelsDictionary();  // the lookup the component keeps (after a list change, as its inspector does)
            EditorSceneManager.MarkSceneDirty(go.scene);
            var warnings = new List<string>();
            if (!number) warnings.Add($"'{v}' is not a number: the property is a text (a study can vary only number properties).");
            return new { element = go.name, component = nameof(SElementLabels), key = PropertyRecords.Key + "." + name,
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

        [CliCommand("simulean_add_team", "Place a resource team (operators, cranes, vehicles…) with n members. Assign it with " +
                                         "simulean_set <station> processBy.team <team> (or transportBy.team). Undoable.", Tags = new[] { Tag })]
        public static object AddTeam(
            [CliArg("name", "Team name.", Required = true)] string name,
            [CliArg("members", "Number of members.")] int members = 1,
            [CliArg("travel", "Travel mode (TeamTravel: Abstract, Network, Matrix, Axes…); empty = the default.")] string travel = "",
            [CliArg("x", "World X.")] float x = 0, [CliArg("y", "World Y.")] float y = 0, [CliArg("z", "World Z.")] float z = 0,
            [CliArg("parent", "Parent GameObject (name or path); empty = its hierarchy group.")] string parent = "")
            => AgentReply.Of(() => AddTeamOf(name, members, travel, x, y, z, parent));

        private static object AddTeamOf(string name, int members, string travel, float x, float y, float z, string parent)
        {
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
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            try
            {
                var go = SimuLeanWindow.Place(entry, new Vector3(x, y, z), under)
                         ?? throw new InvalidOperationException("The resource team could not be placed.");
                var team = go.GetComponent<UnityResourceTeam>();
                Undo.RecordObject(go, "Add team " + name);
                Undo.RecordObject(team, "Add team " + name);
                go.name = name;
                // the members are named after the team's final name (the first was 'Resource Team_1')
                team.members.Clear();
                team.AddMembers(members);
                if (mode.HasValue) team.travel = mode.Value;
                EditorUtility.SetDirty(team);
                EditorSceneManager.MarkSceneDirty(go.scene);
                return new { team = go.name, path = PathOf(go.transform), members = team.members.Select(m => m.name).ToList(), travel = team.travel.ToString() };
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
            finally { Undo.CollapseUndoOperations(group); }
        }

        [CliCommand("simulean_load_table", "Give a Schedule Source its arrivals from a table file (CSV, .data or Excel): a Data Table " +
                                           "named after the source reads the file (created or reused) and is loaded; with readAtRun " +
                                           "the runs read the file as it is when they start.", Tags = new[] { Tag })]
        public static object LoadTable(
            [CliArg("source", "The Source (name or path).", Required = true)] string source,
            [CliArg("file", "Table file: absolute, or relative to the project or to Assets/.", Required = true)] string file,
            [CliArg("sheet", "Excel sheet (empty = the first).")] string sheet = "",
            [CliArg("readAtRun", "Read the file at each run start (else the loaded copy).")] bool readAtRun = false)
            => AgentReply.Of(() => LoadTableOf(source, file, sheet, readAtRun));

        private static object LoadTableOf(string source, string file, string sheet, bool readAtRun)
        {
            var src = FindObject(source).GetComponent<UnitySource>() ?? throw new ArgumentException($"'{source}' is not a Source.");
            // one undo group, reverted when the table cannot be loaded: nothing is left half done
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            try { return LoadTableInto(src, file, sheet, readAtRun); }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
            finally { Undo.CollapseUndoOperations(group); }
        }

        private static object LoadTableInto(UnitySource src, string file, string sheet, bool readAtRun)
        {
            Undo.RecordObject(src, "Load table " + src.name);
            src.mode = SourceMode.Schedule;
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
            Undo.RecordObject(table, "Load table " + src.name);
            table.source = SimuLean.Serialization.DataSourceKind.File;
            table.fileName = file;
            table.sheetName = sheet ?? "";
            table.readFileAtRun = readAtRun;
            if (table.ResolveFile() == null) throw new ArgumentException($"Table file '{file}' not found.");
            table.Load(ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot));
            var rows = table.Rows;
            if (rows.Count == 0) throw new InvalidOperationException($"'{file}' gave no table.");
            EditorUtility.SetDirty(table);
            EditorUtility.SetDirty(src);
            EditorSceneManager.MarkSceneDirty(src.gameObject.scene);
            return new { source = src.name, table = table.TableName, rows = rows.Values.First().Count, columns = rows.Keys.ToList() };
        }

        // ── reading and checking the whole model ────────────────

        [CliCommand("simulean_model", "The scene's model in one compact answer, as a run builds it: the clock (time unit, horizon, " +
                                      "warm-up, end condition, seed), every element (type, next elements in order, parameters under " +
                                      "the keys a study varies), the other connections, teams (members, labels, parameters), lists, " +
                                      "downtimes, reactions and data tables — all by name. Read-only.", Tags = new[] { Tag })]
        public static object Model(
            [CliArg("element", "Only this element / team / list / downtime / reaction / table (empty = the whole model).")] string element = "",
            [CliArg("parameters", "Include the parameters (false = the structure only: smaller).")] bool parameters = true,
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "")
            => AgentReply.Of(() => SimuLean.Optimization.ModelDescription.Of(
                   SceneStudy.Of(string.IsNullOrEmpty(root) ? null : FindObject(root)).Model, parameters,
                   string.IsNullOrWhiteSpace(element) ? null : element.Trim()));

        [CliCommand("simulean_validate", "Check the scene's model without running it: extract it, build it (each scenario of the " +
                                         "optional study: set / metrics as simulean_run_headless) and resolve the metrics. Answers ok " +
                                         "plus the errors and warnings a run would meet (extractor, build, study), with \"did you mean\".",
                     Tags = new[] { Tag })]
        public static object Validate(
            [CliArg("set", "Catalog overrides 'path=v1|v2;…' or JSON to check too (as simulean_run_headless).")] string set = "",
            [CliArg("metrics", "Named metrics 'name=path;…' or JSON to check too.")] string metrics = "",
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "",
            [CliArg("scenarios", "Explicit scenarios instead of set (JSON): [{\"name\": \"A\", \"Buf.capacity\": 2, \"replications\": 5}, ...] - every scenario gives every factor.")] string scenarios = "",
            [CliArg("where", "Keep only the combinations of set's factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _).")] string where = "",
            [CliArg("derived", "Metrics computed per replication from the others, 'name=expression;...' (e.g. 'days=floor(span/24)'); reported as mean +- half-width like them.")] string derived = "",
            [CliArg("confidence", "Confidence level of the half-widths (0 = 0.95).")] double confidence = 0,
            [CliArg("warmup", "Warm-up time: statistics start after it (-1 = the model's).")] double warmup = -1,
            [CliArg("experimenter", "Run a scene Experimenter's study (its factors, scenarios, metrics and settings); maxTime, replications other than 1, seed other than 1 and the metrics given here override / are added.")] string experimenter = "")
        {
            var answer = AgentReply.With(AgentReply.Of(() =>
            {
                var study = MakeStudy(0, 1, 1, set, metrics, root, Options(scenarios, where, derived, confidence, warmup), experimenter);
                var check = study.Check();
                var m = study.Model;
                return new
                {
                    elements = m.Elements.Count, connections = m.Connections.Count, teams = m.ResourceTeams.Count,
                    scenarios = check.Ok ? study.Plan().Count : (int?)null, metrics = study.Metrics.Count,
                    errors = check.Errors, warnings = check.Warnings,
                };
            }), null, null);
            answer["ok"] = ((Newtonsoft.Json.Linq.JArray)answer["errors"]).Count == 0;
            return answer;
        }

        // ── running ──────────────────────────────────────────────────────────

        [CliCommand("simulean_run_headless", "Run the scene's model headless (the scene is not changed) and return per scenario " +
                                             "the named metrics and the model block (makespan, end reason, deadlock…); with " +
                                             "detail=full (the default without metrics) also the elements' (input, output, " +
                                             "content, stay, utilisation) and resources' KPIs.", Tags = new[] { Tag })]
        public static object RunHeadless(
            [CliArg("maxTime", "Horizon in model time units (0 = the clock's).")] double maxTime = 0,
            [CliArg("replications", "Replications (seeds seed, seed+1, …).")] int replications = 1,
            [CliArg("seed", "First seed.")] int seed = 1,
            [CliArg("set", "Catalog overrides 'path=value;…' (e.g. 'Buffer.capacity=4;team:Crew.members[skill=weld]=3'); " +
                           "a value list 'path=1|2|4' makes one scenario per level. Or JSON: {\"Buffer.capacity\": [2, 4], " +
                           "\"Src.tableSource\": \"sql: SELECT a; b\"} (values may then hold ; = |).")] string set = "",
            [CliArg("metrics", "Named metrics 'name=path;…' (e.g. 'span=model.makespan;out=Sink.input'), or JSON " +
                               "{\"span\": \"model.makespan\", \"out\": \"Sink.input\"}.")] string metrics = "",
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "",
            [CliArg("detail", "'metrics' = only the named metrics and the model block per scenario (the default when metrics " +
                              "are given: small enough for an agent's context); 'full' = also every element's and resource's KPIs.")] string detail = "",
            [CliArg("scenarios", "Explicit scenarios instead of set (JSON): [{\"name\": \"A\", \"Buf.capacity\": 2, \"replications\": 5}, ...] - every scenario gives every factor.")] string scenarios = "",
            [CliArg("where", "Keep only the combinations of set's factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _).")] string where = "",
            [CliArg("derived", "Metrics computed per replication from the others, 'name=expression;...' (e.g. 'days=floor(span/24)'); reported as mean +- half-width like them.")] string derived = "",
            [CliArg("confidence", "Confidence level of the half-widths (0 = 0.95).")] double confidence = 0,
            [CliArg("warmup", "Warm-up time: statistics start after it (-1 = the model's).")] double warmup = -1,
            [CliArg("experimenter", "Run a scene Experimenter's study (its factors, scenarios, metrics and settings); maxTime, replications other than 1, seed other than 1 and the metrics given here override / are added.")] string experimenter = "")
            => AgentReply.Of(() =>
            {
                var options = Options(scenarios, where, derived, confidence, warmup);
                var study = BuildStudy(maxTime, replications, seed, set, metrics, root, detail, out bool full, out var warnings, options, experimenter);
                var settings = Settings(study);
                var results = study.Run();
                options.Finish(results);
                return Answer(Kept(results, ResultsOutput.ProjectRoot, UnityEngine.SceneManagement.SceneManager.GetActiveScene().name), full, warnings, settings);
            });

        private static StudyOptions Options(string scenarios, string where, string derived, double confidence, double warmup)
            => new StudyOptions { Scenarios = scenarios ?? "", Where = where ?? "", Derived = derived ?? "", Confidence = confidence, Warmup = warmup };

        // ── long runs in the background ──────────────────────

        private sealed class BackgroundRun
        {
            public System.Threading.Tasks.Task<object> Task;
            public System.Threading.CancellationTokenSource Cancel;
            public int Done, Total;
            public DateTime Started;
            /// <summary>What is logged while the runs go on (worker threads included), for the completed answer.</summary>
            public AgentReply.LogCapture Log;
        }
        private static BackgroundRun background;

        [CliCommand("simulean_run_start", "Start simulean_run_headless in the background (same arguments) and return at once: a long " +
                                          "study no longer blocks the editor or times the MCP out. Poll simulean_run_status; one run " +
                                          "at a time.", Tags = new[] { Tag })]
        public static object RunStart(
            [CliArg("maxTime", "Horizon in model time units (0 = the clock's).")] double maxTime = 0,
            [CliArg("replications", "Replications (seeds seed, seed+1, …).")] int replications = 1,
            [CliArg("seed", "First seed.")] int seed = 1,
            [CliArg("set", "Catalog overrides 'path=v1|v2;…' or JSON, as simulean_run_headless.")] string set = "",
            [CliArg("metrics", "Named metrics 'name=path;…' or JSON, as simulean_run_headless.")] string metrics = "",
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "",
            [CliArg("detail", "'metrics' or 'full', as simulean_run_headless.")] string detail = "",
            [CliArg("scenarios", "Explicit scenarios instead of set (JSON): [{\"name\": \"A\", \"Buf.capacity\": 2, \"replications\": 5}, ...] - every scenario gives every factor.")] string scenarios = "",
            [CliArg("where", "Keep only the combinations of set's factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _).")] string where = "",
            [CliArg("derived", "Metrics computed per replication from the others, 'name=expression;...' (e.g. 'days=floor(span/24)'); reported as mean +- half-width like them.")] string derived = "",
            [CliArg("confidence", "Confidence level of the half-widths (0 = 0.95).")] double confidence = 0,
            [CliArg("warmup", "Warm-up time: statistics start after it (-1 = the model's).")] double warmup = -1,
            [CliArg("experimenter", "Run a scene Experimenter's study (its factors, scenarios, metrics and settings); maxTime, replications other than 1, seed other than 1 and the metrics given here override / are added.")] string experimenter = "")
            => AgentReply.Of(() => RunStartOf(maxTime, replications, seed, set, metrics, root, detail,
                                              Options(scenarios, where, derived, confidence, warmup), experimenter));

        private static object RunStartOf(double maxTime, int replications, int seed, string set, string metrics, string root, string detail,
                                         StudyOptions options, string experimenter)
        {
            if (background != null && !background.Task.IsCompleted)
                throw new InvalidOperationException("A background run is going on: poll simulean_run_status (or cancel it there).");
            // the scene is read here, on the main thread; the runs go on a worker (the engine never touches Unity)
            var study = BuildStudy(maxTime, replications, seed, set, metrics, root, detail, out bool full, out var warnings, options, experimenter);
            var settings = Settings(study);
            study.Threads = 0;                                   // all cores: the editor's main thread is free anyway
            var run = new BackgroundRun { Cancel = new System.Threading.CancellationTokenSource(), Started = DateTime.Now, Log = new AgentReply.LogCapture() };
            string projectRoot = ResultsOutput.ProjectRoot, scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            run.Task = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var results = study.Run(run.Cancel.Token, (d, t) => { run.Done = d; run.Total = t; });
                    options.Finish(results);
                    var answer = Answer(Kept(results, projectRoot, scene), full, warnings, settings);
                    return (object)AgentReply.With(answer, run.Log.Errors, run.Log.Warnings);
                }
                finally { run.Log.Dispose(); }
            });
            background = run;
            return new { status = "running", runs = study.Plan().Count * Math.Max(1, replications), settings, warnings };
        }

        [CliCommand("simulean_run_status", "The background run of simulean_run_start: 'running' (runs done / in all), then 'completed' " +
                                           "with the same answer as simulean_run_headless, or 'failed'. cancel=true stops it; wait=s answers as " +
                                           "soon as the run ends (at most 55 s).", Tags = new[] { Tag })]
        public static System.Threading.Tasks.Task<object> RunStatus(
            [CliArg("cancel", "Stop the run (the runs done so far are kept).")] bool cancel = false,
            [CliArg("wait", "Seconds to wait for the job to end (0 = answer at once; at most 55): the answer comes as soon as it ends, so poll with wait instead of sleeping.")] double wait = 0)
        {
            if (cancel) AgentReply.Of(() => RunStatusOf(true));
            return JobWait.Until(() => JobWait.Json(AgentReply.Of(() => RunStatusOf(false))), wait);
        }

        private static object RunStatusOf(bool cancel)
        {
            var run = background;
            if (run == null) return new { status = "none" };
            if (cancel && !run.Task.IsCompleted) run.Cancel.Cancel();
            if (!run.Task.IsCompleted)
                return new { status = "running", done = run.Done, total = run.Total, seconds = Math.Round((DateTime.Now - run.Started).TotalSeconds, 1) };
            if (run.Task.IsFaulted)
                return new { status = "failed", error = run.Task.Exception?.GetBaseException().Message,
                             errors = new[] { run.Task.Exception?.GetBaseException().Message }.Concat(run.Log.Errors).ToList(), warnings = run.Log.Warnings };
            return new { status = "completed", seconds = Math.Round((DateTime.Now - run.Started).TotalSeconds, 1), result = run.Task.Result };
        }

        /// <summary>The study of the arguments, checked on the built model before any run
        /// (<see cref="Study.Check"/>): an error refuses the run (all its errors in the message); <paramref name="warnings"/> go into the answer.</summary>
        private static SimuLean.Optimization.Study BuildStudy(double maxTime, int replications, int seed, string set, string metrics,
                                                              string root, string detail, out bool full, out List<string> warnings,
                                                              StudyOptions options = null, string experimenter = "")
        {
            full = string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase)
                   || (string.IsNullOrWhiteSpace(detail) && string.IsNullOrWhiteSpace(metrics));
            if (!full && !string.IsNullOrWhiteSpace(detail) && !string.Equals(detail, "metrics", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"detail: '{detail}' is not 'metrics' or 'full'.");
            var study = MakeStudy(maxTime, replications, seed, set, metrics, root, options, experimenter);
            var check = study.Check();
            if (!check.Ok)
                throw new ArgumentException("The study cannot run: " + string.Join(" | ", check.Errors) +
                                            (check.Warnings.Count > 0 ? " Warnings: " + string.Join(" | ", check.Warnings) : ""));
            warnings = check.Warnings;
            return study;
        }

        /// <summary>The study of the arguments on the scene's model (extracted once), not checked.</summary>
        private static SimuLean.Optimization.Study MakeStudy(double maxTime, int replications, int seed, string set, string metrics, string root,
                                                             StudyOptions options = null, string experimenter = "")
        {
            options = options ?? new StudyOptions();
            var factors = FactorsOf(set);
            SimuLean.Optimization.Study study;
            if (!string.IsNullOrWhiteSpace(experimenter))
            {
                // A designed experiment saved in the scene runs in one call
                var component = FindObject(experimenter).GetComponent<SimuLean.Experimentation.Controller.Experimenter>()
                                ?? throw new ArgumentException($"'{experimenter}' has no Experimenter component.");
                if (factors.Count > 0 || !string.IsNullOrWhiteSpace(options.Scenarios))
                    throw new ArgumentException("experimenter: its factors and scenarios are the study's; set / scenarios are not added to them.");
                study = component.PrepareStudy();
                if (maxTime > 0) study.WithRunTime(maxTime);
                if (replications > 1) study.WithReplications(replications);
                if (seed != 1) study.WithSeed(seed);
            }
            else
            {
                study = SceneStudy.Of(string.IsNullOrEmpty(root) ? null : FindObject(root));
                if (maxTime > 0) study.WithRunTime(maxTime);
                study.WithReplications(Math.Max(1, replications)).WithSeed(seed);
                foreach (var (path, levels) in factors)
                    study.Factor(path, levels);
            }
            study.Threads = 1;                                  // deterministic, and the editor stays responsive enough
            options.AddMetrics(study, MetricsOf(metrics));
            options.Apply(study, factors.Count > 0);
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

        /// <summary>
        /// The effective settings of a study's runs: the model's time unit, the horizon actually run (the
        /// argument, else the clock's), the warm-up, the end condition, the seeds and replications — so a default is never
        /// silent. Read on the main thread, before the runs.
        /// </summary>
        internal static Dictionary<string, object> Settings(SimuLean.Optimization.Study study)
        {
            var m = study.Model;
            string end = null;
            if (m.StopAt != null && m.StopAt.Count > 0 && m.StopAtCount > 0)
                end = $"{m.StopAtCount} items received by {string.Join(", ", m.StopAt)}";
            if (m.StopWhenEmpty) end = (end != null ? end + "; " : "") + "nothing left to do (or a deadlock / stall)";
            return new Dictionary<string, object>
            {
                ["timeUnit"] = ResultMetrics.TimeUnitName(m.TimeUnitSeconds > 0 ? m.TimeUnitSeconds : 1.0),
                ["secondsPerTimeUnit"] = m.TimeUnitSeconds > 0 ? m.TimeUnitSeconds : 1.0,
                ["horizon"] = study.EffectiveRunTime,
                ["warmUp"] = m.WarmupTime,
                ["endCondition"] = end,
                ["replications"] = study.Replications,
                ["seeds"] = study.Replications > 1 ? $"{study.BaseSeed}..{study.BaseSeed + study.Replications - 1}" : study.BaseSeed.ToString(CultureInfo.InvariantCulture),
                ["confidence"] = study.Confidence,
            };
        }

        /// <summary>
        /// The answer of a study: the effective <paramref name="settings"/>; per scenario its runs ok / failed
        /// and the first error — a failed run is never averaged in nor reported as a null metric —, the end reasons, each
        /// metric as mean ± half-width over the completed runs (<c>n</c> = the runs that gave a number), and the model
        /// block; with <paramref name="full"/> also the elements' and resources' KPIs (means). <c>errors</c> lists the
        /// scenarios with failed runs.
        /// </summary>
        internal static object Answer(SimuLean.Results.ResultSet resultSet, bool full, List<string> warnings, Dictionary<string, object> settings = null)
        {
            var errors = new List<string>();
            warnings = new List<string>(warnings ?? new List<string>());
            if (resultSet.Metadata.Any(kv => kv.Key == "cancelled"))
                warnings.Add("The study was cancelled: the scenarios hold the runs done so far.");
            var scenarios = resultSet.Scenarios.Select(sc =>
            {
                var runs = resultSet.Runs.Where(r => r.Scenario == sc.Name).ToList();
                var ok = runs.Where(r => r.Error == null).ToList();
                var failed = runs.Where(r => r.Error != null).ToList();
                if (failed.Count > 0)
                    errors.Add($"Scenario '{sc.Name}': {failed.Count} of {runs.Count} runs failed (seed {failed[0].Seed}): {failed[0].Error}");
                var results = ok.Where(r => r.Results != null).Select(r => r.Results).ToList();
                var summary = results.Count > 0 ? ResultSummary.Of(results) : null;
                var answer = new Dictionary<string, object>
                {
                    ["scenario"] = sc.Name,
                    ["parameters"] = sc.Parameters.ToDictionary(p => p.Key, p => p.Value),
                    ["runs"] = new { ok = ok.Count, failed = failed.Count },
                    ["firstError"] = failed.Count > 0 ? failed[0].Error : null,
                    ["endReasons"] = results.Select(r => r.Info.EndReason).ToList(),
                    ["metrics"] = resultSet.MetricNames.ToDictionary(m => m, m => Interval(ok.Select(r => r.Metrics.TryGetValue(m, out double v) ? v : double.NaN), resultSet.Confidence)),
                    ["model"] = summary == null ? null : Stats(summary.Of(ResultScope.Model).FirstOrDefault(), null),
                };
                // who waited for what in the first run that stalled or deadlocked: simulean_diagnose shows the whole state
                var stuck = results.FirstOrDefault(r => r.Info.Waiting.Count > 0);
                if (stuck != null)
                    answer["waiting"] = new { seed = ok.First(r => r.Results == stuck).Seed, items = stuck.Info.Waiting.Take(20).ToList() };
                if (full)
                {
                    answer["elements"] = summary?.Of(ResultScope.Element).Select(o => new { name = o.Name, type = o.Type, stats = Stats(o, ElementKpis) }).ToList();
                    answer["resources"] = summary?.Of(ResultScope.Resource).Select(o => new { name = o.Name, group = o.Group, stats = Stats(o, ResourceKpis) }).ToList();
                }
                return answer;
            }).ToList();
            return new { settings, scenarios, errors, warnings };
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

        private static readonly string[] ElementKpis =
            { ResultMetrics.Input, ResultMetrics.Output, ResultMetrics.ContentAvg, ResultMetrics.ContentMax, ResultMetrics.StayAvg, ResultMetrics.Utilization };
        private static readonly string[] ResourceKpis = { ResultMetrics.Utilization, ResultMetrics.Tasks };

        private static SimuLeanWindow.Entry FindEntry(List<SimuLeanWindow.Entry> entries, string type)
            => TryFindEntry(entries, type)
               ?? throw new ArgumentException($"No Library entry '{type}'. Entries: {string.Join(", ", entries.Select(e => e.Name))}.");

        private static SimuLeanWindow.Entry TryFindEntry(List<SimuLeanWindow.Entry> entries, string type)
            => entries.FirstOrDefault(e => string.Equals(e.Name, type, StringComparison.OrdinalIgnoreCase))
               ?? entries.FirstOrDefault(e => e.Type != null && string.Equals(e.Type.Name, type, StringComparison.OrdinalIgnoreCase));

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
                throw new ArgumentException($"No Library entry or tool '{type}'." +
                                            NameSuggestions.DidYouMean(type, entries.Select(e => e.Name).Concat(tools.Select(t => t.info.DisplayName))
                                                .Concat(entries.Where(e => e.Type != null).Select(e => e.Type.Name)).Concat(tools.Select(t => t.type.Name))) +
                                            $" Entries: {string.Join(", ", entries.Select(e => e.Name))}; " +
                                            $"tools: {string.Join(", ", tools.Select(t => t.info.DisplayName))}.");
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

        private static object Describe(GameObject go)
        {
            var c = go.GetComponents<MonoBehaviour>().FirstOrDefault(IsSimuLean);
            return new { name = go.name, path = PathOf(go.transform), component = c != null ? c.GetType().Name : null, position = new[] { go.transform.position.x, go.transform.position.y, go.transform.position.z } };
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
        // type: 'delay.strategyType = Uniform' keeps delay.uniformMin / uniformMax, not the ~30 others (follow-up #30).
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
        private static Dictionary<string, string> CatalogOf(GameObject holder, Component c)
        {
            var result = new Dictionary<string, string>();
            if (!(c is SElement element)) return result;
            var x = SceneExtractor.Extract(holder);
            if (!x.ElementIds.TryGetValue(element, out string id)) return result;
            var config = x.Config.Elements.Find(e => e.Id == id);
            if (config == null) return result;
            foreach (var kv in config.Parameters) result[kv.Key] = Text(kv.Value);
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
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                var items = new List<string>();
                for (int i = 0; i < Math.Min(p.arraySize, 50); i++) items.Add(ValueOf(p.GetArrayElementAtIndex(i)));
                return "[" + string.Join(", ", items) + (p.arraySize > 50 ? ", …" : "") + "]";
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

        private static string Invariant(Vector3 v)
            => $"{v.x.ToString(CultureInfo.InvariantCulture)},{v.y.ToString(CultureInfo.InvariantCulture)},{v.z.ToString(CultureInfo.InvariantCulture)}";

        private static void Assign(SerializedProperty p, string value)
        {
            value = value ?? "";
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                var parts = value.Trim().TrimStart('[').TrimEnd(']').Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
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
                        throw new ArgumentException($"'{p.propertyPath}': '{value}' is not one of {string.Join(", ", p.enumNames)}." +
                                                    NameSuggestions.DidYouMean(value, p.enumNames));
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
                    if (!ColorUtility.TryParseHtmlString(value, out Color col)) throw new ArgumentException($"'{p.propertyPath}': a colour is '#RRGGBB'.");
                    p.colorValue = col;
                    break;
                case SerializedPropertyType.ManagedReference:
                    p.managedReferenceValue = ManagedValue(p, value.Trim());
                    break;
                default:
                    throw new ArgumentException($"'{p.propertyPath}' ({p.type}) is not set by text: set its fields (e.g. '{p.propertyPath}.<field>').");
            }
        }

        /// <summary>
        /// A <c>[SerializeReference]</c> field set by type (the Optimizer's <c>algorithm</c>, which agents switched
        /// through <c>eval</c>): a new instance of the concrete type named — its class name with or without its base type's
        /// suffix (<c>GeneticAlgorithm</c>, <c>RandomSearch</c>), or the start of it; <c>null</c> clears it. Its fields then
        /// take their defaults and are set by path (<c>algorithm.generations</c>).
        /// </summary>
        private static object ManagedValue(SerializedProperty p, string value)
        {
            if (value.Length == 0 || value == "null") return null;
            string[] declared = p.managedReferenceFieldTypename.Split(' ');   // "Assembly Namespace.Type"
            Type baseType = declared.Length == 2 ? Type.GetType(declared[1] + ", " + declared[0]) : null;
            if (baseType == null) throw new ArgumentException($"'{p.propertyPath}': its declared type '{p.managedReferenceFieldTypename}' is not found.");
            var types = TypeCache.GetTypesDerivedFrom(baseType).Append(baseType)
                                 .Where(t => !t.IsAbstract && !t.IsGenericType && t.GetConstructor(Type.EmptyTypes) != null).Distinct().ToList();
            string Short(Type t) => t.Name.EndsWith(baseType.Name) && t.Name.Length > baseType.Name.Length
                ? t.Name.Substring(0, t.Name.Length - baseType.Name.Length) : t.Name;
            string n = value.Replace(" ", "");
            var match = types.Where(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(Short(t), n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0) match = types.Where(t => t.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count != 1)
                throw new ArgumentException($"'{p.propertyPath}': '{value}' is {(match.Count == 0 ? "not" : "more than")} one of " +
                                            string.Join(", ", types.Select(Short)) + "." + NameSuggestions.DidYouMean(value, types.Select(Short)));
            return Activator.CreateInstance(match[0]);
        }

        /// <summary>A parse error that names the field, the value given, what was expected and an example (.NET's
        /// "Input string was not in a correct format" named none of them).</summary>
        private static ArgumentException Bad(SerializedProperty p, string value, string expected, string example)
            => new ArgumentException($"'{p.propertyPath}' ({TypeOf(p)}): '{value}' is not {expected} (e.g. '{example}').");

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
            return c != null ? c : throw new ArgumentException($"'{go.name}' has no {type} for '{p.propertyPath}'.");
        }

        // a two-way travel-path track between two nodes, in the network that holds them (else the scene's)
        private static object ConnectNodes(UnityTransportNode a, UnityTransportNode b)
        {
            var net = a.GetComponentInParent<UnityTransportNetwork>() ?? b.GetComponentInParent<UnityTransportNetwork>()
                      ?? UnityCompat.FindAny<UnityTransportNetwork>()
                      ?? throw new ArgumentException("No Transport Network in the scene: place one (simulean_add_element 'Transport Network') and the nodes under it.");
            Undo.RecordObject(net, "Connect " + a.name);
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
            return match != null ? match : throw new ArgumentException($"'{path}' is not a {type} for '{p.propertyPath}'.");
        }

        private static IEnumerable<Type> TypeChain(Type t)
        {
            for (; t != null; t = t.BaseType) yield return t;
        }

        /// <summary>
        /// The factors of <c>set</c>: JSON — an object <c>{"path": level or [levels]}</c> or an array
        /// <c>[{"path": …, "values": […]}]</c>, levels as numbers, texts or booleans, so a value may hold <c>;</c> <c>=</c>
        /// <c>|</c> — or the shorthand <c>path=v1|v2;…</c>.
        /// </summary>
        internal static List<(string path, string[] levels)> FactorsOf(string set)
        {
            var list = new List<(string, string[])>();
            var json = Json(set, "set", "{\"Buffer.capacity\": [2, 4]}");
            if (json == null)
            {
                foreach (var (path, value) in Pairs(set, "set")) list.Add((path, value.Split('|').Select(v => v.Trim()).ToArray()));
                return list;
            }
            if (json is Newtonsoft.Json.Linq.JObject o)
                foreach (var kv in o) list.Add((kv.Key, Levels(kv.Value, kv.Key)));
            else
                foreach (var item in (Newtonsoft.Json.Linq.JArray)json)
                {
                    string path = (string)item["path"] ?? throw new ArgumentException($"set: {item.ToString(Newtonsoft.Json.Formatting.None)} has no \"path\".");
                    list.Add((path, Levels(item["values"] ?? item["value"] ?? throw new ArgumentException($"set: '{path}' has no \"values\"."), path)));
                }
            return list;
        }

        /// <summary>The metrics of <c>metrics</c>: JSON <c>{"name": "path"}</c> or <c>[{"name": …, "path": …}]</c>,
        /// or the shorthand <c>name=path;…</c>.</summary>
        internal static List<(string name, string path)> MetricsOf(string metrics)
        {
            var json = Json(metrics, "metrics", "{\"out\": \"Sink.input\"}");
            if (json == null) return Pairs(metrics, "metrics").ToList();
            if (json is Newtonsoft.Json.Linq.JObject o)
                return o.Properties().Select(p => (p.Name, p.Value.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)p.Value
                                                      : throw new ArgumentException($"metrics: '{p.Name}' must be a path text (e.g. \"Sink.input\")."))).ToList();
            return ((Newtonsoft.Json.Linq.JArray)json).Select(i => ((string)i["name"] ?? throw new ArgumentException("metrics: an item has no \"name\"."),
                                                                    (string)i["path"] ?? throw new ArgumentException("metrics: an item has no \"path\"."))).ToList();
        }

        // the JSON of an argument (an object or an array), null for the shorthand
        private static Newtonsoft.Json.Linq.JToken Json(string text, string what, string example)
        {
            string t = (text ?? "").Trim();
            if (!(t.StartsWith("{") || t.StartsWith("["))) return null;
            try
            {
                var token = Newtonsoft.Json.Linq.JToken.Parse(t);
                if (token is Newtonsoft.Json.Linq.JArray a && a.Any(x => !(x is Newtonsoft.Json.Linq.JObject)))
                    throw new ArgumentException($"{what}: a JSON array holds objects (e.g. [{{\"path\": …}}]).");
                return token;
            }
            catch (Newtonsoft.Json.JsonException e)
            {
                throw new ArgumentException($"{what}: not valid JSON ({e.Message}). Expected e.g. {example}, or the shorthand.");
            }
        }

        private static string[] Levels(Newtonsoft.Json.Linq.JToken v, string path)
        {
            var items = v is Newtonsoft.Json.Linq.JArray a ? a.ToList() : new List<Newtonsoft.Json.Linq.JToken> { v };
            if (items.Count == 0) throw new ArgumentException($"set: '{path}' has no level.");
            return items.Select(x =>
            {
                switch (x.Type)
                {
                    case Newtonsoft.Json.Linq.JTokenType.Integer:
                    case Newtonsoft.Json.Linq.JTokenType.Float: return ((double)x).ToString("R", CultureInfo.InvariantCulture);
                    case Newtonsoft.Json.Linq.JTokenType.Boolean: return (bool)x ? "true" : "false";
                    case Newtonsoft.Json.Linq.JTokenType.String: return (string)x;
                    default: throw new ArgumentException($"set: '{path}': a level is a number, a text or true / false, not {x.Type}.");
                }
            }).ToArray();
        }

        private static IEnumerable<(string, string)> Pairs(string text, string what)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (string part in text.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                int eq = part.LastIndexOf('=');
                if (eq <= 0) throw new ArgumentException($"{what}: '{part}' is not 'name=value'.");
                yield return (part.Substring(0, eq).Trim(), part.Substring(eq + 1).Trim());
            }
        }

        private static Dictionary<string, double?> Stats(SummaryObject o, string[] only)
        {
            var d = new Dictionary<string, double?>();
            if (o == null) return d;
            foreach (string m in only ?? o.Metrics.ToArray())
                if (o.TryGet(m, out StatSummary s)) d[m] = Finite(s.Mean);
            return d;
        }

        /// <summary>A metric over the replications: mean, half-width of the <paramref name="confidence"/> interval (null with
        /// fewer than two values) and n, the finite values (a time not reached is NaN and left out).</summary>
        private static object Interval(IEnumerable<double> values, double confidence)
        {
            var v = values.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
            if (v.Count == 0) return new { mean = (double?)null, halfWidth = (double?)null, n = 0 };
            var st = StatSummary.Of(v, confidence);
            return new { mean = Finite(st.Mean), halfWidth = st.HasInterval ? Finite(st.HalfWidth) : null, n = v.Count };
        }

        private static double? Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
    }
}
