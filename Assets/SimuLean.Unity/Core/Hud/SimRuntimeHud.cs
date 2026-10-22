using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Lightweight IMGUI runtime HUD for any scene driven by a <see cref="UnitySimClock"/>:
    /// calendar date/time, simulation time, run/pause and speed controls, a view dropdown
    /// for an optional <see cref="SimCameraController"/>, the element picked with the camera
    /// (name, state, content, in/out), the model seed (see / keep / type / draw before each Run or
    /// Restart) and a controls legend.
    /// Optional (off by default): the model's connections drawn over the Game view (C, or the *Connections* button).
    /// Keys: Space pause/resume · +/- (or ]/[) speed ×2/×0.5 · C connections · H toggle help · Esc quits (clock).
    /// </summary>
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Visualization, "Runtime HUD", Description = "Run controls and clock read-out in Play mode", Singleton = true, Order = 2)]
    public class SimRuntimeHud : MonoBehaviour
    {
        public UnitySimClock simClock;
        public SimCameraController cameraController;
        public bool showHelp = true;
        [Tooltip("Speed presets (simulation seconds per real second).")]
        public double[] speedPresets = { 1, 60, 600, 3600, 40000 };
        [Tooltip("Multiplier on the automatic scale (the HUD scales with the Game view, like the element inspector).")]
        public float uiScale = 1f;
        [Tooltip("Start with the clock panel collapsed to its first rows (the − / + button toggles it).")]
        public bool startCollapsed = false;

        [Header("Connections in the Game view (optional)")]
        [Tooltip("Draw the model's connections over the Game view (key C or the Connections button toggles it).")]
        public bool showConnections = false;
        [Tooltip("Element names over the model in Play (on by default for models of up to 40 elements).")]
        public bool showNames = true;
        public bool showFlow = true, showLists = true, showResources = false;
        public bool showLocations = false, showNetwork = false, showDowntimes = false, showLogic = false;

        private float ui = 1f;
        private bool collapsed;
        private readonly List<Connection> connections = new List<Connection>();
        private float nextConnectionScan;
        private static Material lineMaterial;

        private static readonly List<Rect> hudRects = new List<Rect>();
        private static int hudRectsFrame = -1;

        private bool showViews;
        private GUIStyle box, label, bold;
        private ElementInspectorPanel inspectorPanel;

        /// <summary>
        /// The scale of the run-time panels (HUD, element inspector): they are laid out on a 1600 × 900 reference screen and
        /// scaled to fit the Game view, so their arrangement — and that they do not overlap — is the same at every size.
        /// </summary>
        public static float ReferenceScale => Mathf.Max(0.05f, Mathf.Min(Screen.width / 1600f, Screen.height / 900f));

        /// <summary>
        /// Registers a GUI rect (GUI coordinates, top-left origin, already scaled) drawn by another IMGUI panel this frame, so
        /// <see cref="IsPointerOverHud"/> (camera zoom / picking) ignores the mouse over it. Call during the Layout event.
        /// </summary>
        public static void RegisterScreenRect(Rect r)
        {
            if (Event.current == null || Event.current.type != EventType.Layout) return;
            if (hudRectsFrame != Time.frameCount) { hudRects.Clear(); hudRectsFrame = Time.frameCount; }
            hudRects.Add(r);
        }

        /// <summary>True when the screen point (Input.mousePosition, bottom-left origin) is over a HUD panel.</summary>
        public static bool IsPointerOverHud(Vector3 mousePos)
        {
            if (hudRects.Count == 0 || Time.frameCount - hudRectsFrame > 2) return false;
            var p = new Vector2(mousePos.x, Screen.height - mousePos.y);
            foreach (var r in hudRects) if (r.Contains(p)) return true;
            return false;
        }

#if UNITY_EDITOR
        private void Reset()
        {
            simClock = UnityCompat.FindAny<UnitySimClock>();
            cameraController = UnityCompat.FindAny<SimCameraController>();
        }
#endif

        private void Start()
        {
            collapsed = startCollapsed;
            inspectorPanel = UnityCompat.FindAny<ElementInspectorPanel>();
            if (simClock == null) simClock = UnityCompat.FindAny<UnitySimClock>();
            if (cameraController == null) cameraController = UnityCompat.FindAny<SimCameraController>();
            elements = UnityCompat.FindAll<SElement>();
            if (elements.Length > 40) showNames = false;   // a large model: names on demand (N)
            // the help opens by itself the first time only (review2 #45)
            if (PlayerPrefs.GetInt(HelpSeenKey, 0) == 1) showHelp = false;
            else PlayerPrefs.SetInt(HelpSeenKey, 1);
        }

        private const string HelpSeenKey = "SimuLean.Hud.HelpSeen";
        private SElement[] elements = new SElement[0];
        private GUIStyle nameStyle;

        // the elements' names over them (review2 #45: nothing told which object was which in Play)
        private void DrawNames()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (nameStyle == null)
                nameStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12, fontStyle = FontStyle.Bold,
                                                          normal = { textColor = new Color(1f, 1f, 1f, 0.9f) } };
            foreach (var e in elements)
            {
                if (e == null || !e.isActiveAndEnabled) continue;
                Vector3 p = cam.WorldToScreenPoint(e.transform.position + Vector3.up * 2.2f);
                if (p.z <= 0) continue;
                var rect = new Rect(p.x / ui - 80, (Screen.height - p.y) / ui - 10, 160, 20);
                GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), e.name, nameStyle);
                GUI.color = Color.white;
                GUI.Label(rect, e.name, nameStyle);
            }
        }

        private void Update()
        {
            if (simClock == null) return;
            // Typing in the seed field: no shortcuts; a click outside the HUD ends the typing.
            if (GUIUtility.keyboardControl != 0)
            {
                if (Input.GetMouseButtonDown(0) && !IsPointerOverHud(Input.mousePosition)) GUIUtility.keyboardControl = 0;
                return;
            }
            if (Input.GetKeyDown(KeyCode.Space)) TogglePause();
            if (Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.RightBracket))
                simClock.TimeScale = simClock.TimeScale * 2;
            if (Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.LeftBracket))
                simClock.TimeScale = simClock.TimeScale * 0.5;
            if (Input.GetKeyDown(KeyCode.H)) showHelp = !showHelp;
            if (Input.GetKeyDown(KeyCode.C)) showConnections = !showConnections;
            if (Input.GetKeyDown(KeyCode.N)) showNames = !showNames;
        }

        private void TogglePause()
        {
            if (!simClock.SimOn && !simClock.SimStarted) { simClock.RunSimulation(); return; }
            if (simClock.SimPaused) simClock.ResumeSim(); else simClock.PauseSim();
        }

        private void OnGUI()
        {
            if (simClock == null) return;
            if (hudRectsFrame != Time.frameCount && Event.current.type == EventType.Layout)
            { hudRects.Clear(); hudRectsFrame = Time.frameCount; }
            EnsureStyles();
            var old = GUI.matrix;
            ui = Scale();
            GUI.matrix = Matrix4x4.Scale(new Vector3(ui, ui, 1));

            if (showNames) DrawNames();
            DrawClockPanel();
            // the element inspector, when open, shows the selection in full: no summary panel then
            bool inspectorOpen = inspectorPanel != null && inspectorPanel.isActiveAndEnabled && inspectorPanel.IsOpen;
            if (cameraController != null && cameraController.Selected != null && !inspectorOpen) DrawSelectionPanel(cameraController.Selected);
            if (showHelp) DrawHelp(); else DrawHelpButton();

            GUI.matrix = old;
        }

        private void Register(Rect r)
        {
            if (Event.current.type == EventType.Layout)
                hudRects.Add(new Rect(r.x * ui, r.y * ui, r.width * ui, r.height * ui));
        }

        private float Scale() => Mathf.Max(0.1f, uiScale) * ReferenceScale;

        private void DrawClockPanel()
        {
            int nViews = cameraController != null ? cameraController.presets.Count : 0;
            float h = collapsed ? 84 : 128 + SeedRowsHeight + (showViews ? 22 * (nViews + 1) : 0);
            var r = new Rect(10, 10, 330, h);
            Register(r);
            GUI.Box(r, GUIContent.none, box);
            if (GUI.Button(new Rect(r.xMax - 30, r.y + 6, 22, 20), collapsed ? "+" : "−")) collapsed = !collapsed;
            GUILayout.BeginArea(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 10));

            // model time in its unit, numbers in one format; the calendar date only when the model has one (review2 #45)
            double t = simClock.clock.GetSimulationTime();
            double unitSeconds = simClock.TimeUnitSeconds > 0 ? simClock.TimeUnitSeconds : 1;
            string unit = SimuLean.Results.ResultMetrics.TimeUnitName(unitSeconds);
            string days = (t * unitSeconds / 86400.0).ToString("0.00", CultureInfo.InvariantCulture);
            string state = !simClock.SimOn && !simClock.SimStarted ? "not started" : simClock.SimPaused ? "PAUSED" : simClock.SimOn ? "running" : "stopped";
            bool dated = !string.IsNullOrWhiteSpace(simClock.StartDateTime);
            GUILayout.Label(dated ? simClock.CurrentDateTime.ToString("ddd yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture)
                                  : $"t = {t.ToString("0.#", CultureInfo.InvariantCulture)} {unit}", bold);
            GUILayout.Label((dated ? $"t = {t.ToString("0", CultureInfo.InvariantCulture)} {unit} · " : "") + $"day {days}   speed ×{FormatScale(simClock.TimeScale)}   [{state}]", label);

            GUILayout.BeginHorizontal();
            string runLabel = !simClock.SimOn && !simClock.SimStarted ? "Run" : simClock.SimPaused ? "Resume" : "Pause";
            if (GUILayout.Button(runLabel, GUILayout.Width(62))) TogglePause();
            if (GUILayout.Button("×0.5")) simClock.TimeScale = simClock.TimeScale * 0.5;
            if (GUILayout.Button("×2")) simClock.TimeScale = simClock.TimeScale * 2;
            // a speed profile: the user's speed wins; "Auto" gives the speed back to the profile
            var profile = SimSpeedProfile.Active;
            if (profile != null)
            {
                if (profile.Manual) { if (GUILayout.Button(new GUIContent("Auto", "Give the speed back to the speed profile"), GUILayout.Width(46))) profile.ResumeAuto(); }
                else GUILayout.Label(new GUIContent("auto", "The speed profile sets the speed (change it to take over)"), label, GUILayout.Width(46));
            }
            GUILayout.EndHorizontal();
            if (collapsed) { GUILayout.EndArea(); return; }

            GUILayout.BeginHorizontal();
            foreach (var s in speedPresets)
                if (GUILayout.Button(new GUIContent("×" + FormatScale(s), "Speed: model time per real second"))) simClock.TimeScale = s;
            GUILayout.EndHorizontal();

            DrawSeedRows();

            GUILayout.BeginHorizontal();
            if (nViews > 0 && GUILayout.Button(showViews ? "Views ▲" : "Views ▼")) showViews = !showViews;
            if (GUILayout.Button(showConnections ? "Connections ✓" : "Connections")) showConnections = !showConnections;
            if (GUILayout.Button(showNames ? "Names ✓" : "Names")) showNames = !showNames;
            GUILayout.EndHorizontal();
            if (nViews > 0)
            {
                if (showViews)
                {
                    for (int i = 0; i < nViews; i++)
                        if (GUILayout.Button($"{(i < 9 ? (i + 1).ToString() : " ")}  {cameraController.presets[i].name}", GUILayout.Height(20)))
                            cameraController.GoTo(i);
                    if (GUILayout.Button("R  Reset view", GUILayout.Height(20))) cameraController.ResetView();
                }
            }
            GUILayout.EndArea();
        }

        // ── Seed: see it, keep it, type one or draw a new one before Run / Restart ──

        private const float SeedRowsHeight = 48;
        private const string SeedControl = "SimRuntimeHud.Seed";
        private string seedText = "";

        private void DrawSeedRows()
        {
            bool random = simClock.SeedMode == SeedMode.RandomEachRun;
            // The field shows the seed of the next run (Fixed) or, in random mode, the last one.
            string shown = !random ? simClock.Seed.ToString(CultureInfo.InvariantCulture)
                         : simClock.HasLastRunSeed ? simClock.LastRunSeed.ToString(CultureInfo.InvariantCulture) : "";
            if (GUI.GetNameOfFocusedControl() != SeedControl) seedText = shown;
            else if (Event.current.type == EventType.KeyDown &&
                     (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
                GUI.FocusControl(null);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Seed", label, GUILayout.Width(34));
            GUI.SetNextControlName(SeedControl);
            string typed = GUILayout.TextField(seedText, 11, GUILayout.Width(88));
            if (typed != seedText)
            {
                seedText = typed;
                if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) simClock.UseFixedSeed(v);
            }
            bool r = GUILayout.Toggle(random, "random");
            if (r != random)
            {
                if (r) simClock.UseRandomSeedEachRun();
                else simClock.UseFixedSeed(simClock.HasLastRunSeed ? simClock.LastRunSeed : simClock.Seed);
            }
            GUI.enabled = simClock.HasLastRunSeed && !(!random && simClock.Seed == simClock.LastRunSeed);
            if (GUILayout.Button("Keep")) { GUI.FocusControl(null); simClock.KeepLastRunSeed(); }
            GUI.enabled = true;
            if (GUILayout.Button("New")) { GUI.FocusControl(null); simClock.DrawNewSeed(); }
            GUILayout.EndHorizontal();

            string last = simClock.HasLastRunSeed ? simClock.LastRunSeed.ToString(CultureInfo.InvariantCulture) : "—";
            string next = random ? "new random seed" : "seed " + simClock.Seed.ToString(CultureInfo.InvariantCulture);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"last run: {last}   next: {next}", label);
            if (simClock.SimStarted && GUILayout.Button("Restart", GUILayout.Width(62))) simClock.RunSimulation();
            GUILayout.EndHorizontal();
        }

        private void DrawSelectionPanel(GameObject go)
        {
            var el = FindElement(go, out GameObject owner);
            var r = new Rect(10, Screen.height / ui - 146, 330, 136);
            Register(r);
            GUI.Box(r, GUIContent.none, box);
            GUILayout.BeginArea(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 10));
            GUILayout.Label(el != null ? el.GetName() : owner.name, bold);
            if (el != null)
            {
                GUILayout.Label($"State: {el.CurrentState}", label);
                var st = el.StatsCollector;
                if (st != null)
                {
                    GUILayout.Label($"Content: {st.ContentCurrent:F0}  (max {st.ContentMax:F0}, avg {st.ContentAvgTimeWeighted:F1})", label);
                    GUILayout.Label($"In: {st.TotalInput}   Out: {st.TotalOutput}   avg stay {st.AvgStayTime:F0} s", label);
                }
            }
            else GUILayout.Label("(no simulation element)", label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Frame (F)")) cameraController.Frame(owner);
            if (GUILayout.Button("Deselect")) cameraController.Select(null);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawHelp()
        {
            const string text =
                "Right-drag  orbit (screen centre)\n" +
                "Left / middle-drag, WASD  pan (Shift faster)\n" +
                "Wheel  zoom to cursor     Q / E  down / up\n" +
                "Left-click  select        F  frame object\n" +
                "1-9  preset views         R  reset view\n" +
                "Space  pause/resume       + / -  speed ×2 / ×0.5\n" +
                "H  help    C  connections    N  names    Esc  quit";
            float w = 330, hgt = 132;
            var r = new Rect(Screen.width / ui - w - 10, 10, w, hgt);
            Register(r);
            GUI.Box(r, GUIContent.none, box);
            GUI.Label(new Rect(r.x + 8, r.y + 6, w - 16, hgt - 10), text, label);
            if (GUI.Button(new Rect(r.xMax - 70, r.yMax - 24, 62, 20), "Hide (H)")) showHelp = false;
        }

        // the help hidden: a small button in the top-right corner brings it back
        private void DrawHelpButton()
        {
            var r = new Rect(Screen.width / ui - 80, 10, 70, 24);
            Register(r);
            if (GUI.Button(r, "Help (H)")) showHelp = true;
        }

        // ── Connections over the Game view (optional, read-only) ─────────────

        private bool GroupShown(ConnectionGroup g)
        {
            switch (g)
            {
                case ConnectionGroup.Flow: return showFlow;
                case ConnectionGroup.Lists: return showLists;
                case ConnectionGroup.Resources: return showResources;
                case ConnectionGroup.Locations: return showLocations;
                case ConnectionGroup.Network: return showNetwork;
                case ConnectionGroup.Logic: return showLogic;
                default: return showDowntimes;
            }
        }

        private void OnRenderObject()
        {
            if (!showConnections) return;
            var cam = Camera.current;
            if (cam == null || cam.cameraType != CameraType.Game) return;
            if (Time.unscaledTime >= nextConnectionScan)   // the wiring does not change during a run; rescan now and then
            {
                connections.Clear();
                connections.AddRange(SceneConnections.All());
                nextConnectionScan = Time.unscaledTime + 1f;
            }
            if (lineMaterial == null)
            {
                lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                lineMaterial.SetInt("_ZWrite", 0);
                lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            }
            lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.QUADS);
            foreach (var c in connections)
            {
                if (!c.IsValid || !GroupShown(SceneConnections.GroupOf(c.Kind))) continue;
                Color col = SceneConnections.ColorOf(SceneConnections.GroupOf(c.Kind));
                col.a = 0.85f;
                Ribbon(cam, SceneConnections.Anchor(c.From), SceneConnections.Anchor(c.To), col, SceneConnections.IsDirected(c));
            }
            GL.End();
            GL.PopMatrix();
        }

        // a camera-facing band from a to b (width proportional to the distance to the camera) and an arrow head
        private static void Ribbon(Camera cam, Vector3 a, Vector3 b, Color col, bool arrow)
        {
            Vector3 dir = b - a;
            if (dir.sqrMagnitude < 1e-6f) return;
            Vector3 mid = (a + b) * 0.5f;
            float w = Vector3.Distance(cam.transform.position, mid) * 0.0025f;
            Vector3 side = Vector3.Cross(dir, cam.transform.position - mid).normalized * w;
            GL.Color(col);
            GL.Vertex(a - side); GL.Vertex(a + side); GL.Vertex(b + side); GL.Vertex(b - side);
            if (!arrow) return;
            Vector3 tip = Vector3.Lerp(a, b, 0.6f), f = dir.normalized * w * 8f;
            GL.Vertex(tip + f); GL.Vertex(tip - f * 0.2f + side * 4f); GL.Vertex(tip - f * 0.2f); GL.Vertex(tip - f * 0.2f - side * 4f);
        }

        private static string FormatScale(double s)
            => s >= 1000 ? (s / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : s.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Finds the engine <see cref="Element"/> behind a picked object by walking up the hierarchy:
        /// an <see cref="SElement"/> wrapper, or any component exposing an <see cref="Element"/> field/property
        /// (e.g. case-specific views).
        /// </summary>
        public static Element FindElement(GameObject go, out GameObject owner)
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                foreach (var c in t.GetComponents<MonoBehaviour>())
                {
                    if (c == null) continue;
                    Element el = null;
                    if (c is SElement se) { try { el = se.GetElement(); } catch { } }
                    else el = ReflectElement(c);
                    if (el != null) { owner = t.gameObject; return el; }
                }
            }
            owner = go;
            return null;
        }

        private static readonly Dictionary<System.Type, MemberInfo> memberCache = new Dictionary<System.Type, MemberInfo>();

        private static Element ReflectElement(MonoBehaviour c)
        {
            var type = c.GetType();
            if (!memberCache.TryGetValue(type, out var m))
            {
                const BindingFlags f = BindingFlags.Instance | BindingFlags.Public;
                foreach (var fi in type.GetFields(f)) if (typeof(Element).IsAssignableFrom(fi.FieldType)) { m = fi; break; }
                if (m == null)
                    foreach (var pi in type.GetProperties(f))
                        if (pi.CanRead && pi.GetIndexParameters().Length == 0 && typeof(Element).IsAssignableFrom(pi.PropertyType)) { m = pi; break; }
                memberCache[type] = m;
            }
            if (m is FieldInfo fld) return fld.GetValue(c) as Element;
            if (m is PropertyInfo prop) { try { return prop.GetValue(c) as Element; } catch { return null; } }
            return null;
        }

        private Texture2D boxTexture;

        // the panel background goes with the HUD (a texture per HUD instance was never released)
        private void OnDestroy()
        {
            if (boxTexture != null) Destroy(boxTexture);
            boxTexture = null;
            box = null;
        }

        private void EnsureStyles()
        {
            if (box != null) return;
            boxTexture = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave };
            boxTexture.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.11f, 0.78f));
            boxTexture.Apply();
            box = new GUIStyle(GUI.skin.box) { normal = { background = boxTexture } };
            label = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.9f, 0.92f, 0.95f) } };
            bold = new GUIStyle(label) { fontSize = 15, fontStyle = FontStyle.Bold };
        }
    }
}
