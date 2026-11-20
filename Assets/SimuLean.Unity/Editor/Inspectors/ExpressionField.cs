using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SimuLean.Expressions;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The check of an expression as it is entered, shared by the inspector drawer of every
    /// <see cref="SimuLeanExpressionAttribute"/> field, the list-field drawer, the reaction and optimiser editors and the
    /// agent commands (<c>simulean_set</c>, <c>simulean_apply</c>): the syntax and what the place serves
    /// (<see cref="Expression.Parse(string, ExpressionContext, string, string)"/>) are errors; an element named by a constant
    /// that the scene does not have is a warning (the build then fails on it). Item labels are not checked (they are set
    /// during the run).
    /// </summary>
    public static class ExpressionField
    {
        public enum Severity { None, Ok, Warning, Error }

        public readonly struct Result
        {
            public readonly Severity Severity; public readonly string Message;
            public Result(Severity s, string m) { Severity = s; Message = m; }
        }

        /// <summary>The check of <paramref name="text"/> written in the place <paramref name="contextKey"/> (a record
        /// <c>name = expression</c> when <paramref name="records"/>). An empty text is <see cref="Severity.None"/>.
        /// <paramref name="freshNames"/>: read the scene's element names now instead of the inspector's cache (an agent
        /// command, which may run after a scene was opened or an element renamed in the same editor frame).</summary>
        public static Result Check(string text, string contextKey, bool records = false, bool freshNames = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Result(Severity.None, null);
            var context = ExpressionContext.Of(contextKey) ?? ExpressionContext.Any;
            string expr = text.Trim();
            if (records)
            {
                int eq = expr.IndexOf('=');
                if (eq <= 0 || (eq + 1 < expr.Length && expr[eq + 1] == '='))
                    return new Result(Severity.Error, "Write 'name = expression' (quote a text: name = 'A').");
                string name = expr.Substring(0, eq).Trim();
                if (Expression.IsConstantName(name)) return new Result(Severity.Error, $"'{name}' is a constant (1 / 0), not a name.");
                expr = expr.Substring(eq + 1).Trim();
                if (expr.Length == 0) return new Result(Severity.Error, "Write 'name = expression' (the expression is empty).");
            }
            Expression parsed;
            try { parsed = Expression.Parse(expr, context); }
            catch (FormatException e) { return new Result(Severity.Error, e.Message); }
            if (freshNames) sceneNames = null;
            var names = SceneElementNames();
            var missing = parsed.NamedElements().Where(n => !names.Contains(n)).Distinct().ToList();
            if (missing.Count > 0)
                return new Result(Severity.Warning, string.Join(" ", missing.Select(m => $"No element '{m}' in the scene.{NameSuggestions(m, names)}")));
            return new Result(Severity.Ok, "Expression OK (" + context.Name + ").");
        }

        private static string NameSuggestions(string given, IEnumerable<string> names)
            => SimuLean.Serialization.NameSuggestions.DidYouMean(given, names);

        // the element names of the open scenes, kept for the inspector until the hierarchy or the open scenes change (the
        // hierarchy event comes on a later editor update: a scene opened or an element renamed by a command is not seen yet)
        private static HashSet<string> sceneNames;
        static ExpressionField()
        {
            EditorApplication.hierarchyChanged += () => sceneNames = null;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += (s, m) => sceneNames = null;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosed += s => sceneNames = null;
            UnityEditor.SceneManagement.EditorSceneManager.newSceneCreated += (s, setup, m) => sceneNames = null;
        }

        private static HashSet<string> SceneElementNames()
        {
            if (sceneNames != null) return sceneNames;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in UnityCompat.FindAll<SElement>()) if (e != null) set.Add(e.gameObject.name);
            return sceneNames = set;
        }

        /// <summary>The attribute of the field at <paramref name="propertyPath"/> of <paramref name="type"/> (an array element
        /// counts as its field), or null.</summary>
        public static SimuLeanExpressionAttribute AttributeOf(Type type, string propertyPath)
        {
            FieldInfo field = null;
            var t = type;
            var parts = propertyPath.Split('.');
            for (int i = 0; i < parts.Length && t != null; i++)
            {
                string part = parts[i];
                if (part == "Array" && i + 1 < parts.Length && parts[i + 1].StartsWith("data[", StringComparison.Ordinal))
                {
                    i++;
                    t = t.IsArray ? t.GetElementType() : t.IsGenericType ? t.GetGenericArguments()[0] : null;
                    continue;
                }
                field = FindField(t, part);
                if (field == null) return null;
                t = field.FieldType;
            }
            return field?.GetCustomAttribute<SimuLeanExpressionAttribute>();
        }

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>The texts of a string property or of each element of a list of strings.</summary>
        public static IEnumerable<string> Texts(SerializedProperty p)
        {
            if (p.propertyType == SerializedPropertyType.String) { yield return p.stringValue; yield break; }
            if (p.isArray)
                for (int i = 0; i < p.arraySize; i++)
                {
                    var e = p.GetArrayElementAtIndex(i);
                    if (e.propertyType == SerializedPropertyType.String) yield return e.stringValue;
                }
        }

        // ── drawing ──────────────────────────────────────────────────────────

        private static float Line => EditorGUIUtility.singleLineHeight;

        /// <summary>Height of the status line under the field (0 when the text is empty).</summary>
        public static float StatusHeight(Result r, float width)
        {
            if (r.Severity == Severity.None) return 0;
            return Mathf.Max(Line, Style(r.Severity).CalcHeight(new GUIContent(Prefix(r.Severity) + r.Message), Mathf.Max(60, width)));
        }

        /// <summary>Draws the status (green check, yellow warning or red error) in <paramref name="rect"/>.</summary>
        public static void DrawStatus(Rect rect, Result r)
        {
            if (r.Severity == Severity.None) return;
            GUI.Label(rect, new GUIContent(Prefix(r.Severity) + r.Message, r.Message), Style(r.Severity));
        }

        /// <summary>Layout version (custom editors).</summary>
        public static void LayoutStatus(Result r)
        {
            if (r.Severity == Severity.None) return;
            var rect = EditorGUILayout.GetControlRect(false, StatusHeight(r, EditorGUIUtility.currentViewWidth - 40));
            rect = EditorGUI.IndentedRect(rect);
            DrawStatus(rect, r);
        }

        private static string Prefix(Severity s) => s == Severity.Ok ? "✓ " : s == Severity.Warning ? "⚠ " : "✗ ";

        private static readonly Dictionary<Severity, GUIStyle> styles = new Dictionary<Severity, GUIStyle>();

        private static GUIStyle Style(Severity s)
        {
            if (styles.TryGetValue(s, out var st) && st != null) return st;
            st = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            var c = s == Severity.Ok ? new Color(0.25f, 0.65f, 0.3f) : s == Severity.Warning ? new Color(0.85f, 0.6f, 0.1f) : new Color(0.9f, 0.3f, 0.25f);
            st.normal.textColor = c;
            st.hover.textColor = c;
            return styles[s] = st;
        }
    }

    /// <summary>The inspector of every <see cref="SimuLeanExpressionAttribute"/> field: the text, then its check.</summary>
    [CustomPropertyDrawer(typeof(SimuLeanExpressionAttribute))]
    public class SimuLeanExpressionDrawer : PropertyDrawer
    {
        private SimuLeanExpressionAttribute Attr => (SimuLeanExpressionAttribute)attribute;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float h = EditorGUIUtility.singleLineHeight;
            if (property.propertyType != SerializedPropertyType.String) return EditorGUI.GetPropertyHeight(property, label, true);
            var r = ExpressionField.Check(property.stringValue, Attr.Context, Attr.Records);
            float s = ExpressionField.StatusHeight(r, EditorGUIUtility.currentViewWidth - 60);
            return s > 0 ? h + EditorGUIUtility.standardVerticalSpacing + s : h;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String) { EditorGUI.PropertyField(position, property, label, true); return; }
            float line = EditorGUIUtility.singleLineHeight;
            EditorGUI.BeginProperty(position, label, property);
            var field = new Rect(position.x, position.y, position.width, line);
            EditorGUI.BeginChangeCheck();
            string text = EditorGUI.TextField(field, label, property.stringValue);
            if (EditorGUI.EndChangeCheck()) property.stringValue = text;
            var r = ExpressionField.Check(property.stringValue, Attr.Context, Attr.Records);
            if (r.Severity != ExpressionField.Severity.None)
            {
                float y = field.yMax + EditorGUIUtility.standardVerticalSpacing;
                var status = EditorGUI.IndentedRect(new Rect(position.x, y, position.width, position.yMax - y));
                if (label != GUIContent.none && !string.IsNullOrEmpty(label.text))
                {
                    status.xMin = position.x + EditorGUIUtility.labelWidth;
                }
                ExpressionField.DrawStatus(status, r);
            }
            EditorGUI.EndProperty();
        }
    }
}
