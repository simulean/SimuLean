using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The SimuLean window (<i>Tools ▸ SimuLean ▸ Open SimuLean</i>, Ctrl+Shift+L): the one entry point of the
    /// modelling interface. Tabs:
    /// <list type="bullet">
    /// <item><b>Library</b> — what is placed to build a model (elements, resources, lists, transport, downtimes and the
    /// GameObject ▸ SimuLean presets), as colour-coded tiles: click to place at the view's centre, drag into the Scene
    /// view to place there.</item>
    /// <item><b>Model</b> — what the open scenes hold, by the hierarchy's groups (counts, problems, select / remove) and
    /// their connections.</item>
    /// <item><b>Data</b> — the project database: its tables, CSV / Excel import, an SQL console, the scene's
    /// data tables.</item>
    /// <item><b>Fit</b> — distribution fitting: data → ranked candidates → inserted into a distribution field.</item>
    /// <item><b>Experiments</b> — Experimenter, Optimizer and the analysis components (run / stop, status, result).</item>
    /// <item><b>Results</b> — charts of the last result set or an opened <c>.json</c> export.</item>
    /// <item><b>Settings</b> — scene set-up and hierarchy, the clock, the run-time views as on / off switches, maintenance.</item>
    /// </list>
    /// Built from the registry attributes (<see cref="SimuLeanElementAttribute"/>, <see cref="SimuLeanToolAttribute"/>)
    /// and the <c>GameObject/SimuLean/…</c> menu items, so whatever is registered appears with nothing to list here.
    /// </summary>
    public partial class SimuLeanWindow : EditorWindow
    {
        private static readonly string[] Tabs = { "Library", "Model", "Data", "Fit", "Experiments", "Results", "Settings" };
        private const string PrefTab = "SimuLean.Window.Tab";

        /// <summary>The index of the tab named <paramref name="name"/> (callers name tabs, never number them).</summary>
        internal static int TabIndex(string name) => System.Array.IndexOf(Tabs, name);
        private const string PrefabFolder = "Assets/SimuLean.Unity/Prefabs/Elements";

        private int tab;
        private string search = "";
        private Vector2 scroll;
        private bool needsRepaint;

        [MenuItem("Tools/SimuLean/Open SimuLean %#l", priority = 1)]
        public static void Open()
        {
            var w = GetWindow<SimuLeanWindow>("SimuLean");
            w.minSize = new Vector2(320, 420);
            w.Show();
        }

        /// <summary>Opens the window on a tab (0 Library, 1 Model, 2 Data, 3 Fit, 4 Experiments, 5 Results, 6 Settings).</summary>
        public static void Open(int tabIndex)
        {
            Open();
            var w = GetWindow<SimuLeanWindow>();
            w.tab = tabIndex;
            EditorPrefs.SetInt(PrefTab, tabIndex);
        }

        // ── lifecycle ────────────────────────────────────────────────────────

        private void OnEnable()
        {
            titleContent = new GUIContent("SimuLean", EditorGUIUtility.IconContent("d_Prefab Icon").image);
            tab = EditorPrefs.GetInt(PrefTab, 0);
            BuildRegistry();
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.hierarchyChanged -= Repaint;
            EditorApplication.hierarchyChanged += Repaint;
            Selection.selectionChanged -= Repaint;
            Selection.selectionChanged += Repaint;
            ResultsOutput.Produced -= OnResultsProduced;
            ResultsOutput.Produced += OnResultsProduced;
            ResultsOutput.Stored -= OnResultsStored;
            ResultsOutput.Stored += OnResultsStored;
        }

        private void OnResultsProduced(SimuLean.Results.ResultSet set) => needsRepaint = true;   // any thread: repainted in Update

        private void OnDisable()
        {
            ResultsOutput.Produced -= OnResultsProduced;
            ResultsOutput.Stored -= OnResultsStored;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.hierarchyChanged -= Repaint;
            Selection.selectionChanged -= Repaint;
        }

        private void Update()
        {
            if (needsRepaint || (tab == TabIndex("Experiments") && AnyExperimentRunning())) { needsRepaint = false; Repaint(); }
        }

        private void OnGUI()
        {
            if (entries == null) BuildRegistry();
            int t = GUILayout.Toolbar(tab, Tabs, GUILayout.Height(24));
            if (t != tab) { tab = t; EditorPrefs.SetInt(PrefTab, t); scroll = Vector2.zero; }
            EditorGUILayout.Space(2);
            switch (tab)
            {
                case 0: DrawLibrary(); break;
                case 1: DrawModel(); break;
                case 2: DrawData(); break;
                case 3: DrawFit(); break;
                case 4: DrawExperiments(); break;
                case 5: DrawResults(); break;
                default: DrawSettings(); break;
            }
        }

        // ── registry ─────────────────────────────────────────────────────────

        /// <summary>One thing the Library can place: an element, a model tool or a GameObject ▸ SimuLean preset.</summary>
        internal sealed class Entry
        {
            public string Name, Description, Group, Subgroup;
            public Type Type;
            public string PrefabPath;     // elements with a prefab
            public string MenuPath;       // presets
            public System.Reflection.MethodInfo MenuMethod;   // the preset's menu method, called directly by Place
            public string IconPath;       // presets: [SimuLeanPresetIcon]; elements / tools without a prefab: their Icon
            public SimuLeanToolAttribute Tool;
            public int Order;
            /// <summary>The library pack (<see cref="SimuLeanPacks"/>): hidden packs are left out of the Library tab.</summary>
            public string Pack = SimuLeanPacks.Core;
            /// <summary>Shown under Tools (lists, downtimes, logic, data) rather than Elements.</summary>
            public bool IsTool => Group == "Lists" || Group == "Downtimes" || Group == "Logic" || Group == "Data";
            /// <summary>Decoration only (a building, furniture, fences): listed under Visuals when they are switched on.</summary>
            public bool Visual;
            /// <summary>The Library section it is drawn in (<see cref="SectionOf"/>).</summary>
            public string Section => SectionOf(this);
            public bool HasPrefab => !string.IsNullOrEmpty(PrefabPath) && AssetDatabase.AssetPathToGUID(PrefabPath) != "";
        }

        /// <summary>The Library's groups in their order, with the colours the connection drawing uses.</summary>
        private static readonly string[] LibraryGroups = { "Flow", "Resources", "Lists", "Transport", "Downtimes", "Logic", "Data", "Presets" };   // the elements first (review2 #44)

        private static Color GroupColor(string group)
        {
            switch (group)
            {
                case "Flow": return SceneConnections.ColorOf(ConnectionGroup.Flow);
                case "Resources": return SceneConnections.ColorOf(ConnectionGroup.Resources);
                case "Lists": return SceneConnections.ColorOf(ConnectionGroup.Lists);
                case "Transport": return SceneConnections.ColorOf(ConnectionGroup.Network);
                case "Downtimes": return SceneConnections.ColorOf(ConnectionGroup.Downtimes);
                case "Data": return new Color(0.35f, 0.75f, 0.70f);
                case "Logic": return new Color(0.80f, 0.55f, 0.95f);
                case "Experiments": return new Color(0.95f, 0.85f, 0.3f);
                case "Runtime": return new Color(0.6f, 0.65f, 0.75f);
                default: return new Color(0.65f, 0.65f, 0.65f);
            }
        }

        private List<Entry> entries;            // the Library's
        private List<(Type type, SimuLeanToolAttribute info)> tools;

        private void BuildRegistry()
        {
            tools = CollectTools();
            entries = CollectEntries(tools);
        }

        /// <summary>The non-model tools (<see cref="SimuLeanToolAttribute"/>) in their category order.</summary>
        internal static List<(Type type, SimuLeanToolAttribute info)> CollectTools()
            => TypeCache.GetTypesWithAttribute<SimuLeanToolAttribute>().Where(t => !t.IsAbstract)
                .Select(t => (t, t.GetCustomAttribute<SimuLeanToolAttribute>()))
                .OrderBy(x => Rank(SimuLeanToolCategories.Order, x.Item2.Category)).ThenBy(x => x.Item2.Order).ThenBy(x => x.Item2.DisplayName)
                .ToList();

        /// <summary>The Library's entries (elements, tools of the model groups, presets), in the Library's order — also the
        /// catalog of the pipeline commands (<c>simulean_types</c>, <c>simulean_add_element</c>).</summary>
        internal static List<Entry> CollectEntries(List<(Type type, SimuLeanToolAttribute info)> tools = null)
        {
            tools = tools ?? CollectTools();
            var entries = new List<Entry>();
            foreach (var t in TypeCache.GetTypesWithAttribute<SimuLeanElementAttribute>().Where(t => !t.IsAbstract))
            {
                var a = t.GetCustomAttribute<SimuLeanElementAttribute>();
                entries.Add(new Entry
                {
                    Name = a.DisplayName, Description = a.Description, Group = "Flow", Subgroup = a.Module, Type = t,
                    PrefabPath = string.IsNullOrEmpty(a.Prefab) ? "" : $"{PrefabFolder}/{a.Prefab}", IconPath = a.Icon,
                    Order = Rank(SimuLeanModules.Order, a.Module) * 1000 + a.Order,
                    Pack = string.IsNullOrEmpty(a.Pack) ? SimuLeanPacks.OfModule(a.Module) : a.Pack,
                });
            }
            foreach (var (t, a) in tools)
            {
                string g = ModelHierarchy.GroupOf(t);
                if (g == "Data" || g == "Resources" || g == "Lists" || g == "Transport" || g == "Downtimes" || g == "Logic")
                    entries.Add(new Entry { Name = a.DisplayName, Description = a.Description, Group = g, Type = t, Tool = a, Order = a.Order, IconPath = a.Icon,
                                            Pack = string.IsNullOrEmpty(a.Pack) ? SimuLeanPacks.Core : a.Pack });
            }
            // the GameObject ▸ SimuLean ▸ … menu items: presets (racks, conveyor, stackers, building kit)
            foreach (var m in TypeCache.GetMethodsWithAttribute<MenuItem>())
                foreach (MenuItem mi in m.GetCustomAttributes(typeof(MenuItem), false))
                {
                    if (mi.validate || !mi.menuItem.StartsWith("GameObject/SimuLean/")) continue;
                    string rest = mi.menuItem.Substring("GameObject/SimuLean/".Length);
                    int slash = rest.LastIndexOf('/');
                    var packMark = (SimuLeanPresetPackAttribute)Attribute.GetCustomAttribute(m, typeof(SimuLeanPresetPackAttribute));
                    entries.Add(new Entry
                    {
                        Name = slash >= 0 ? rest.Substring(slash + 1) : rest, Group = "Presets",
                        Subgroup = slash >= 0 ? rest.Substring(0, slash) : "", MenuPath = mi.menuItem, MenuMethod = m,
                        IconPath = ((SimuLeanPresetIconAttribute)Attribute.GetCustomAttribute(m, typeof(SimuLeanPresetIconAttribute)))?.AssetPath,
                        Description = "Creates a ready " + rest.Replace("/", " ▸ ").ToLowerInvariant() + ".", Order = mi.priority,
                        Pack = packMark != null ? packMark.Pack : SimuLeanPacks.OfPresetFolder(rest.Split('/')[0]),
                        Visual = Attribute.IsDefined(m, typeof(SimuLeanVisualAttribute)) || rest.StartsWith("Building/") || rest.Contains("/Decor/"),
                    });
                }
            return entries.OrderBy(e => Array.IndexOf(LibraryGroups, e.Group))
                          .ThenBy(e => e.Group == "Flow" ? Rank(SimuLeanModules.Order, e.Subgroup) : 0)   // modules in their order
                          .ThenBy(e => e.Subgroup ?? "").ThenBy(e => e.Order).ThenBy(e => e.Name).ToList();
        }

        /// <summary>The core sections of the Library's Elements switch, in their order.</summary>
        internal static readonly string[] CoreSections = { "Arrivals & exits", "Queues & conveyors", "Stations", "Storage", "Resources & paths" };

        /// <summary>
        /// Where an entry is drawn: a tool under its group; a visual-only preset under Visuals; an entry of a sector pack
        /// under its pack; a core one in its section — by its element module, its preset's menu folder or its group.
        /// </summary>
        internal static string SectionOf(Entry e)
        {
            if (e.IsTool) return e.Group;
            if (e.Visual) return "Visuals";
            if (e.Pack != SimuLeanPacks.Core) return e.Pack;
            if (e.Group == "Resources" || e.Group == "Transport") return "Resources & paths";
            string key = e.Group == "Flow" ? e.Subgroup : (e.Subgroup ?? "").Split('/')[0];
            switch (key)
            {
                case SimuLeanModules.Sources: case SimuLeanModules.Sinks: return "Arrivals & exits";
                case SimuLeanModules.Buffers: case "Queues": case SimuLeanModules.Transport: return "Queues & conveyors";
                case SimuLeanModules.Storage: return "Storage";
                case "Teams": return "Resources & paths";
                default: return "Stations";   // processors, assembly, stacking
            }
        }

        private static int Rank(string[] order, string key)
        {
            int i = Array.IndexOf(order, key);
            return i < 0 ? order.Length : i;
        }

        private bool Matches(string name, string description)
            => string.IsNullOrEmpty(search)
               || name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
               || (description ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

        // ── shared helpers ───────────────────────────────────────────────────

        private static void Select(Object o)
        {
            var c = o as Component;
            Selection.activeObject = c != null ? (Object)c.gameObject : o;
            EditorGUIUtility.PingObject(Selection.activeObject);
        }

        /// <summary>A group heading with its colour strip.</summary>
        private static void GroupHeader(string title, string group, string note = null)
        {
            EditorGUILayout.Space(6);
            var r = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.DrawRect(new Rect(r.x, r.y + 2, 4, r.height - 4), GroupColor(group));
            GUI.Label(new Rect(r.x + 10, r.y, r.width - 10, r.height), title, EditorStyles.boldLabel);
            if (note != null) GUI.Label(new Rect(r.x + 10, r.y, r.width - 14, r.height), note, new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight });
        }

        private static IEnumerable<T> InScenes<T>() where T : Component
            => UnityCompat.FindAll<T>(true).Where(c => c != null && c.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(c));
    }
}
