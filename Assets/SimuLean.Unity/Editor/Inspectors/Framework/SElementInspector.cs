using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The inspector standard of every SimuLean element: a header (type, module, state in Play) with the
    /// problems found and their fix, then the sections <b>Parameters → Flow → Resources → Visual → Statistics</b>
    /// (statistics in Play only, the same KPIs as the run-time element panel, <see cref="ElementInspectorReport"/>).
    /// An element's inspector only says what goes into Parameters and Visual (and its own checks); the rest is shared.
    /// </summary>
    public abstract class SElementInspector : UnityEditor.Editor
    {
        /// <summary>The element's parameters (capacity, times, rules…). Use <see cref="SimuLeanGUI.Field"/>.</summary>
        protected abstract void DrawParameters();

        /// <summary>How the element looks (item positions, spacing, animator). Empty = no Visual section.</summary>
        protected virtual void DrawVisual() { }
        protected virtual bool HasVisual => true;

        /// <summary>Elements that choose their inputs themselves (Combiner) or never receive (sources) say false.</summary>
        protected virtual bool DrawsReceive => true;

        /// <summary>Element-specific problems (added after the shared ones).</summary>
        protected virtual void CollectIssues(List<InspectorIssue> issues) { }

        /// <summary>More of the Flow section (e.g. the combiner's inputs), drawn after the shared fields.</summary>
        protected virtual void DrawFlowExtra() { }

        /// <summary>More of the Statistics section in Play (live controls, progress), drawn after the KPIs.</summary>
        protected virtual void DrawPlayExtra() { }

        protected SElement Element => (SElement)target;
        protected SerializedProperty Prop(string name) => serializedObject.FindProperty(name);
        private string Key(string section) => target.GetType().Name + "." + section;

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        protected virtual void OnEnable()
        {
            // the connection drawing switch is in the Visual section: its component stays out of the inspector
            if (target != null && Element.GetComponent<SElementConnectionGizmo>() is SElementConnectionGizmo g)
                g.hideFlags |= HideFlags.HideInInspector;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawElementHeader();

            if (SimuLeanGUI.Section(Key("Parameters"), "Parameters")) { EditorGUI.indentLevel++; DrawParameters(); DrawProperties(); EditorGUI.indentLevel--; }
            if (SimuLeanGUI.Section(Key("Flow"), "Flow", true, FlowSummary()))
            {
                EditorGUI.indentLevel++;
                SElementEditorHelper.DrawFlow(serializedObject, DrawsReceive);
                DrawFlowExtra();
                if (GUILayout.Button(new GUIContent("Edit connections in the Scene view (Shift+C)", "Opens the SimuLean Connect tool")))
                    SimuLeanConnectTool.Activate();
                EditorGUI.indentLevel--;
            }
            if (SimuLeanGUI.Section(Key("Resources"), "Resources", false, ResourcesSummary()))
            { EditorGUI.indentLevel++; SElementEditorHelper.DrawResourceFields(serializedObject); EditorGUI.indentLevel--; }
            // what reacts to this element, and + Reaction
            if (SimuLeanGUI.Section(Key("Reactions"), "Reactions", false, ReactionsSection.Summary(Element)))
            { EditorGUI.indentLevel++; ReactionsSection.Draw(Element); EditorGUI.indentLevel--; }
            if (HasVisual && SimuLeanGUI.Section(Key("Visual"), "Visual", false))
            { EditorGUI.indentLevel++; DrawVisual(); DrawConnectionSwitch(); EditorGUI.indentLevel--; }
            if (Application.isPlaying && SimuLeanGUI.Section(Key("Statistics"), "Statistics"))
            { EditorGUI.indentLevel++; DrawStatistics(); DrawPlayExtra(); EditorGUI.indentLevel--; }

            serializedObject.ApplyModifiedProperties();
        }

        // ── header and issues ────────────────────────────────────────────────

        // The element's look as its icon (review2 #47: every header showed the generic script icon): the prefab it was
        // placed from, else the Library's prefab or icon of its type; previews load over a few frames.
        private Texture HeaderIcon(SimuLeanElementAttribute info)
        {
            Object source = target is Component c ? PrefabUtility.GetCorrespondingObjectFromSource(c.gameObject) : null;
            if (source == null && !string.IsNullOrEmpty(info?.Prefab))
                source = AssetDatabase.LoadMainAssetAtPath("Assets/SimuLean.Unity/Prefabs/Elements/" + info.Prefab);
            if (source == null && !string.IsNullOrEmpty(info?.Icon)) source = AssetDatabase.LoadMainAssetAtPath(info.Icon);
            if (source is Texture t) return t;
            if (source != null)
            {
                var preview = AssetPreview.GetAssetPreview(source);
                if (preview != null) return preview;
                if (AssetPreview.IsLoadingAssetPreview(UnityCompat.Entity(source))) Repaint();
            }
            return AssetPreview.GetMiniThumbnail(target);
        }

        private void DrawElementHeader()
        {
            var info = target.GetType().GetCustomAttribute<SimuLeanElementAttribute>();
            string title = target.name;
            string subtitle = info != null ? $"{info.DisplayName} · {info.Module}" : target.GetType().Name;
            string badge = null;
            Color color = Color.gray;
            var e = Application.isPlaying ? Element.GetElement() : null;
            if (e != null)
            {
                string s = e.CurrentState.Name;
                badge = InspectorPalette.StateText(s);
                color = InspectorPalette.StateColor(s);
            }
            SimuLeanGUI.Header(title, subtitle, badge, color, HeaderIcon(info));
            if (!string.IsNullOrEmpty(info?.Description)) EditorGUILayout.LabelField(info.Description, EditorStyles.wordWrappedMiniLabel);

            var issues = new List<InspectorIssue>();
            SharedIssues(issues);
            CollectIssues(issues);
            SimuLeanGUI.Issues(issues);
        }

        private void SharedIssues(List<InspectorIssue> issues) => ElementChecks.Collect(Element, issues, DrawsReceive);

        private string FlowSummary()
        {
            var el = Element;
            int outs = 0;
            if (el.nextElements != null) foreach (var x in el.nextElements) if (x != null) outs++;
            string send = !el.HasOutput ? null : el.sendTo == SElement.SendMode.List ? "→ list" : $"→ {outs}";
            return send;
        }

        private string ResourcesSummary()
        {
            var el = Element;
            var parts = new List<string>();
            if (el is IProcessResourceUser && el.processBy?.team != null) parts.Add("process: " + el.processBy.team.name);
            if (el.transportBy?.team != null) parts.Add("transport: " + el.transportBy.team.name);
            return parts.Count > 0 ? string.Join(" · ", parts) : "none";
        }

        // ── shared helpers for element inspectors ────────────────────────────

        private static readonly HashSet<string> SharedFields = new HashSet<string>(SElementEditorHelper.ConnectionFields);

        /// <summary>
        /// Every serialized field of the element not drawn by the shared sections nor listed in <paramref name="skip"/>,
        /// split by kind: <paramref name="visual"/> = scene references (Transform, GameObject, Animator, materials…) and
        /// layout fields; the others are parameters. Used by the generic inspector and the elements with many fields.
        /// </summary>
        protected void DrawRemaining(bool visual, params string[] skip)
        {
            var it = serializedObject.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = false;
                if (SharedFields.Contains(it.name) || System.Array.IndexOf(skip, it.name) >= 0) continue;
                if (IsVisual(it) != visual) continue;
                SimuLeanGUI.Field(it.Copy());
            }
        }

        private static bool IsVisual(SerializedProperty p)
        {
            if (p.propertyType == SerializedPropertyType.ObjectReference)
            {
                string t = p.type;   // "PPtr<$Transform>"…
                return t.Contains("Transform") || t.Contains("GameObject") || t.Contains("Animator") || t.Contains("Material")
                       || t.Contains("Mesh") || t.Contains("Renderer");
            }
            string n = p.name.ToLowerInvariant();
            return n.Contains("separation") || n.Contains("spacing") || n.Contains("layout") || n.Contains("visual")
                   || n.Contains("color") || n.Contains("colour");
        }

        private void DrawConnectionSwitch()
        {
            var g = Element.GetComponent<SElementConnectionGizmo>();
            if (g == null) return;
            EditorGUI.BeginChangeCheck();
            bool on = EditorGUILayout.Toggle(new GUIContent("Draw connections", "Draw the connections that start here in the Scene view."), g.showConnections);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(g, "Draw connections");
                g.showConnections = on;
                EditorUtility.SetDirty(g);
            }
        }

        // ── user properties (labels) ─────────────────────────────────────────

        private void DrawProperties()
        {
            var go = Element.gameObject;
            if (go.GetComponent<SElementLabels>() != null) return;   // edited in its own component
            var r = EditorGUILayout.GetControlRect();
            r = EditorGUI.PrefixLabel(r, new GUIContent("Properties", "User properties (key = value) read by expressions, lists and routing."));
            if (GUI.Button(r, "+ Add properties")) Undo.AddComponent<SElementLabels>(go);
        }

        // ── statistics (Play) ────────────────────────────────────────────────

        private void DrawStatistics()
        {
            var e = Element.GetElement();
            var clock = UnitySimClock.Instance;
            if (e == null || clock == null || clock.clock == null) { EditorGUILayout.LabelField("Not running.", EditorStyles.miniLabel); return; }
            var ctx = new ElementInspectorContext { Clock = clock.clock, Owner = Element.gameObject, Now = clock.clock.GetSimulationTime() };
            var view = ElementInspectorReport.Build(e, ctx, withCharts: false);
            foreach (var row in view.Kpis)
            {
                if (row.Header) SimuLeanGUI.SubHeader(row.Label);
                else SimuLeanGUI.Row(row.Label, row.Value, row.Note, row.Swatch);
            }
        }
    }
}
