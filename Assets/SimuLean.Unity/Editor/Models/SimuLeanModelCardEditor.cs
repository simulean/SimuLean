using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The model card's inspector the card's fields, then a read-only <i>Scripts / Logic</i> section — the card's
    /// <c>scripts</c> as <c>simulean_card</c> answers them (<see cref="SimuLeanCommands.CardScripts"/> →
    /// <see cref="SimuLean.Logic.LogicReport.Scripts"/>): the summary (N of M decisions declarative, K by a choice rule), the
    /// decisions per rung of the escalation ladder, each choice rule used (owner, setting, label, Why, reads, parameters) and
    /// <i>Open Model Logic</i>. Computed only when the card's scene is the open scene (else a note), once per open scene and on
    /// <i>Refresh</i> — never per repaint. Drawing it writes nothing: neither the card nor the scene is dirtied.
    /// </summary>
    [CustomEditor(typeof(SimuLeanModelCard))]
    public sealed class SimuLeanModelCardEditor : UnityEditor.Editor
    {
        private Dictionary<string, object> scripts;
        private string scriptsScene;
        private bool stale;
        private bool showScripts = true;
        private GUIStyle wrap;

        /// <summary>The card's scripts (the <c>simulean_card</c> answer's <c>scripts</c>): a dictionary with <c>note</c> when the
        /// card's scene is not the open one or its model cannot be read. <paramref name="root"/> = the model's root (null = the
        /// scene's model).</summary>
        public static Dictionary<string, object> ScriptsOf(SimuLeanModelCard card, GameObject root = null)
            => card == null
                ? new Dictionary<string, object> { ["note"] = "no card" }
                : (Dictionary<string, object>)SimuLeanCommands.CardScripts(card.scenePath, root);

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged -= MarkStale;
            EditorApplication.hierarchyChanged += MarkStale;
        }

        private void OnDisable() => EditorApplication.hierarchyChanged -= MarkStale;

        private void MarkStale() { stale = true; Repaint(); }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space(8);
            DrawScripts();
        }

        private void DrawScripts()
        {
            if (wrap == null) wrap = new GUIStyle(EditorStyles.label) { wordWrap = true };
            showScripts = EditorGUILayout.Foldout(showScripts, new GUIContent("Scripts / Logic",
                "How the model decides: its decisions by rung of the ladder field / expression / query / reaction / rule, and the " +
                "choice rules it uses (read-only; the same as simulean_card's scripts)."), true, EditorStyles.foldoutHeader);
            if (!showScripts) return;

            var card = (SimuLeanModelCard)target;
            string active = SceneManager.GetActiveScene().path;
            if (scripts == null || scriptsScene != active) { scripts = ScriptsOf(card); scriptsScene = active; stale = false; }

            using (new EditorGUI.IndentLevelScope())
            {
                if (scripts.TryGetValue("note", out var note))
                    EditorGUILayout.HelpBox(card.scenePath == active
                        ? Capitalise(note as string) + "."
                        : $"Open the model to see its logic ({(string.IsNullOrEmpty(card.scenePath) ? "the card names no scene" : card.scenePath)}).",
                        MessageType.Info);
                else
                {
                    EditorGUILayout.LabelField(Capitalise(scripts["summary"] as string) + ".", wrap);
                    if (scripts["decisions"] is IDictionary rungs)
                        EditorGUILayout.LabelField("By rung", string.Join("  ·  ", rungs.Keys.Cast<object>().Select(k => $"{k} {rungs[k]}")), wrap);
                    var rules = (scripts["rules"] as IList)?.Cast<IDictionary>().ToList() ?? new List<IDictionary>();
                    if (rules.Count == 0) EditorGUILayout.LabelField("Choice rules", "none: every decision is declarative");
                    foreach (var r in rules) DrawRule(r);
                }

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent(stale ? "Refresh *" : "Refresh", "Read the open scene's model again (it may have changed)."), GUILayout.Width(80)))
                { scripts = ScriptsOf(card); scriptsScene = active; stale = false; }
                if (GUILayout.Button(new GUIContent("Open Model Logic", "Tools ▸ SimuLean ▸ Model Logic: every decision site of the open scene's model."), GUILayout.Width(130)))
                    ModelLogicWindow.Open();
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawRule(IDictionary r)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"{r["label"]}  ({r["rule"]})", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Used by", $"{r["owner"]} — {r["key"]} ({r["site"]})", wrap);
            EditorGUILayout.LabelField("Why", r["why"] as string ?? "— (no Why: simulean_validate warns)", wrap);
            if (r["reads"] is IEnumerable reads) EditorGUILayout.LabelField("Reads", Join(reads), wrap);
            if (r["parameters"] is IList ps && ps.Count > 0)
            {
                var keys = r["catalog"] as IList;
                EditorGUILayout.LabelField("Parameters", string.Join("\n", ps.Cast<object>().Select((p, i) =>
                    keys != null && i < keys.Count ? $"{p}   (study key {keys[i]})" : p.ToString())), wrap);
            }
            if (r["sourceHash"] is string hash) EditorGUILayout.LabelField("Source", hash, wrap);
            if (r["problem"] is string problem) EditorGUILayout.HelpBox(problem, MessageType.Warning);
            EditorGUILayout.EndVertical();
        }

        private static string Join(IEnumerable values) => string.Join(", ", values.Cast<object>());

        private static string Capitalise(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
