using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Results.Charts;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// <b>Optional</b> runtime element inspector (IMGUI). Add it to any GameObject of a scene driven by a
    /// <see cref="UnitySimClock"/>; clicking an element (picking of <see cref="SimCameraController"/> when present,
    /// otherwise <see cref="SimCameraController.PickObject"/> from the main camera; both prefer simulation elements over floors / walls) opens a panel docked on the right with three
    /// tabs: <b>KPIs</b> (state, content, input / output, time-weighted WIP, stay times, utilisation, time per
    /// state, active stops), <b>Charts</b> (state split per period, cumulative split, content mean / max over the
    /// run and recently) and <b>Detail</b> (static parameters). The panel can be dragged by its title bar
    /// ("Dock" docks it again); × or <see cref="closeKey"/> closes it.
    ///
    /// <para><b>Read-only and cheap.</b> Everything is read from the engine statistics (Link-layer
    /// <see cref="Statistics.ElementStatsCollector"/>, state tracker / log, time-weighted content
    /// integrals); the history is sampled once per rendered frame and nothing is scheduled on the simulation clock,
    /// so runs are unchanged. KPIs are rebuilt at most <see cref="refreshHz"/> times per real second and charts
    /// once per second, so the panel costs almost nothing at high simulation speeds.</para>
    ///
    /// <para><b>Which elements.</b> Only the elements in <see cref="elements"/> open the panel. The list is kept
    /// filled with every element of the scene (<see cref="autoCollectElements"/>; in the editor whenever the
    /// hierarchy changes, and at <c>Awake</c>) except the ones removed by hand or cleared.</para>
    ///
    /// <para><b>Extensible.</b> Models add rows / charts through <see cref="IElementInspectorContributor"/>:
    /// components on the selected element's hierarchy, the optional <see cref="contributors"/> list, or
    /// <see cref="RegisterContributor"/>. The editor scene set-up adds this panel next to the clock.</para>
    /// </summary>
    [DefaultExecutionOrder(-100)]   // before UnitySimClock, so a consumed Esc does not also quit
    [AddComponentMenu("SimuLean/Runtime/Element Inspector Panel")]
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Visualization, "Element Inspector Panel", Description = "Runtime panel: click an element to see its KPIs and charts", Singleton = true, Order = 1)]
    public class ElementInspectorPanel : MonoBehaviour
    {
        public UnitySimClock simClock;
        [Tooltip("Optional: selection comes from its picking (SelectionChanged). Empty = found in the scene, else a raycast from the main camera.")]
        public SimCameraController cameraController;
        [Tooltip("Elements that open the panel when clicked. Kept filled with every element of the scene (Auto Collect); " +
                 "elements you remove by hand or clear are not re-added.")]
        public List<SElement> elements = new List<SElement>();

        [Tooltip("Keep Elements filled with every element of the scene, except the ones removed by hand or cleared.")]
        public bool autoCollectElements = true;

        [Tooltip("Optional extra contributors (components implementing IElementInspectorContributor) applied to every element. " +
                 "Contributors on the selected element's own hierarchy are used anyway.")]
        public List<MonoBehaviour> contributors = new List<MonoBehaviour>();

        // Elements the user removed / cleared: never re-added automatically.
        [SerializeField, HideInInspector] private List<SElement> excludedElements = new List<SElement>();
        [NonSerialized] private List<SElement> lastElements;

        /// <summary>Adds every element of this scene that is neither listed nor excluded and drops deleted ones. Returns true if the list changed.</summary>
        public bool GatherSceneElements()
        {
            // Drop elements deleted from the scene (destroyed objects), keep empty slots the user is filling.
            bool added = elements.RemoveAll(se => !ReferenceEquals(se, null) && se == null) > 0;
            foreach (var se in UnityCompat.FindAll<SElement>(true))
                if (se != null && se.gameObject.scene == gameObject.scene && !elements.Contains(se) && !excludedElements.Contains(se))
                {
                    elements.Add(se);
                    added = true;
                }
            lastElements = new List<SElement>(elements);
            return added;
        }

        /// <summary>Empties the list; auto collection does not re-add those elements.</summary>
        [ContextMenu("Clear Elements")]
        public void ClearElements()
        {
            foreach (var se in elements) if (se != null && !excludedElements.Contains(se)) excludedElements.Add(se);
            elements.Clear();
            lastElements = new List<SElement>();
        }

        /// <summary>Forgets the exclusions and lists every element of the scene again.</summary>
        [ContextMenu("Add All Scene Elements")]
        public void AddAllSceneElements()
        {
            excludedElements.Clear();
            elements.RemoveAll(se => se == null);
            GatherSceneElements();
        }

        /// <summary>True if clicking <paramref name="el"/> opens the panel.</summary>
        public bool IsInspectable(Element el)
        {
            if (el == null) return false;
            foreach (var se in elements)
                if (se != null && se.GetElement() == el) return true;
            return false;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Elements removed by hand (or replaced) since the last validation become exclusions;
            // an excluded element added back by hand is no longer excluded.
            if (lastElements != null)
                foreach (var se in lastElements)
                    if (se != null && !elements.Contains(se) && !excludedElements.Contains(se))
                        excludedElements.Add(se);
            excludedElements.RemoveAll(se => se == null || elements.Contains(se));
            lastElements = new List<SElement>(elements);
        }

        private void Reset()
        {
            simClock = UnityCompat.FindAny<UnitySimClock>();
            cameraController = UnityCompat.FindAny<SimCameraController>();
            GatherSceneElements();
        }
#endif

        [Header("Behaviour")]
        [Tooltip("KPI refreshes per real second (charts refresh once per second).")]
        public float refreshHz = 4f;
        [Tooltip("Key that closes the panel (and is then not passed to UnitySimClock's Esc-quits). None = only the × button.")]
        public KeyCode closeKey = KeyCode.Escape;
        [Tooltip("Record the content history of every element for the charts (sampled once per frame).")]
        public bool recordHistory = true;
        [Tooltip("History bucket length in simulation units. 0 = automatic (1 hour with a calendar, else MaxTime / 240).")]
        public double historyBucket = 0;
        [Tooltip("State-split chart column length in simulation units. 0 = automatic (1 day with a calendar, else MaxTime / 40).")]
        public double statePeriod = 0;
        [Tooltip("Unit label for times when the clock has no calendar.")]
        public string timeUnitLabel = "tu";
        [Tooltip("Panel width in reference pixels (the run-time panels are laid out on a 1600 × 900 screen and scaled to the Game view).")]
        public float panelWidth = 520f;

        private static readonly List<IElementInspectorContributor> registered = new List<IElementInspectorContributor>();

        /// <summary>Registers a code contributor for every panel (e.g. from a model builder).</summary>
        public static void RegisterContributor(IElementInspectorContributor c) { if (c != null && !registered.Contains(c)) registered.Add(c); }
        public static void UnregisterContributor(IElementInspectorContributor c) => registered.Remove(c);

        /// <summary>The element currently shown (null when closed).</summary>
        public Element Current => open ? element : null;
        public bool IsOpen => open;

        private enum Tab { Kpis, Charts, Detail }
        private Tab tab = Tab.Kpis;
        private Element element;
        private GameObject owner;
        private bool open;
        private InspectorView view;
        private float nextRefresh, nextCharts;
        private readonly ElementLiveHistory history = new ElementLiveHistory();
        private readonly Dictionary<Element, ElementLiveHistory.PeriodStates> stateCache = new Dictionary<Element, ElementLiveHistory.PeriodStates>();
        private readonly List<IElementInspectorContributor> active = new List<IElementInspectorContributor>();
        private SimClock boundClock;
        private SimCalendar boundCalendar;
        private readonly List<Texture2D> chartTex = new List<Texture2D>();
        private readonly List<float> chartMax = new List<float>();
        private readonly List<Vector2> chartPx = new List<Vector2>();
        private bool chartsDirty;
        private Vector2 scroll;
        private Rect window;
        private bool docked = true;
        private float ui = 1f;
        private GUIStyle win, title, sub, label, value, note, header, tabOn, tabOff, btn, close, small, smallRight, smallCenter;

        private void Awake()
        {
            if (simClock == null) simClock = UnityCompat.FindAny<UnitySimClock>();
            if (cameraController == null) cameraController = UnityCompat.FindAny<SimCameraController>();
            if (autoCollectElements) GatherSceneElements();   // also those created at run time
        }

        private void OnEnable() { if (cameraController != null) cameraController.SelectionChanged += OnSelection; }
        private void OnDisable() { if (cameraController != null) cameraController.SelectionChanged -= OnSelection; }

        private void OnDestroy()
        {
            foreach (var t in chartTex) if (t != null) Destroy(t);
            chartTex.Clear();
        }

        // ── selection ────────────────────────────────────────────────────────

        private void OnSelection(GameObject go)
        {
            if (go == null) return;   // clicking empty space keeps the panel (× / close key close it)
            var el = SimRuntimeHud.FindElement(go, out GameObject own);
            if (IsInspectable(el)) Show(el, own);
        }

        /// <summary>Opens the panel on an element (<paramref name="ownerObject"/> = its scene object, optional).</summary>
        public void Show(Element el, GameObject ownerObject = null)
        {
            if (el == null) return;
            if (el != element) scroll = Vector2.zero;
            element = el; owner = ownerObject; open = true;
            nextRefresh = 0; nextCharts = 0; view = null;
        }

        /// <summary>Shows a tab: 0 KPIs, 1 Charts, 2 Detail.</summary>
        public void SelectTab(int t) { tab = (Tab)Mathf.Clamp(t, 0, 2); scroll = Vector2.zero; nextRefresh = 0; nextCharts = 0; }

        public void Close()
        {
            open = false; element = null; view = null; owner = null;
            if (cameraController != null && cameraController.Selected != null) cameraController.Select(null);
        }

        // ── update ───────────────────────────────────────────────────────────

        private void Update()
        {
            if (open && closeKey != KeyCode.None && Input.GetKeyDown(closeKey))
            {
                if (closeKey == KeyCode.Escape) UnitySimClock.ConsumeEscape();
                Close();
            }
            if (cameraController == null && Input.GetMouseButtonDown(0) && !SimRuntimeHud.IsPointerOverHud(Input.mousePosition))
            {
                var picked = SimCameraController.PickObject(Camera.main, Input.mousePosition);
                if (picked != null) OnSelection(picked);
            }
        }

        private void LateUpdate()
        {
            if (simClock == null || simClock.clock == null) return;
            var clock = simClock.clock;
            if (clock != boundClock || clock.Context.Calendar != boundCalendar) Bind(clock);
            if (recordHistory && simClock.SimStarted) history.Sample();
            if (!open || element == null) return;

            float t = Time.unscaledTime;
            if (t < nextRefresh) return;
            nextRefresh = t + 1f / Mathf.Max(0.5f, refreshHz);
            bool charts = tab == Tab.Charts && (t >= nextCharts || view == null || view.Charts.Count == 0);
            if (charts) nextCharts = t + 1f;
            try
            {
                var ctx = MakeContext(clock);
                CollectContributors();
                var v = ElementInspectorReport.Build(element, ctx, charts, active, stateCache);
                if (charts) chartsDirty = true;
                else if (view != null) v.Charts.AddRange(view.Charts);   // keep the last charts between chart refreshes
                view = v;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                nextRefresh = t + 2f;
            }
        }

        private void Bind(SimClock clock)
        {
            boundClock = clock;
            boundCalendar = clock.Context.Calendar;
            stateCache.Clear();
            var cal = boundCalendar;
            double bucket = historyBucket > 0 ? historyBucket
                          : !cal.IsDefault ? cal.FromSeconds(3600)
                          : Math.Max(1e-6, simClock.MaxTime / 240.0);
            history.Attach(clock, bucket);
            if (element != null && !clock.Context.Elements.Contains(element)) Close();
        }

        private ElementInspectorContext MakeContext(SimClock clock)
        {
            var cal = clock.Context.Calendar;
            double period = statePeriod > 0 ? statePeriod
                          : !cal.IsDefault ? cal.FromSeconds(86400)
                          : Math.Max(1e-6, simClock.MaxTime / 40.0);
            return new ElementInspectorContext
            {
                Clock = clock, History = history, Owner = owner, Now = clock.GetSimulationTime(),
                TimeUnitLabel = timeUnitLabel, Period = period, PeriodLabel = !cal.IsDefault && statePeriod <= 0 ? "day" : "period",
            };
        }

        private void CollectContributors()
        {
            active.Clear();
            foreach (var mb in contributors) if (mb is IElementInspectorContributor c && !active.Contains(c)) active.Add(c);
            foreach (var c in registered) if (!active.Contains(c)) active.Add(c);
            if (owner != null)
                foreach (var mb in owner.GetComponentsInParent<MonoBehaviour>())
                    if (mb is IElementInspectorContributor c && !active.Contains(c)) active.Add(c);
        }

        // ── GUI ──────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (!open || view == null) return;
            EnsureStyles();
            GUI.depth = -10;
            ui = SimRuntimeHud.ReferenceScale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(ui, ui, 1));
            float W = Screen.width / ui, H = Screen.height / ui;
            float top = Mathf.Min(150f, H * 0.2f);
            if (docked || window.width <= 0)
                window = new Rect(W - panelWidth - 10, top, panelWidth, Mathf.Max(260, H - top - 20));
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, W - 60)); window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, H - 40));
            if (Event.current.type == EventType.Layout)
                SimRuntimeHud.RegisterScreenRect(new Rect(window.x * ui, window.y * ui, window.width * ui, window.height * ui));
            var before = window;
            window = GUI.Window(0x53494E53, window, DrawWindow, GUIContent.none, win);
            if (window.position != before.position) docked = false;
            GUI.matrix = old;
        }

        private void DrawWindow(int id)
        {
            float w = window.width;
            GUI.DrawTexture(new Rect(0, 0, w, 64), Tex(new Color(0.11f, 0.13f, 0.17f, 1f)));
            GUI.DrawTexture(new Rect(14, 14, 12, 12), Tex(view.StateColor));
            GUI.Label(new Rect(34, 6, w - 190, 26), view.Title, title);
            GUI.Label(new Rect(34, 30, w - 190, 18), view.Subtitle, sub);
            GUI.Label(new Rect(34, 45, w - 190, 18), "State: " + view.StateText, sub);
            if (GUI.Button(new Rect(w - 38, 8, 30, 28), "×", close)) { Close(); return; }
            if (owner != null && cameraController != null && GUI.Button(new Rect(w - 142, 8, 98, 26), "Frame", btn)) cameraController.Frame(owner);
            if (!docked && GUI.Button(new Rect(w - 142, 36, 98, 22), "Dock", btn)) docked = true;

            float tx = 10, ty = 70;
            TabButton(ref tx, ty, "KPIs", Tab.Kpis);
            TabButton(ref tx, ty, "Charts", Tab.Charts);
            TabButton(ref tx, ty, "Detail", Tab.Detail);
            GUI.DrawTexture(new Rect(0, ty + 30, w, 1), Tex(new Color(1, 1, 1, 0.12f)));

            var area = new Rect(10, ty + 36, w - 20, window.height - ty - 44);
            GUILayout.BeginArea(area);
            scroll = GUILayout.BeginScrollView(scroll, false, false);
            float cw = area.width - 22;
            switch (tab)
            {
                case Tab.Kpis: Table(view.Kpis, cw); break;
                case Tab.Detail: Table(view.Detail, cw); break;
                case Tab.Charts: Charts(cw); break;
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.DragWindow(new Rect(0, 0, w - 150, 64));
        }

        private void TabButton(ref float x, float y, string text, Tab t)
        {
            if (GUI.Button(new Rect(x, y, 110, 28), text, tab == t ? tabOn : tabOff) && tab != t)
            { tab = t; scroll = Vector2.zero; nextRefresh = 0; nextCharts = 0; }
            x += 114;
        }

        private void Table(List<InspectorRow> rows, float cw)
        {
            float lw = Mathf.Floor(cw * 0.50f);
            foreach (var r in rows)
            {
                if (r.Header)
                {
                    GUILayout.Space(8);
                    GUILayout.Label(r.Label, header);
                    GUI.DrawTexture(GUILayoutUtility.GetRect(cw, 1), Tex(new Color(1, 1, 1, 0.10f)));
                    continue;
                }
                GUILayout.BeginHorizontal();
                var sw = GUILayoutUtility.GetRect(14, 18, GUILayout.Width(14));
                if (r.Swatch.HasValue) GUI.DrawTexture(new Rect(sw.x, sw.y + 5, 10, 10), Tex(r.Swatch.Value));
                GUILayout.Label(r.Label, label, GUILayout.Width(lw - 14));
                GUILayout.BeginVertical();
                if (!string.IsNullOrEmpty(r.Value)) GUILayout.Label(r.Value, value);
                if (!string.IsNullOrEmpty(r.Note)) GUILayout.Label(r.Note, note);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(6);
        }

        private void Charts(float cw)
        {
            if (view.Charts.Count == 0)
            {
                GUILayout.Label(recordHistory ? "No data yet (recorded while the simulation runs)." : "History recording is off (recordHistory).", label);
                return;
            }
            while (chartTex.Count < view.Charts.Count) { chartTex.Add(null); chartMax.Add(0); chartPx.Add(Vector2.zero); }
            for (int i = 0; i < view.Charts.Count; i++)
            {
                var spec = view.Charts[i];
                GUILayout.Space(6);
                GUILayout.Label(spec.Title, header);
                if (spec.Kind == ChartKind.Bar) { Bars(spec, cw); continue; }
                Legend(spec, cw);
                float ch = 150;
                var r = GUILayoutUtility.GetRect(cw, ch + 18);
                var plot = new Rect(r.x + 44, r.y + 4, r.width - 50, ch);
                var px = new Vector2(Mathf.Round(plot.width * ui), Mathf.Round(plot.height * ui));
                if (Event.current.type == EventType.Repaint)
                {
                    if (chartsDirty || chartPx[i] != px || chartTex[i] == null)
                    {
                        // the engine draws the plot (one chart renderer), the panel writes the labels in its own font
                        ChartTextures.UseResourceFonts();
                        var img = ChartRenderer.RenderPlot(spec, Mathf.Clamp((int)px.x, 16, 2048), Mathf.Clamp((int)px.y, 16, 1024), ChartTheme.Panel,
                                                           out double top, Mathf.Max(1, Mathf.RoundToInt(ui)));
                        chartTex[i] = ChartTextures.ToTexture(img, chartTex[i]);
                        chartMax[i] = (float)top;
                        chartPx[i] = px;
                    }
                    GUI.DrawTexture(plot, chartTex[i]);
                    float ymax = chartMax[i];
                    GUI.Label(new Rect(r.x, plot.y - 4, 42, 16), Num(ymax) + (spec.Unit == "%" ? "%" : ""), small);
                    GUI.Label(new Rect(r.x, plot.y + plot.height / 2 - 8, 42, 16), Num(ymax / 2), small);
                    GUI.Label(new Rect(r.x, plot.yMax - 12, 42, 16), "0", small);
                    GUI.Label(new Rect(plot.x, plot.yMax + 1, 140, 16), spec.XStart ?? "", small);
                    GUI.Label(new Rect(plot.xMax - 140, plot.yMax + 1, 140, 16), spec.XEnd ?? "", smallRight);
                    GUI.Label(new Rect(plot.x + plot.width / 2 - 90, plot.yMax + 1, 180, 16), spec.XCenter ?? "", smallCenter);
                }
            }
            if (Event.current.type == EventType.Repaint) chartsDirty = false;
            GUILayout.Space(8);
            GUILayout.Label("Content means are time-weighted; maxima are sampled once per rendered frame.", note);
        }

        private void Legend(ChartData spec, float cw)
        {
            GUILayout.BeginHorizontal();
            float x = 0;
            foreach (var s in spec.Series)
            {
                float lw = 22 + label.CalcSize(new GUIContent(s.Name)).x * 0.85f;
                if (x + lw > cw) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); x = 0; }
                var sw = GUILayoutUtility.GetRect(12, 16, GUILayout.Width(12));
                GUI.DrawTexture(new Rect(sw.x, sw.y + 4, 10, 10), Tex(InspectorPalette.ToColor(s.Color)));
                GUILayout.Label(s.Name, small, GUILayout.Width(lw - 16));
                x += lw;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // a bar chart as the panel's list: one row per series (Values[0] = share 0–1, Note = its text)
        private void Bars(ChartData spec, float cw)
        {
            for (int i = 0; i < spec.Series.Count; i++)
            {
                var s = spec.Series[i];
                var r = GUILayoutUtility.GetRect(cw, 22);
                float lw = cw * 0.42f, bw = cw * 0.30f;
                GUI.Label(new Rect(r.x, r.y + 2, lw, 18), s.Name, small);
                GUI.DrawTexture(new Rect(r.x + lw, r.y + 5, bw, 12), Tex(new Color(1, 1, 1, 0.07f)));
                float share = s.Values != null && s.Values.Length > 0 ? Mathf.Clamp01((float)s.Values[0]) : 0f;
                GUI.DrawTexture(new Rect(r.x + lw, r.y + 5, bw * share, 12), Tex(InspectorPalette.ToColor(s.Color)));
                GUI.Label(new Rect(r.x + lw + bw + 8, r.y + 2, cw - lw - bw - 8, 18), s.Note ?? "", small);
            }
        }

        private static string Num(float v)
            => v >= 10000 ? ElementInspectorContext.N(v / 1000, "N0") + " k" : ElementInspectorContext.N(v, v >= 10 ? "N0" : "N1");

        // ── styles ───────────────────────────────────────────────────────────

        private static readonly Dictionary<Color, Texture2D> texCache = new Dictionary<Color, Texture2D>();

        private static Texture2D Tex(Color c)
        {
            if (texCache.TryGetValue(c, out var t) && t != null) return t;
            t = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave };
            t.SetPixel(0, 0, c); t.Apply();
            texCache[c] = t;
            return t;
        }

        private void EnsureStyles()
        {
            if (win != null) return;
            var ink = new Color(0.93f, 0.94f, 0.96f);
            var dim = new Color(0.66f, 0.69f, 0.74f);
            win = new GUIStyle(GUI.skin.box) { normal = { background = Tex(new Color(0.075f, 0.085f, 0.105f, 0.96f)) }, padding = new RectOffset(0, 0, 0, 0), border = new RectOffset(1, 1, 1, 1) };
            win.onNormal.background = win.normal.background;
            title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = Color.white }, clipping = TextClipping.Clip };
            sub = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = dim }, clipping = TextClipping.Clip };
            label = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, normal = { textColor = new Color(0.80f, 0.83f, 0.87f) } };
            value = new GUIStyle(label) { fontStyle = FontStyle.Bold, normal = { textColor = ink } };
            note = new GUIStyle(label) { fontSize = 11, normal = { textColor = dim } };
            small = new GUIStyle(label) { fontSize = 11, wordWrap = false, normal = { textColor = new Color(0.78f, 0.80f, 0.84f) } };
            smallRight = new GUIStyle(small) { alignment = TextAnchor.UpperRight };
            smallCenter = new GUIStyle(small) { alignment = TextAnchor.UpperCenter };
            header = new GUIStyle(label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.55f, 0.75f, 1f) } };
            btn = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            close = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
            tabOff = new GUIStyle(GUI.skin.button) { fontSize = 13, normal = { background = Tex(new Color(0.16f, 0.18f, 0.22f)), textColor = dim },
                                                     hover = { background = Tex(new Color(0.21f, 0.24f, 0.29f)), textColor = ink } };
            tabOn = new GUIStyle(tabOff) { fontStyle = FontStyle.Bold, normal = { background = Tex(new Color(0.16f, 0.42f, 0.80f)), textColor = Color.white },
                                           hover = { background = Tex(new Color(0.20f, 0.50f, 0.90f)), textColor = Color.white } };
        }
    }
}
