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
    /// (<see cref="ChartRenderer"/>, the Results tab's charts) <b>on a worker thread</b>: the frame only captures the
    /// run (a copy, ≈ 2 ms) and uploads the finished images, so a long run's Gantt (≈ 360 ms to build and draw at a few
    /// thousand intervals in Debug) no longer stalls the scene every second. <b>Read-only</b>: it reads the statistics and never schedules an
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
            if (job != null)
            {
                if (!job.IsCompleted) return;   // one refresh at a time
                Finish(job);
                job = null;
            }
            if (!expanded || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + refreshSeconds;
            job = Begin().Draw();
        }

        /// <summary>Rebuilds every chart from the run now, on this thread (the timer does it on a worker thread).</summary>
        public void Refresh()
        {
            var work = Begin();
            if (job != null) work.Buffers = new ChartBuffers[work.Charts.Count];   // a refresh in flight draws into the kept ones
            work.Run();
            var done = new System.Threading.Tasks.TaskCompletionSource<Work>();
            done.SetResult(work);
            Finish(done.Task);
        }

        // One refresh: what the frame captured (the run's copy, each chart's query, the size), then built and drawn on a
        // worker thread — engine code only (the charts, the renderer; the fonts' cache is locked) — and uploaded by the frame.
        private sealed class Work
        {
            public RunResults Live;
            public string Model;
            public Vector2Int Size;
            public float Ui;
            public readonly List<(ChartQuery spec, string title, ChartData stored)> Charts = new List<(ChartQuery, string, ChartData)>();
            public ChartImage[] Images;
            public ChartBuffers[] Buffers;   // the dashboard's, one per chart: drawn into again (one refresh at a time)

            public System.Threading.Tasks.Task<Work> Draw() => System.Threading.Tasks.Task.Run(() => { Run(); return this; });

            public void Run()
            {
                Dictionary<string, ResultTable> tables = Live != null
                    ? ChartDefinition.Tables(ResultsOutput.SingleRun(Live, Model, System.DateTime.Now)) : null;
                Images = new ChartImage[Charts.Count];
                for (int i = 0; i < Charts.Count; i++)
                {
                    var (spec, title, stored) = Charts[i];
                    if (spec == null) continue;
                    var data = stored ?? (tables != null ? ChartBuilder.Build(spec, tables)
                                                         : new ChartData { Title = title, Message = "No results yet: run the model." });
                    if (string.IsNullOrWhiteSpace(data.Title)) data.Title = title;
                    Images[i] = ChartRenderer.Render(data, Size.x, Size.y, ChartTheme.Panel, Ui, Buffers[i]);
                }
            }
        }

        private System.Threading.Tasks.Task<Work> job;
        private readonly List<ChartBuffers> buffers = new List<ChartBuffers>();

        private Work Begin()
        {
            bool gantt = charts.Exists(c => c != null && (c.spec.Kind == ChartKind.Gantt || c.spec.Kind == ChartKind.JobGantt));
            float ui = SimRuntimeHud.ReferenceScale;
            var work = new Work
            {
                Live = simClock != null ? simClock.CaptureLive(gantt) : null, Model = gameObject.scene.name, Ui = ui,
                Size = new Vector2Int(Mathf.Max(80, Mathf.RoundToInt(chartSize.x * ui)), Mathf.Max(60, Mathf.RoundToInt(chartSize.y * ui))),
            };
            if (work.Live != null) status = $"t = {work.Live.Info.EndTime:0.##}";
            ChartTextures.UseResourceFonts();   // Unity's Resources: on this thread
            string root = ResultsOutput.ProjectRoot;
            foreach (var def in charts)
            {
                if (def == null) { work.Charts.Add((null, null, null)); continue; }
                ChartData data = null;
                if (def.source == ChartDataSource.StoredSets && !stored.TryGetValue(def, out data))   // stored sets don't change: read once
                    stored[def] = data = def.Build((IReadOnlyDictionary<string, ResultTable>)null, root);
                work.Charts.Add((def.spec, def.name, data));
            }
            while (buffers.Count < work.Charts.Count) buffers.Add(new ChartBuffers());
            work.Buffers = buffers.GetRange(0, work.Charts.Count).ToArray();
            return work;
        }

        private void Finish(System.Threading.Tasks.Task<Work> done)
        {
            if (done.IsFaulted)
            {
                status = "chart error: " + done.Exception?.GetBaseException().Message;
                return;
            }
            var work = done.Result;
            for (int i = 0; i < work.Images.Length && i < charts.Count; i++)
            {
                while (textures.Count <= i) textures.Add(null);
                if (work.Images[i] != null) textures[i] = ChartTextures.ToTexture(work.Images[i], textures[i]);
            }
            // charts removed from the list: their textures go too
            for (int i = textures.Count - 1; i >= charts.Count; i--)
            {
                if (textures[i] != null) Destroy(textures[i]);
                textures.RemoveAt(i);
            }
            renderedSize = work.Size;
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
