using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Admission of an element: the rule on one line (its description as the tooltip),
    /// then only the fields the rule needs; a condition shows its error when it has one.
    /// </summary>
    [CustomPropertyDrawer(typeof(InputStrategyConfig))]
    public class InputStrategyConfigDrawer : PropertyDrawer
    {
        private static float Line => EditorGUIUtility.singleLineHeight;
        private static float Gap => EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var typeProp = property.FindPropertyRelative("strategyType");
            var type = (InputStrategyType)typeProp.enumValueIndex;
            var r = new Rect(position.x, position.y, position.width, Line);
            EditorGUI.PropertyField(r, typeProp, new GUIContent(label.text, Description(type)));

            EditorGUI.indentLevel++;
            switch (type)
            {
                case InputStrategyType.SingleLabel:
                    r.y += Line + Gap;
                    EditorGUI.PropertyField(r, property.FindPropertyRelative("singleLabelName"), new GUIContent("Label"));
                    r.y += Line + Gap;
                    EditorGUI.PropertyField(r, property.FindPropertyRelative("singleLabelValue"),
                        new GUIContent("Value", "Empty = the value of the first item admitted."));
                    break;
                case InputStrategyType.MultiLabel:
                {
                    var pairs = property.FindPropertyRelative("multiLabelPairs");
                    r.y += Line + Gap;
                    r.height = EditorGUI.GetPropertyHeight(pairs, true);
                    EditorGUI.PropertyField(r, pairs, new GUIContent("Label = value", "Admitted when ANY pair matches (e.g. color=red, size=10)."), true);
                    break;
                }
                case InputStrategyType.Condition:
                {
                    var cond = property.FindPropertyRelative("condition");
                    r.y += Line + Gap;
                    EditorGUI.PropertyField(r, cond, new GUIContent("Condition",
                        "Admits the item when it holds, e.g. item.color == 'red' && item.weight <= element.maxWeight"));
                    string err = Error(cond.stringValue);
                    if (err != null) { r.y += Line + Gap; r.height = Line * 2; EditorGUI.HelpBox(r, err, MessageType.Error); }
                    break;
                }
            }
            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var type = (InputStrategyType)property.FindPropertyRelative("strategyType").enumValueIndex;
            switch (type)
            {
                case InputStrategyType.SingleLabel: return Line * 3 + Gap * 2;
                case InputStrategyType.MultiLabel: return Line + Gap + EditorGUI.GetPropertyHeight(property.FindPropertyRelative("multiLabelPairs"), true);
                case InputStrategyType.Condition:
                    return Line * 2 + Gap + (Error(property.FindPropertyRelative("condition").stringValue) != null ? Line * 2 + Gap : 0);
                default: return Line;
            }
        }

        private static string Error(string expr) => string.IsNullOrWhiteSpace(expr) ? null : SimuLean.Expressions.Expression.Check(expr);

        public static string Description(InputStrategyType type)
        {
            switch (type)
            {
                case InputStrategyType.Default: return "Admits every item.";
                case InputStrategyType.SingleLabel: return "Admits the items with a label value.";
                case InputStrategyType.MultiLabel: return "Admits the items matching any of the label = value pairs.";
                case InputStrategyType.Condition: return "Admits the items for which a condition holds.";
                default: return "";
            }
        }
    }
}
