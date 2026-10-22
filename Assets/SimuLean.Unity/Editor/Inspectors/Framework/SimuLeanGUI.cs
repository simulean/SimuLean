using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>A problem an inspector shows in its header: what is wrong, and optionally a button that fixes it.</summary>
    public sealed class InspectorIssue
    {
        public MessageType Severity;
        public string Text;
        public string FixLabel;
        public Action Fix;

        public InspectorIssue(MessageType severity, string text, string fixLabel = null, Action fix = null)
        {
            Severity = severity; Text = text; FixLabel = fixLabel; Fix = fix;
        }
    }

    /// <summary>
    /// The shared pieces of the SimuLean inspectors: section headers, number fields that
    /// show rounded values, issue boxes, read-only rows. One look for every element.
    /// </summary>
    public static class SimuLeanGUI
    {
        private static GUIStyle sectionStyle, rowValueStyle, badgeStyle, titleStyle;

        /// <summary>The model's time unit (the scene clock's <i>Seconds per Time Unit</i>: s, min, h, d): shown next to
        /// every time an inspector edits (review2 #38).</summary>
        public static string TimeUnit
        {
            get
            {
                var clock = UnitySimClock.Instance;
                return SimuLean.Results.ResultMetrics.TimeUnitName(clock != null && clock.TimeUnitSeconds > 0 ? clock.TimeUnitSeconds : 1.0);
            }
        }

        // ── sections ─────────────────────────────────────────────────────────

        /// <summary>A collapsible section (open state remembered per <paramref name="key"/> for the session).</summary>
        public static bool Section(string key, string title, bool defaultOpen = true, string note = null)
        {
            if (sectionStyle == null)
                sectionStyle = new GUIStyle(EditorStyles.foldoutHeader) { fontStyle = FontStyle.Bold, fixedHeight = 22 };
            EditorGUILayout.Space(4);
            bool open = SessionState.GetBool("SimuLean.Section." + key, defaultOpen);
            var content = new GUIContent(note != null ? $"{title}   <size=10><color=#888>{note}</color></size>" : title);
            sectionStyle.richText = true;
            bool now = EditorGUILayout.BeginFoldoutHeaderGroup(open, content, sectionStyle);
            EditorGUILayout.EndFoldoutHeaderGroup();
            if (now != open) SessionState.SetBool("SimuLean.Section." + key, now);
            return now;
        }

        // ── numbers ──────────────────────────────────────────────────────────

        /// <summary>The text a number is shown with: at most 6 significant decimals, no float noise (2.0999999642 → 2.1).</summary>
        public static string Format(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return v.ToString(CultureInfo.InvariantCulture);
            double r = Math.Round(v, 6);
            return r.ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>A double field that shows the rounded value and keeps the typed one (delayed: applied on Enter / focus out).</summary>
        public static double Number(Rect rect, GUIContent label, double value)
        {
            string shown = Format(value);
            string typed = EditorGUI.DelayedTextField(rect, label, shown);
            if (typed == shown) return value;
            return double.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : value;
        }

        public static void Number(Rect rect, SerializedProperty p, GUIContent label)
        {
            EditorGUI.BeginProperty(rect, label, p);
            EditorGUI.BeginChangeCheck();
            double v = Number(rect, label, p.doubleValue);
            if (EditorGUI.EndChangeCheck()) p.doubleValue = v;
            EditorGUI.EndProperty();
        }

        public static void Number(SerializedProperty p, GUIContent label = null)
            => Number(EditorGUILayout.GetControlRect(), p, label ?? new GUIContent(p.displayName, p.tooltip));

        /// <summary>Any property, doubles through <see cref="Number(SerializedProperty, GUIContent)"/>.</summary>
        public static void Field(SerializedProperty p, GUIContent label = null, bool children = true)
        {
            if (p == null) return;
            if (p.propertyType == SerializedPropertyType.Float && p.type == "double") Number(p, label);
            else EditorGUILayout.PropertyField(p, label ?? new GUIContent(p.displayName, p.tooltip), children);
        }

        // ── header, issues, rows ─────────────────────────────────────────────

        public static void Header(string title, string subtitle, string badge, Color badgeColor, Texture icon)
        {
            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
                badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            }
            var r = EditorGUILayout.GetControlRect(false, 38);
            EditorGUI.DrawRect(new Rect(r.x - 2, r.y, r.width + 4, r.height), EditorGUIUtility.isProSkin ? new Color(0.17f, 0.19f, 0.22f) : new Color(0.82f, 0.84f, 0.87f));
            float x = r.x + 4;
            if (icon != null) { GUI.DrawTexture(new Rect(x, r.y + 5, 28, 28), icon, ScaleMode.ScaleToFit); x += 34; }
            GUI.Label(new Rect(x, r.y + 2, r.width - (x - r.x) - 110, 20), title, titleStyle);
            GUI.Label(new Rect(x, r.y + 20, r.width - (x - r.x) - 110, 16), subtitle, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(badge))
            {
                var b = new Rect(r.xMax - 104, r.y + 10, 100, 18);
                EditorGUI.DrawRect(b, badgeColor);
                GUI.Label(b, badge, badgeStyle);
            }
        }

        /// <summary>The inspector standard's header for a SimuLean tool (clock, experimenter, optimizer…): its name, its Library
        /// entry and description (review2 #41).</summary>
        public static void ToolHeader(Component tool, string status = null)
        {
            var info = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<SimuLeanToolAttribute>(tool.GetType());
            string subtitle = info != null ? $"{info.DisplayName} · {info.Category}" : ObjectNames.NicifyVariableName(tool.GetType().Name);
            Header(tool.name, subtitle, status, new Color(0.25f, 0.45f, 0.65f), AssetPreview.GetMiniThumbnail(tool));
            if (!string.IsNullOrEmpty(info?.Description)) EditorGUILayout.LabelField(info.Description, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(2);
        }

        public static void Issues(List<InspectorIssue> issues)
        {
            foreach (var i in issues)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(i.Text, i.Severity);
                if (i.Fix != null && GUILayout.Button(i.FixLabel ?? "Fix", GUILayout.Width(110), GUILayout.Height(38))) i.Fix();
                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>A read-only row: label, value, optional note and colour swatch (statistics).</summary>
        public static void Row(string label, string value, string note = null, Color? swatch = null)
        {
            if (rowValueStyle == null) rowValueStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
            var r = EditorGUILayout.GetControlRect();
            float lw = EditorGUIUtility.labelWidth;
            if (swatch.HasValue) EditorGUI.DrawRect(new Rect(r.x, r.y + 5, 8, 8), swatch.Value);
            GUI.Label(new Rect(r.x + 12, r.y, lw - 12, r.height), label);
            GUI.Label(new Rect(r.x + lw, r.y, (r.width - lw) * 0.5f, r.height), value, rowValueStyle);
            if (!string.IsNullOrEmpty(note)) GUI.Label(new Rect(r.x + lw + (r.width - lw) * 0.5f, r.y, (r.width - lw) * 0.5f, r.height), note, EditorStyles.miniLabel);
        }

        public static void SubHeader(string text)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel);
        }
    }
}
