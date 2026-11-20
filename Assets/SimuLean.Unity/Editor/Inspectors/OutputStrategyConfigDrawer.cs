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
                var content = new GUIContent("Expression", "Output index (0 = first) from the item and the element, e.g. item.color == 'red' ? 0 : 1");
                r.height = EditorGUI.GetPropertyHeight(expr, content);
                EditorGUI.PropertyField(r, expr, content);
            }
            else if (type == OutputStrategyType.BestScore)
            {
                var expr = property.FindPropertyRelative("scoreExpression");
                r.y += Line + Gap;
                var content = new GUIContent("Score", "The score of each next element (out.x = the next element, item.x = the item), e.g. out.speed - out.content; the highest wins, ties by the order of Next Elements.");
                r.height = EditorGUI.GetPropertyHeight(expr, content);
                EditorGUI.PropertyField(r, expr, content);
                r.y += r.height + Gap;
                r.height = Line;
                EditorGUI.PropertyField(r, property.FindPropertyRelative("whenBestFull"),
                    new GUIContent("When the best is full", "Skip: score only the next elements with room. Wait: the item waits for the best one."));
            }
            else if (type == OutputStrategyType.Rule)
            {
                r.y += Line + Gap;
                r.height = Line;
                EditorGUI.PropertyField(r, property.FindPropertyRelative("rule"), new GUIContent("Rule", property.FindPropertyRelative("rule").tooltip));
                r.y += Line + Gap;
                EditorGUI.PropertyField(r, property.FindPropertyRelative("whenBestFull"),
                    new GUIContent("When the best is full", "Skip: score only the next elements with room. Wait: the item waits for the best one."));
                r.y += Line + Gap;
                var ps = property.FindPropertyRelative("ruleParameters");
                r.height = EditorGUI.GetPropertyHeight(ps, true);
                EditorGUI.PropertyField(r, ps, new GUIContent("Rule parameters", ps.tooltip), true);
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
                h += Gap + EditorGUI.GetPropertyHeight(property.FindPropertyRelative("routingExpression"), new GUIContent("Expression"));
            }
            if (type == OutputStrategyType.Rule)
                h += 2 * (Line + Gap) + Gap + EditorGUI.GetPropertyHeight(property.FindPropertyRelative("ruleParameters"), true);
            if (type == OutputStrategyType.BestScore)
                h += Gap + EditorGUI.GetPropertyHeight(property.FindPropertyRelative("scoreExpression"), new GUIContent("Score")) + Gap + Line;
            return h;
        }


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
                case OutputStrategyType.BestScore: return "The next element with the highest score (an expression per next element); ties to the first in order.";
                case OutputStrategyType.Rule: return "A choice rule (C#, Assets/SimuLean.UserLogic) scores each next element; the highest wins, ties to the first in order.";
                default: return "";
            }
        }
    }
}
