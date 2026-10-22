using System.Collections.Generic;
using SimuLean.Data;
using SimuLean.Results;
using SimuLean.Results.Charts;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A live dashboard in Play: <see cref="ChartDefinition"/>s drawn over the scene while the model runs, from the
    /// run going on (<see cref="UnitySimClock.CaptureLive"/>: the statistics so far and, with the clock's Series Interval, the
    /// series) — or from their stored sets. Refreshed every <see cref="refreshSeconds"/> of real time, drawn by the engine
    /// (<see cref="ChartRenderer"/>, the Results tab's charts). <b>Read-only</b>: it reads the statistics and never schedules an
    /// event or moves an item, so the run is unchanged. Optional (create it from the SimuLean window ▸ Experiments ▸ Analysis).
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Analysis, "Dashboard",
        Description = "Charts (chart assets) drawn live over the scene in Play, from the run going on. Set the clock's Series Interval " +
                      "for charts over time.", Order = 30)]
    [AddComponentMenu("SimuLean/Dashboard")]
    [DisallowMultipleComponent]
    public sealed class UnityDashboard : MonoBehaviour
    {
        public enum Corner { BottomLeft, BottomRight, TopLeft, TopRight }

        [Tooltip("The charts shown (Assets ▸ Create ▸ SimuLean ▸ Chart, or Results tab ▸ Charts ▸ New chart).")]
        public List<ChartDefinition> charts = new List<ChartDefinition>();
        [Tooltip("The clock whose run is charted (empty = the scene's).")]
        public UnitySimClock simClock;
        [Tooltip("Seconds of real time between refreshes.")]
        [Min(0.1f)] public float refreshSeconds = 1f;
        [Tooltip("Where the dashboard sits on the screen.")]
        public Corner corner = Corner.BottomLeft;
        [Tooltip("Size of one chart (reference 1600 × 900 screen: scaled with the screen like the HUD).")]
        public Vector2 chartSize = new Vector2(480, 300);
        [Tooltip("Charts per row.")]
        [Min(1)] public int columns = 2;
        [Tooltip("Key that shows / hides the dashboard (None = only its header button).")]
        public KeyCode toggleKey = KeyCode.None;
        [Tooltip("Shown when Play starts (else collapsed to its header).")]
        public bool expanded = true;

        private readonly List<Texture2D> textures = new List<Texture2D>();
        private readonly Dictionary<ChartDefinition, ChartData> stored = new Dictionary<ChartDefinition, ChartData>();
        private float nextRefresh;
        private Vector2Int renderedSize;
        private string status = "Waiting for the run.";

        private void Awake()
        {
            if (simClock == null) simClock = UnityCompat.FindAny<UnitySimClock>();
        }

        private void Update()
        {
            if (toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey)) expanded = !expanded;
            if (!expanded || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + refreshSeconds;
            Refresh();
        }

        /// <summary>Rebuilds every chart from the run now (also called by the refresh timer).</summary>
        public void Refresh()
        {
            bool gantt = charts.Exists(c => c != null && (c.spec.Kind == ChartKind.Gantt || c.spec.Kind == ChartKind.JobGantt));
            var live = simClock != null ? simClock.CaptureLive(gantt) : null;
            Dictionary<string, ResultTable> tables = null;
            if (live != null)
            {
                var set = ResultsOutput.SingleRun(live, gameObject.scene.name, System.DateTime.Now);
                tables = ChartDefinition.Tables(set);
                status = $"t = {live.Info.EndTime:0.##}";
            }
            float ui = SimRuntimeHud.ReferenceScale;
            var size = new Vector2Int(Mathf.Max(80, Mathf.RoundToInt(chartSize.x * ui)), Mathf.Max(60, Mathf.RoundToInt(chartSize.y * ui)));
            ChartTextures.UseResourceFonts();
            string root = ResultsOutput.ProjectRoot;
            for (int i = 0; i < charts.Count; i++)
            {
                var def = charts[i];
                while (textures.Count <= i) textures.Add(null);
                if (def == null) continue;
                ChartData data;
                if (def.source == ChartDataSource.StoredSets)   // stored sets don't change during the run: read once
                {
                    if (!stored.TryGetValue(def, out data)) stored[def] = data = def.Build((IReadOnlyDictionary<string, ResultTable>)null, root);
                }
                else data = def.Build(tables, root);
                textures[i] = ChartTextures.ToTexture(ChartRenderer.Render(data, size.x, size.y, ChartTheme.Panel, ui), textures[i]);
            }
            // charts removed from the list: their textures go too
            for (int i = textures.Count - 1; i >= charts.Count; i--)
            {
                if (textures[i] != null) Destroy(textures[i]);
                textures.RemoveAt(i);
            }
            renderedSize = size;
        }

        private void OnGUI()
        {
            if (charts.Count == 0) return;
            float ui = SimRuntimeHud.ReferenceScale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(ui, ui, 1));
            float W = Screen.width / ui, H = Screen.height / ui;
            int cols = Mathf.Clamp(columns, 1, Mathf.Max(1, charts.Count));
            int rows = (charts.Count + cols - 1) / cols;
            const float pad = 6, header = 22;
            float w = cols * chartSize.x + (cols + 1) * pad, h = header + (expanded ? rows * chartSize.y + (rows + 1) * pad : 0);
            float x = corner == Corner.BottomLeft || corner == Corner.TopLeft ? 10 : W - w - 10;
            float y = corner == Corner.TopLeft || corner == Corner.TopRight ? 10 : H - h - 10;
            var box = new Rect(x, y, w, h);
            if (Event.current.type == EventType.Layout) SimRuntimeHud.RegisterScreenRect(new Rect(box.x * ui, box.y * ui, box.width * ui, box.height * ui));

            GUI.color = new Color(0.075f, 0.085f, 0.105f, 0.96f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            if (GUI.Button(new Rect(x + 4, y + 2, 22, header - 4), expanded ? "–" : "+")) { expanded = !expanded; if (expanded) nextRefresh = 0; }
            GUI.Label(new Rect(x + 30, y + 2, w - 34, header - 2), "Dashboard · " + status);
            if (expanded)
                for (int i = 0; i < charts.Count && i < textures.Count; i++)
                {
                    if (textures[i] == null) continue;
                    int c = i % cols, r = i / cols;
                    GUI.DrawTexture(new Rect(x + pad + c * (chartSize.x + pad), y + header + pad + r * (chartSize.y + pad), chartSize.x, chartSize.y), textures[i]);
                }
            GUI.matrix = old;
        }

        private void OnDestroy()
        {
            foreach (var t in textures) if (t != null) Destroy(t);
            textures.Clear();
        }

        /// <summary>The size the charts were last rendered at (pixels), for tests.</summary>
        public Vector2Int RenderedSize => renderedSize;
    }
}
