using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Routing of an element's items: the rule on one line (its description as the
    /// tooltip), then only the field the rule needs; an expression shows its error when it has one.
    /// </summary>
    [CustomPropertyDrawer(typeof(OutputStrategyConfig))]
    public class OutputStrategyConfigDrawer : PropertyDrawer
    {
        private static float Line => EditorGUIUtility.singleLineHeight;
        private static float Gap => EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var typeProp = property.FindPropertyRelative("strategyType");
            var type = (OutputStrategyType)typeProp.enumValueIndex;
            var r = new Rect(position.x, position.y, position.width, Line);
            EditorGUI.PropertyField(r, typeProp, new GUIContent(label.text, Description(type)));

            EditorGUI.indentLevel++;
            if (type == OutputStrategyType.Parameterized)
            {
                r.y += Line + Gap;
                EditorGUI.PropertyField(r, property.FindPropertyRelative("parameterKey"),
                    new GUIContent("Parameter key", "Model parameter (SimContext.ModelParameters) holding the output index."));
            }
            else if (type == OutputStrategyType.Probability)
            {
                var w = property.FindPropertyRelative("weights");
                r.y += Line + Gap;
                r.height = EditorGUI.GetPropertyHeight(w, true);
                EditorGUI.PropertyField(r, w, new GUIContent("Weights", "The weight of each next element, in the order of Next Elements."), true);
            }
            else if (type == OutputStrategyType.Expression)
            {
                var expr = property.FindPropertyRelative("routingExpression");
                r.y += Line + Gap;
                EditorGUI.PropertyField(r, expr, new GUIContent("Expression", "Output index (0 = first) from the item and the element, e.g. item.color == 'red' ? 0 : 1"));
                string err = Error(expr.stringValue);
                if (err != null) { r.y += Line + Gap; r.height = Line * 2; EditorGUI.HelpBox(r, err, MessageType.Error); }
            }
            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var type = (OutputStrategyType)property.FindPropertyRelative("strategyType").enumValueIndex;
            float h = Line;
            if (type == OutputStrategyType.Parameterized) h += Line + Gap;
            if (type == OutputStrategyType.Probability) h += Gap + EditorGUI.GetPropertyHeight(property.FindPropertyRelative("weights"), true);
            if (type == OutputStrategyType.Expression)
            {
                h += Line + Gap;
                if (Error(property.FindPropertyRelative("routingExpression").stringValue) != null) h += Line * 2 + Gap;
            }
            return h;
        }

        private static string Error(string expr) => string.IsNullOrWhiteSpace(expr) ? null : SimuLean.Expressions.Expression.Check(expr);

        public static string Description(OutputStrategyType type)
        {
            switch (type)
            {
                case OutputStrategyType.FirstAvailable: return "The first next element that can take the item.";
                case OutputStrategyType.ShortestQueue: return "The next element with the shortest queue (balances the load).";
                case OutputStrategyType.RoundRobin: return "The next elements in turn, whatever their queues.";
                case OutputStrategyType.MostAvailableCapacity: return "The next element with the most free capacity.";
                case OutputStrategyType.PriorityBased: return "High-priority items to the first available, the others to the shortest queue.";
                case OutputStrategyType.Parameterized: return "The output index held by a model parameter.";
                case OutputStrategyType.Expression: return "The output index given by an expression over the item and the element.";
                case OutputStrategyType.Probability: return "Each item to a next element drawn by its weight (a percentage); a busy choice is waited for.";
                default: return "";
            }
        }
    }
}
