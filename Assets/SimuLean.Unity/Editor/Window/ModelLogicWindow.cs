using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Logic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// <i>Tools ▸ SimuLean ▸ Model Logic</i>: the logic viewer — the decisions of the open
    /// scene's model, one plain-language line each (<see cref="Decision.Sentence"/>: <c>Route best score: … · if best is full:
    /// skip</c>) behind a chip of its site (routing, admission, order, dispatch, reaction, rule, wait, labels…), grouped by element,
    /// team, list, reaction and downtime. By default only the real decisions show — the settings that are not their default and
    /// apply —; the objects without a decision of their own share one dimmed line; <i>All</i> shows every decision with its raw
    /// <c>key = value</c> settings (always in the line's tooltip). The objects a decision reads, writes or is woken by are links:
    /// a click selects and pings the object and scrolls the window to it. The same report as <c>simulean_model section=logic</c>
    /// (<see cref="ModelLogic"/>, built in the engine from the extracted model, so the scene and a headless run show the same).
    /// In Play it shows the running model's decisions (<see cref="SimuLean.Headless.HeadlessModelFactory.Config"/>) with their
    /// run-time counters beside each line (<see cref="ModelLogic.Live"/>: items per output and the last choice, admissions and
    /// wake-ups, releases, requests served, a reaction's fires, a rule's calls), read about every 0.5 s, never every frame.
    /// Read-only: it extracts the scene when refreshed and changes nothing. A window, not a scene component, so it is no
    /// <see cref="SimuLeanToolAttribute"/> tool; the SimuLean window's Model tab opens it too.
    /// </summary>
    public sealed class ModelLogicWindow : EditorWindow
    {
        private static readonly string[] Kinds = { "element", "team", "list", "reaction", "downtime" };
        private static readonly string[] KindTitles = { "Elements", "Teams", "Lists", "Reactions", "Downtimes" };

        private LogicReport report;
        private string error;
        private string search = "";
        private bool showAll;
        private Vector2 scroll;
        private readonly HashSet<string> folded = new HashSet<string>();
        private readonly Dictionary<string, float> ownerY = new Dictionary<string, float>();
        private string scrollTo;
        private SimuLean.Headless.HeadlessModelFactory liveModel;
        private UnitySimClock liveClock;
        private double nextLive;

        /// <summary>The seconds between two reads of the counters in Play.</summary>
        public const double LiveInterval = 0.5;
        private GUIStyle wrap, mini, chip, link, header;

        [MenuItem("Tools/SimuLean/Model Logic", priority = 3)]
        public static void Open()
        {
            var w = GetWindow<ModelLogicWindow>("Model Logic");
            w.minSize = new Vector2(380, 300);
            w.Refresh();
            w.Show();
        }

        /// <summary>The report of the open scene's model (null when it could not be extracted: <see cref="Error"/>).</summary>
        public LogicReport Report => report;

        /// <summary>Why the last refresh gave no report, or null.</summary>
        public string Error => error;

        /// <summary>Every decision shown, not only the real ones (the <i>All</i> toggle).</summary>
        public bool ShowAll { get => showAll; set { showAll = value; Repaint(); } }

        /// <summary>Extracts the open scene's model again (or the model under <paramref name="root"/>) and rebuilds the report
        /// (read-only).</summary>
        public void Refresh(GameObject root = null)
        {
            objects.Clear();
            liveModel = null;
            try { report = ModelLogic.Of(SceneStudy.Of(root).Model); error = null; }
            catch (Exception e) { report = null; error = e.Message; }
            Repaint();
        }

        /// <summary>The lines the window shows in its current view, as text (an owner as <c>name (type)</c>, a decision as
        /// <c>[site] sentence</c>, the objects without decisions as one line): what a test or an agent reads of it.</summary>
        public List<string> VisibleLines()
        {
            var lines = new List<string>();
            if (report == null) return lines;
            foreach (var kind in Kinds)
            {
                var (shown, plain) = Owners(kind);
                foreach (var o in shown)
                {
                    lines.Add($"{o.Name} ({o.Type})");
                    foreach (var d in VisibleDecisions(o))
                    {
                        lines.Add($"  [{d.Site}] {d.Sentence}" + (d.IsDefault ? " (default)" : ""));
                        if (d.LiveText != null) lines.Add("    live: " + d.LiveText);
                    }
                }
                if (plain.Count > 0) lines.Add(PlainLine(kind, plain));
            }
            return lines;
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Model Logic");
            EditorApplication.hierarchyChanged -= MarkStale;
            EditorApplication.hierarchyChanged += MarkStale;
        }

        private void OnDisable() => EditorApplication.hierarchyChanged -= MarkStale;

        /// <summary>Shows <paramref name="model"/>'s decisions with its run-time counters (what the window does by itself in
        /// Play with the scene's running model): the report of the model it built, the counters read now.</summary>
        public void ShowLive(SimuLean.Headless.HeadlessModelFactory model)
        {
            if (model?.Config == null) return;
            if (!ReferenceEquals(model, liveModel))
            {
                liveModel = model;
                objects.Clear();
                try { report = ModelLogic.Of(model.Config); error = null; }
                catch (Exception e) { report = null; error = e.Message; return; }
            }
            ModelLogic.Live(report, model);
            Repaint();
        }

        // in Play: the running model's counters, about every 0.5 s (never every frame)
        private void Update()
        {
            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < nextLive) return;
            nextLive = EditorApplication.timeSinceStartup + LiveInterval;
            if (liveClock == null) liveClock = UnityCompat.FindAny<UnitySimClock>();
            if (liveClock != null && liveClock.Model != null) ShowLive(liveClock.Model);
        }

        private bool stale;
        private void MarkStale() { stale = true; objects.Clear(); Repaint(); }

        // ── what is shown ────────────────────────────────────────────────

        private IEnumerable<Decision> VisibleDecisions(LogicOwner o) => o.Decisions.Where(d => showAll || !d.IsDefault);

        // the owners of a kind with a line of their own, and those without a decision of their own (one dimmed line)
        private (List<LogicOwner> shown, List<LogicOwner> plain) Owners(string kind)
        {
            var shown = new List<LogicOwner>();
            var plain = new List<LogicOwner>();
            foreach (var o in report.OfKind(kind).Where(Matches))
            {
                if (o.HasOwnLogic || (showAll && o.Decisions.Count > 0)) shown.Add(o);
                else if (kind == "element") plain.Add(o);
            }
            return (shown, plain);
        }

        private static string PlainLine(string kind, List<LogicOwner> plain)
            => $"No decisions of their own ({plain.Count}): " + string.Join(", ", plain.Select(o => o.Name));

        private bool Matches(LogicOwner o)
        {
            if (string.IsNullOrEmpty(search)) return true;
            string q = search.Trim();
            bool Has(string text) => text != null && text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has(o.Name) || Has(o.Type) || o.Decisions.Any(d => Has(d.Sentence) || d.Sites.Any(s => Has(s.Setting)));
        }

        // ── drawing ──────────────────────────────────────────────────────

        private void Styles()
        {
            if (wrap != null) return;
            wrap = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };
            mini = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, richText = true };
            chip = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, padding = new RectOffset(2, 2, 1, 1) };
            chip.normal.textColor = Color.white;
            link = new GUIStyle(EditorStyles.linkLabel) { fontSize = EditorStyles.miniLabel.fontSize, wordWrap = false };
            header = new GUIStyle(EditorStyles.foldout) { richText = true, fontStyle = FontStyle.Bold };
        }

        private static bool Dark => EditorGUIUtility.isProSkin;

        // the chip colour of a site (darker in the light theme, so the white text reads on both)
        private static Color SiteColor(string site, string verb)
        {
            if (verb == Decision.VerbRule) return Dark ? new Color(0.62f, 0.38f, 0.78f) : new Color(0.50f, 0.28f, 0.66f);
            switch (site)
            {
                case "routing": return Dark ? new Color(0.24f, 0.52f, 0.86f) : new Color(0.16f, 0.42f, 0.76f);
                case "admission": return Dark ? new Color(0.22f, 0.62f, 0.40f) : new Color(0.13f, 0.50f, 0.30f);
                case "order": return Dark ? new Color(0.84f, 0.56f, 0.16f) : new Color(0.72f, 0.44f, 0.06f);
                case "dispatch": case "resources": return Dark ? new Color(0.16f, 0.62f, 0.66f) : new Color(0.08f, 0.48f, 0.52f);
                case "reaction": return Dark ? new Color(0.80f, 0.36f, 0.48f) : new Color(0.68f, 0.24f, 0.36f);
                case "wait": return Dark ? new Color(0.80f, 0.40f, 0.30f) : new Color(0.70f, 0.30f, 0.20f);
                case "labels": return Dark ? new Color(0.50f, 0.54f, 0.60f) : new Color(0.40f, 0.44f, 0.50f);
                default: return Dark ? new Color(0.45f, 0.48f, 0.70f) : new Color(0.34f, 0.37f, 0.60f);
            }
        }

        private static string Dim(string text) => $"<color={(Dark ? "#9A9A9A" : "#707070")}>{text}</color>";

        private void OnGUI()
        {
            Styles();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(new GUIContent(stale ? "Refresh *" : "Refresh", "Extract the open scene again (the model may have changed)."), EditorStyles.toolbarButton, GUILayout.Width(70)))
            { stale = false; Refresh(); }
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
            showAll = GUILayout.Toggle(showAll, new GUIContent("All", "Show every decision — the defaults too (first in first out, first available…) — " +
                                                                      "with its raw settings (key = value)."), EditorStyles.toolbarButton, GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();
            if (liveModel != null)
                EditorGUILayout.LabelField(EditorApplication.isPlaying ? $"Live: t = {liveModel.Clock.GetSimulationTime():0.##}, counters every {LiveInterval} s"
                                                                       : "Counters of the last run (Refresh reads the scene again).", EditorStyles.miniLabel);

            if (report == null)
            {
                EditorGUILayout.HelpBox(error != null ? "The model could not be read: " + error : "Refresh to read the open scene's model.", MessageType.Info);
                return;
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            var notes = report.Notes;
            if (notes.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", notes), MessageType.Warning);
            int lines = 0;
            for (int k = 0; k < Kinds.Length; k++)
            {
                var (shown, plain) = Owners(Kinds[k]);
                if (shown.Count == 0 && plain.Count == 0) continue;
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField($"{KindTitles[k]} ({shown.Count + plain.Count})", EditorStyles.boldLabel);
                foreach (var o in shown) { DrawOwner(o); lines++; }
                if (plain.Count > 0) DrawPlain(Kinds[k], plain);
            }
            if (lines == 0 && string.IsNullOrEmpty(search))
                EditorGUILayout.HelpBox("Every decision of the model is the elements' default. Turn on All to see them.", MessageType.None);
            if (report.WakeUps.Count > 0 && showAll)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Admission wake-ups", EditorStyles.boldLabel);
                foreach (var w in report.WakeUps)
                    EditorGUILayout.LabelField($"{w.Element} ← a write of {(w.Property == null ? "any property" : "'" + w.Property + "'")}: " +
                                               (w.WrittenBy.Count > 0 ? string.Join(", ", w.WrittenBy) : "<i>nothing writes it</i>"), wrap);
            }
            EditorGUILayout.EndScrollView();
            if (scrollTo != null && Event.current.type == EventType.Repaint && ownerY.TryGetValue(scrollTo, out float y))
            {
                scroll.y = Mathf.Max(0, y - 4);
                scrollTo = null;
                Repaint();
            }
        }

        private void DrawOwner(LogicOwner o)
        {
            string id = o.Kind + "/" + o.Name;
            EditorGUILayout.BeginHorizontal();
            bool open = !folded.Contains(id);
            string next = o.Next.Count > 0 ? (o.Kind == "reaction" ? " on " : " → ") + string.Join(", ", o.Next) : "";
            bool now = EditorGUILayout.Foldout(open, new GUIContent($"{o.Name}  " + Dim(o.Type + next)), true, header);
            if (Event.current.type == EventType.Repaint) ownerY[o.Name] = GUILayoutUtility.GetLastRect().y;
            if (now != open) { if (now) folded.Remove(id); else folded.Add(id); }
            var go = FindObject(o.Name);
            using (new EditorGUI.DisabledScope(go == null))
                if (GUILayout.Button(new GUIContent("Select", go == null ? "No scene object of this name." : "Select and ping it in the hierarchy."), EditorStyles.miniButton, GUILayout.Width(50)))
                    Ping(go);
            EditorGUILayout.EndHorizontal();
            if (!now) return;
            EditorGUI.indentLevel++;
            foreach (var d in VisibleDecisions(o)) DrawDecision(o, d);
            if (showAll)
                foreach (string u in o.UsedBy) EditorGUILayout.LabelField("used by " + u, mini);
            foreach (string n in o.Notes) EditorGUILayout.HelpBox(n, MessageType.Warning);
            EditorGUI.indentLevel--;
        }

        private void DrawDecision(LogicOwner o, Decision d)
        {
            var tip = new List<string>(d.Settings);
            if (d.Reads.Count > 0) tip.Add("reads " + string.Join(", ", d.Reads));
            if (d.Writes.Count > 0) tip.Add("writes " + string.Join(", ", d.Writes));
            if (d.ViewOnly) tip.Add("view only: changes nothing the model reads");
            string tooltip = string.Join("\n", tip);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15f);
            string label = d.Verb == Decision.VerbRule ? "rule" : d.Site;
            var r = GUILayoutUtility.GetRect(new GUIContent(label), chip, GUILayout.Width(66), GUILayout.Height(16));
            var c = SiteColor(d.Site, d.Verb);
            if (d.IsDefault) c.a = 0.45f;
            EditorGUI.DrawRect(r, c);
            GUI.Label(r, new GUIContent(label, d.Site), chip);
            string text = $"<b>{Esc(d.Verb)}</b> {Esc(d.Text)}";
            if (d.IsDefault) text = Dim(text + " (default)");
            if (d.Problem != null) text += $"  <color=#E05050>does not parse: {Esc(d.Problem)}</color>";
            EditorGUILayout.LabelField(new GUIContent(text, tooltip), wrap);
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.IndentLevelScope(1))
            {
                if (d.LiveText != null)
                    EditorGUILayout.LabelField(new GUIContent($"<color={(Dark ? "#6CC6E8" : "#1F6F8B")}>▶ {Esc(d.LiveText)}</color>",
                                                              "Run-time counters of this decision (read-only)."), mini);
                if (showAll)
                    EditorGUILayout.LabelField(Dim(Esc(string.Join(" · ", d.Settings))), mini);
                if (d.Links.Count > 0) DrawLinks(d);
            }
        }

        // the objects a decision names: a click selects / pings it and scrolls to it
        private void DrawLinks(Decision d)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space((EditorGUI.indentLevel + 1) * 15f);
            GUILayout.Label(Dim("→"), mini, GUILayout.ExpandWidth(false));
            foreach (string name in d.Links)
                if (GUILayout.Button(new GUIContent(name, "Select it and go to its decisions."), link, GUILayout.ExpandWidth(false)))
                    Go(name);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPlain(string kind, List<LogicOwner> plain)
        {
            var old = GUI.color;
            GUI.color = new Color(old.r, old.g, old.b, 0.65f);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"No decisions of their own ({plain.Count}):", mini, GUILayout.ExpandWidth(false));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(15);
            float width = 15, max = position.width - 40;
            foreach (var o in plain)
            {
                var content = new GUIContent(o.Name, $"{o.Type}: default routing and admission. Select it.");
                float w = link.CalcSize(content).x + 6;
                if (width + w > max) { GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal(); EditorGUILayout.BeginHorizontal(); GUILayout.Space(15); width = 15; }
                if (GUILayout.Button(content, link, GUILayout.ExpandWidth(false))) Go(o.Name);
                width += w;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUI.color = old;
        }

        private void Go(string name)
        {
            var go = FindObject(name);
            if (go != null) Ping(go);
            if (report?.Find(name) is LogicOwner target)
            {
                folded.Remove(target.Kind + "/" + target.Name);
                scrollTo = target.Name;
            }
            Repaint();
        }

        private static void Ping(GameObject go)
        {
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        // rich text: a '<' in an expression must not open a tag
        private static string Esc(string s) => s?.Replace("<", "<​");

        private readonly Dictionary<string, GameObject> objects = new Dictionary<string, GameObject>();

        // the scene object of an owner (null when there is none or several share the name), looked up once per refresh
        private GameObject FindObject(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (objects.TryGetValue(name, out var go)) return go;
            try { go = SimuLeanCommands.FindObject(name); }
            catch (ArgumentException) { go = null; }
            objects[name] = go;
            return go;
        }
    }
}
