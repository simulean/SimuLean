using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Stochastics.Fitting;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The SimuLean window's <b>Fit</b> tab: data → candidate distributions → the chosen one into a model.
    /// Data from pasted numbers, a CSV / Excel / .data file (<see cref="TableFiles"/>), an SQL query on the project
    /// database, or the times recorded in a headless run of the scene (<see cref="RunData"/>); the summary, a histogram
    /// with the selected fit's density, the candidates ranked by Anderson–Darling or Kolmogorov–Smirnov
    /// (<see cref="DistributionFitter"/>), and <b>Insert</b> into the distribution field the tab was opened for (right-click
    /// a distribution ▸ <i>Fit from data…</i>) or <b>Copy spec</b>.
    /// </summary>
    public partial class SimuLeanWindow
    {
        private enum FitSource { Paste, File, Database, Run, Table }

        private FitSource fitSource;
        private string pasteText = "";
        private string filePath = "", sheetName = "";
        private Dictionary<string, List<string>> fileTable;
        private string dbPath = "", sql = "SELECT * FROM my_table";
        private List<Dictionary<string, object>> dbRows;
        private string[] columns = new string[0];
        private int column;
        private string runElement = "";
        private string fitTableName = "";
        private RunQuantity runQuantity = RunQuantity.StayTime;
        private double runTime;
        private int runSeed = 1;

        private FitSample fitSample;
        private List<FitResult> fitResults;
        private FitCriterion criterion = FitCriterion.AndersonDarling;
        private int fitSelected;
        private string fitError;

        private Object fitTarget;
        private string fitTargetPath;
        private Vector2 fitScroll;

        /// <summary>Opens the Fit tab aimed at a distribution field (a <see cref="RandomProcessConfig"/>) to insert the result into.</summary>
        public static void OpenFit(Object target, string propertyPath)
        {
            Open(TabIndex("Fit"));
            var w = GetWindow<SimuLeanWindow>();
            w.fitTarget = target;
            w.fitTargetPath = propertyPath;
            w.Repaint();
        }

        private void DrawFit()
        {
            fitScroll = EditorGUILayout.BeginScrollView(fitScroll);
            DrawFitTarget();

            GroupHeader("Data", "Presets");
            fitSource = (FitSource)GUILayout.Toolbar((int)fitSource, new[] { "Paste", "File", "Database (SQL)", "Run", "Data table" });
            EditorGUILayout.Space(2);
            switch (fitSource)
            {
                case FitSource.Paste: DrawPasteSource(); break;
                case FitSource.File: DrawFileSource(); break;
                case FitSource.Database: DrawDatabaseSource(); break;
                case FitSource.Table: DrawTableSource(); break;
                default: DrawRunSource(); break;
            }
            if (fitError != null) EditorGUILayout.HelpBox(fitError, MessageType.Error);

            if (fitSample != null)
            {
                GroupHeader("Summary", "Presets", $"n = {fitSample.N}");
                SimuLeanGUI.Row("Mean · std dev", $"{F(fitSample.Mean)} · {F(fitSample.StdDev)}", $"CV {F(fitSample.CoefficientOfVariation)}");
                SimuLeanGUI.Row("Min · max", $"{F(fitSample.Min)} · {F(fitSample.Max)}", $"skewness {F(fitSample.Skewness)}");
                DrawHistogram();
                DrawCandidates();
            }
            EditorGUILayout.EndScrollView();
        }

        private static string F(double v) => SimuLeanGUI.Format(Math.Round(v, 4));

        // ── target ───────────────────────────────────────────────────────────

        private void DrawFitTarget()
        {
            var r = EditorGUILayout.GetControlRect(false, 20);
            if (fitTarget != null)
            {
                GUI.Label(new Rect(r.x, r.y, r.width - 70, r.height),
                          new GUIContent($"Insert into: {fitTarget.name} ▸ {ObjectNames.NicifyVariableName(fitTargetPath)}", "Set by right-click on a distribution ▸ Fit from data…"),
                          EditorStyles.boldLabel);
                if (GUI.Button(new Rect(r.xMax - 64, r.y, 64, r.height), "Clear", EditorStyles.miniButton)) { fitTarget = null; fitTargetPath = null; }
            }
            else
                GUI.Label(r, "Right-click a distribution in an inspector ▸ Fit from data… to insert the result there.", EditorStyles.miniLabel);
        }

        // ── sources ──────────────────────────────────────────────────────────

        private void DrawPasteSource()
        {
            EditorGUILayout.LabelField("Numbers (a column copied from a spreadsheet; commas, tabs, spaces or lines):", EditorStyles.miniLabel);
            pasteText = EditorGUILayout.TextArea(pasteText, GUILayout.MinHeight(70));
            if (GUILayout.Button("Fit these values")) Fit(FitSample.ParseNumbers(pasteText), "pasted text");
        }

        private void DrawFileSource()
        {
            EditorGUILayout.BeginHorizontal();
            filePath = EditorGUILayout.TextField("File", filePath);
            if (GUILayout.Button("…", GUILayout.Width(26)))
            {
                string p = EditorUtility.OpenFilePanel("Data file", string.IsNullOrEmpty(filePath) ? Application.dataPath : System.IO.Path.GetDirectoryName(filePath), "csv,xlsx,xls,data,txt");
                if (!string.IsNullOrEmpty(p)) { filePath = p; fileTable = null; }
            }
            EditorGUILayout.EndHorizontal();
            sheetName = EditorGUILayout.TextField(new GUIContent("Sheet", "Excel only (empty = the first)"), sheetName);
            if (GUILayout.Button("Load"))
            {
                fitError = null;
                try
                {
                    fileTable = TableFiles.Read(filePath, string.IsNullOrEmpty(sheetName) ? null : sheetName);
                    columns = fileTable.Keys.ToArray();
                    column = Mathf.Clamp(column, 0, Mathf.Max(0, columns.Length - 1));
                }
                catch (Exception e) { fitError = "Load: " + e.Message; fileTable = null; }
            }
            if (fileTable != null && columns.Length > 0)
            {
                column = EditorGUILayout.Popup("Column", column, columns);
                if (GUILayout.Button("Fit this column")) Fit(Numbers(fileTable[columns[column]]), $"{System.IO.Path.GetFileName(filePath)} ▸ {columns[column]}");
            }
        }

        private void DrawDatabaseSource()
        {
            dbPath = EditorGUILayout.TextField(new GUIContent("Database", "Empty = the project database (Settings ▸ Data)"), dbPath);
            EditorGUILayout.LabelField("Query", EditorStyles.miniLabel);
            sql = EditorGUILayout.TextArea(sql, GUILayout.MinHeight(40));
            if (GUILayout.Button("Run query"))
            {
                fitError = null;
                try
                {
                    string path = string.IsNullOrEmpty(dbPath)
                        ? ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot) : dbPath;
                    dbRows = SimuLean.Data.ProjectDatabaseRegistry.GetOrOpen(path).Query(sql);
                    columns = dbRows.Count > 0 ? dbRows[0].Keys.ToArray() : new string[0];
                    column = 0;
                    if (dbRows.Count == 0) fitError = "The query returned no rows.";
                }
                catch (Exception e) { fitError = "Query: " + e.Message; dbRows = null; }
            }
            if (dbRows != null && columns.Length > 0)
            {
                EditorGUILayout.LabelField(" ", $"{dbRows.Count} rows", EditorStyles.miniLabel);
                column = EditorGUILayout.Popup("Column", column, columns);
                if (GUILayout.Button("Fit this column"))
                    Fit(Numbers(dbRows.Select(row => row.TryGetValue(columns[column], out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : null)), $"query ▸ {columns[column]}");
            }
        }

        /// <summary>A column of one of the scene's data tables: the same rows the model reads.</summary>
        private void DrawTableSource()
        {
            var tables = InScenes<UnityDataTable>().OrderBy(t => t.TableName).ToList();
            if (tables.Count == 0) { EditorGUILayout.HelpBox("The open scenes have no data table (Library ▸ Data).", MessageType.Info); return; }
            var names = tables.Select(t => t.TableName).ToArray();
            int idx = Mathf.Max(0, Array.IndexOf(names, fitTableName));
            fitTableName = names[EditorGUILayout.Popup("Table", idx, names)];
            var table = tables[Array.IndexOf(names, fitTableName)];
            var rows = table.Rows;
            if (rows.Count == 0)
            {
                EditorGUILayout.HelpBox("No rows loaded: load the table (its inspector, or the Data tab).", MessageType.Info);
                return;
            }
            var cols = rows.Keys.ToArray();
            column = Mathf.Clamp(column, 0, cols.Length - 1);
            column = EditorGUILayout.Popup("Column", column, cols);
            if (GUILayout.Button("Fit this column")) Fit(Numbers(rows[cols[column]]), $"{fitTableName} ▸ {cols[column]}");
        }

        private void DrawRunSource()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var elements = InScenes<SElement>().Where(e => e.gameObject.scene == active).Select(e => e.name).OrderBy(n => n).ToArray();
            if (elements.Length == 0) { EditorGUILayout.HelpBox("The active scene has no elements.", MessageType.Info); return; }
            int idx = Mathf.Max(0, Array.IndexOf(elements, runElement));
            runElement = elements[EditorGUILayout.Popup("Element", idx, elements)];
            runQuantity = (RunQuantity)EditorGUILayout.EnumPopup(new GUIContent("Times", "Stay time in the element, or the time between its arrivals / departures."), runQuantity);
            runTime = EditorGUILayout.DoubleField(new GUIContent("Run time", "0 = the clock's run time"), runTime);
            runSeed = EditorGUILayout.IntField("Seed", runSeed);
            if (GUILayout.Button("Run the scene headless and fit"))
            {
                fitError = null;
                try
                {
                    var root = active.GetRootGameObjects().FirstOrDefault(g => g.name == ModelHierarchy.RootName);
                    var config = SceneExtractor.Extract(root).Config;
                    Fit(RunData.Collect(config, runElement, runQuantity, runTime, runSeed), $"run ▸ {runElement} ▸ {ObjectNames.NicifyVariableName(runQuantity.ToString())}");
                }
                catch (Exception e) { fitError = "Run: " + (e.InnerException?.Message ?? e.Message); }
            }
        }

        private static List<double> Numbers(IEnumerable<string> cells)
        {
            var list = new List<double>();
            foreach (var c in cells)
                if (c != null && double.TryParse(c.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) list.Add(v);
            return list;
        }

        private string fitSourceLabel;

        private void Fit(List<double> values, string label)
        {
            fitError = null;
            try
            {
                fitSample = new FitSample(values);
                fitResults = DistributionFitter.FitAll(fitSample, criterion);
                fitSelected = 0;
                fitSourceLabel = label;
                if (fitResults.Count == 0) fitError = "No distribution applies to these values.";
            }
            catch (Exception e) { fitError = e.Message; fitSample = null; fitResults = null; }
        }

        // ── histogram and candidates ─────────────────────────────────────────

        private void DrawHistogram()
        {
            var r = GUILayoutUtility.GetRect(position.width - 30, 150);
            r.x += 8; r.width -= 16;
            EditorGUI.DrawRect(r, EditorGUIUtility.isProSkin ? new Color(0.16f, 0.16f, 0.18f) : new Color(0.92f, 0.92f, 0.93f));
            var s = fitSample;
            var sel = fitResults != null && fitSelected < fitResults.Count ? fitResults[fitSelected].Distribution : null;
            bool discrete = sel != null && sel.Discrete;
            double lo = s.Min, hi = s.Max;
            if (!(hi > lo)) { lo -= 1; hi += 1; }
            int bins = discrete ? (int)Math.Min(60, hi - lo + 1) : Mathf.Clamp((int)Math.Ceiling(Math.Sqrt(s.N)), 8, 40);
            double width = discrete ? 1 : (hi - lo) / bins;
            double start = discrete ? lo - 0.5 : lo;
            var counts = new int[bins];
            foreach (double v in s.Values) counts[Mathf.Clamp((int)((v - start) / width), 0, bins - 1)]++;
            // heights as densities, so the fitted curve shares the scale
            var dens = counts.Select(c => c / (s.N * width)).ToArray();
            double top = dens.Max();
            var curve = new List<Vector2>();
            if (sel != null)
                for (int i = 0; i <= 120; i++)
                {
                    double x = start + (bins * width) * i / 120;
                    double y = discrete ? sel.Density(Math.Round(x)) : sel.Density(x);
                    if (!double.IsNaN(y) && !double.IsInfinity(y)) { top = Math.Max(top, Math.Min(y, dens.Max() * 3)); curve.Add(new Vector2((float)x, (float)y)); }
                }
            if (!(top > 0)) return;
            float px(double x) => r.x + (float)((x - start) / (bins * width)) * r.width;
            float py(double y) => r.yMax - 14 - (float)(y / top) * (r.height - 20);
            var barColor = GroupColor("Flow"); barColor.a = 0.55f;
            for (int i = 0; i < bins; i++)
            {
                float x0 = px(start + i * width), x1 = px(start + (i + 1) * width), y = py(dens[i]);
                EditorGUI.DrawRect(new Rect(x0 + 1, y, Mathf.Max(1, x1 - x0 - 2), r.yMax - 14 - y), barColor);
            }
            if (curve.Count > 1)
            {
                Handles.color = new Color(1f, 0.62f, 0.18f);
                Handles.DrawAAPolyLine(3f, curve.Select(p => new Vector3(px(p.x), py(Math.Min(p.y, top)), 0)).ToArray());
            }
            GUI.Label(new Rect(r.x + 2, r.yMax - 14, 120, 14), F(lo), EditorStyles.miniLabel);
            GUI.Label(new Rect(r.xMax - 122, r.yMax - 14, 120, 14), F(hi), new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperRight });
            if (sel != null) GUI.Label(new Rect(r.x + 4, r.y + 2, r.width - 8, 16), $"{fitSourceLabel} — {sel}", EditorStyles.miniLabel);
        }

        private void DrawCandidates()
        {
            if (fitResults == null || fitResults.Count == 0) return;
            bool counts = fitSample.AllIntegers && fitResults.Any(r => r.Distribution.Discrete);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(counts ? "Ranked by χ² (count data)" : "Ranked by", EditorStyles.boldLabel, GUILayout.Width(counts ? 200 : 70));
            if (!counts)
            {
                var c = (FitCriterion)GUILayout.Toolbar((int)criterion, new[] { "Anderson–Darling", "Kolmogorov–Smirnov" });
                if (c != criterion) { criterion = c; fitResults = DistributionFitter.FitAll(fitSample, criterion); fitSelected = 0; }
            }
            EditorGUILayout.EndHorizontal();

            foreach (var res in fitResults)
            {
                int i = res.Rank - 1;
                var row = EditorGUILayout.GetControlRect(false, 34);
                if (i == fitSelected) EditorGUI.DrawRect(row, new Color(0.24f, 0.48f, 0.9f, 0.3f));
                if (GUI.Button(row, GUIContent.none, GUIStyle.none)) fitSelected = i;
                var verdictColor = res.Verdict == FitVerdict.Good ? new Color(0.3f, 0.8f, 0.4f) : res.Verdict == FitVerdict.Doubtful ? new Color(0.95f, 0.75f, 0.2f) : new Color(0.9f, 0.35f, 0.3f);
                EditorGUI.DrawRect(new Rect(row.x + 2, row.y + 4, 4, row.height - 8), verdictColor);
                GUI.Label(new Rect(row.x + 12, row.y, row.width - 12, 17), $"{res.Rank}. {res.Distribution}", EditorStyles.boldLabel);
                string stats = counts
                    ? $"χ² p {P(res.ChiSquareP)} · AIC {F(res.AIC)} · {res.VerdictText}"
                    : $"A² {F(res.AD)} (p {P(res.ADP)}) · D {F(res.KS)} (p {P(res.KSP)}) · χ² p {P(res.ChiSquareP)} · AIC {F(res.AIC)} · {res.VerdictText}";
                GUI.Label(new Rect(row.x + 12, row.y + 16, row.width - 12, 16), stats, EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField("p-values assume known parameters: with fitted ones they are optimistic.", EditorStyles.wordWrappedMiniLabel);

            var chosen = fitResults[Mathf.Clamp(fitSelected, 0, fitResults.Count - 1)];
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = fitTarget != null;
            if (GUILayout.Button(new GUIContent($"Insert {chosen.Distribution.Family}", fitTarget != null ? "Writes it into the field (Undo)" : "Open the tab from a distribution field (right-click ▸ Fit from data…)"), GUILayout.Height(24)))
                Insert(chosen.Distribution);
            GUI.enabled = true;
            if (GUILayout.Button(new GUIContent("Copy spec", chosen.Spec), GUILayout.Height(24), GUILayout.Width(100)))
            {
                EditorGUIUtility.systemCopyBuffer = chosen.Spec;
                ShowNotification(new GUIContent("Copied " + chosen.Spec));
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string P(double p) => double.IsNaN(p) ? "–" : p < 0.001 ? "<0.001" : p.ToString("0.000", CultureInfo.InvariantCulture);

        /// <summary>Writes the fitted distribution into the target <see cref="RandomProcessConfig"/> (its type and parameters).</summary>
        private void Insert(FittedDistribution d)
        {
            if (fitTarget == null) return;
            if (!Enum.TryParse(d.Family, out RandomProcessType type))
            {
                fitError = $"{d.Family} is not a distribution type of the field.";
                return;
            }
            var so = new SerializedObject(fitTarget);
            var prop = so.FindProperty(fitTargetPath);
            if (prop == null) { fitError = "The field is gone (the object changed)."; return; }
            prop.FindPropertyRelative("strategyType").enumValueIndex = (int)type;
            var fields = RandomProcessConfigDrawer.Parameters(type);
            for (int i = 0; i < fields.Length && i < d.Parameters.Length; i++)
            {
                var q = prop.FindPropertyRelative(fields[i].field);
                if (q == null) continue;
                if (q.propertyType == SerializedPropertyType.Integer) q.intValue = (int)Math.Round(d.Parameters[i]);
                else q.doubleValue = d.Parameters[i];
            }
            so.ApplyModifiedProperties();   // Undo-recorded
            ShowNotification(new GUIContent($"{fitTarget.name}: {d}"));
        }
    }

    /// <summary>Right-click on any distribution field ▸ <i>Fit from data…</i> opens the Fit tab aimed at it.</summary>
    [InitializeOnLoad]
    internal static class FitFieldMenu
    {
        static FitFieldMenu()
        {
            EditorApplication.contextualPropertyMenu += (menu, property) =>
            {
                if (property.type != nameof(RandomProcessConfig)) return;
                var target = property.serializedObject.targetObject;
                string path = property.propertyPath;
                menu.AddItem(new GUIContent("Fit from data…"), false, () => SimuLeanWindow.OpenFit(target, path));
            };
        }
    }
}
