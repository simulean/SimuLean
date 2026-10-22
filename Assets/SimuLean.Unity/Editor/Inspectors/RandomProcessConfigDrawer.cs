using System;
using UnityEditor;
using UnityEngine;
using SimuLean.Unity.Editor;

namespace SimuLean.Unity
{
    /// <summary>
    /// A distribution in two lines: the label, the type and its mean on the first; the
    /// parameters of that type side by side on the second (rounded display). Every element's sampler uses it.
    /// </summary>
    [CustomPropertyDrawer(typeof(RandomProcessConfig))]
    public class RandomProcessConfigDrawer : PropertyDrawer
    {
        private static readonly (string field, string label)[] None = new (string, string)[0];

        /// <summary>The parameters of each distribution, with their short labels.</summary>
        public static (string field, string label)[] Parameters(RandomProcessType t)
        {
            switch (t)
            {
                case RandomProcessType.Constant: return new[] { ("constantValue", "value") };
                case RandomProcessType.Uniform: return new[] { ("uniformMin", "min"), ("uniformMax", "max") };
                case RandomProcessType.Normal: return new[] { ("normalMean", "mean"), ("normalStdDev", "std dev") };
                case RandomProcessType.Exponential: return new[] { ("exponentialRate", "rate") };
                case RandomProcessType.Triangular: return new[] { ("triangularMin", "min"), ("triangularMode", "mode"), ("triangularMax", "max") };
                case RandomProcessType.Gamma: return new[] { ("gammaShape", "shape"), ("gammaScale", "scale") };
                case RandomProcessType.Weibull: return new[] { ("weibullShape", "shape"), ("weibullScale", "scale") };
                case RandomProcessType.LogNormal: return new[] { ("logNormalMu", "mu"), ("logNormalSigma", "sigma") };
                case RandomProcessType.Beta: return new[] { ("betaAlpha", "alpha"), ("betaBeta", "beta"), ("betaMin", "min"), ("betaMax", "max") };
                case RandomProcessType.ChiSquare: return new[] { ("chiSquareDf", "df") };
                case RandomProcessType.StudentT: return new[] { ("studentTDf", "df"), ("studentTClamp", "≥ 0") };
                case RandomProcessType.FDistribution: return new[] { ("fDistD1", "d1"), ("fDistD2", "d2") };
                case RandomProcessType.Poisson: return new[] { ("poissonLambda", "lambda") };
                case RandomProcessType.Binomial: return new[] { ("binomialN", "n"), ("binomialP", "p") };
                case RandomProcessType.DiscreteUniform: return new[] { ("discreteUniformMin", "min"), ("discreteUniformMax", "max") };
                default: return None;
            }
        }

        /// <summary>The mean of the configured distribution (NaN when it has none or depends on the item).</summary>
        public static double Mean(SerializedProperty p, RandomProcessType t)
        {
            double D(string f) { var q = p.FindPropertyRelative(f); return q == null ? double.NaN : q.propertyType == SerializedPropertyType.Integer ? q.intValue : q.doubleValue; }
            switch (t)
            {
                case RandomProcessType.Constant: return D("constantValue");
                case RandomProcessType.Uniform: return (D("uniformMin") + D("uniformMax")) / 2;
                case RandomProcessType.Normal: return D("normalMean");
                case RandomProcessType.Exponential: return D("exponentialRate") > 0 ? 1 / D("exponentialRate") : double.NaN;
                case RandomProcessType.Triangular: return (D("triangularMin") + D("triangularMode") + D("triangularMax")) / 3;
                case RandomProcessType.Gamma: return D("gammaShape") * D("gammaScale");
                case RandomProcessType.Weibull: return D("weibullShape") > 0 ? D("weibullScale") * SimuLean.Stochastics.Probability.Gamma(1 + 1 / D("weibullShape")) : double.NaN;
                case RandomProcessType.LogNormal: return Math.Exp(D("logNormalMu") + D("logNormalSigma") * D("logNormalSigma") / 2);
                case RandomProcessType.Beta:
                    double a = D("betaAlpha"), b = D("betaBeta");
                    return D("betaMin") + (D("betaMax") - D("betaMin")) * a / (a + b);
                case RandomProcessType.ChiSquare: return D("chiSquareDf");
                case RandomProcessType.FDistribution: return D("fDistD2") > 2 ? D("fDistD2") / (D("fDistD2") - 2) : double.NaN;
                case RandomProcessType.Poisson: return D("poissonLambda");
                case RandomProcessType.Binomial: return D("binomialN") * D("binomialP");
                case RandomProcessType.DiscreteUniform: return (D("discreteUniformMin") + D("discreteUniformMax")) / 2;
                default: return double.NaN;
            }
        }

        private static float Line => EditorGUIUtility.singleLineHeight;
        private static float Gap => EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var typeProp = property.FindPropertyRelative("strategyType");
            var type = (RandomProcessType)typeProp.enumValueIndex;

            // line 1: label | type | mean
            var r = new Rect(position.x, position.y, position.width, Line);
            var field = EditorGUI.PrefixLabel(r, label);
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            double mean = Mean(property, type);
            float meanWidth = double.IsNaN(mean) ? 0 : Mathf.Min(110, field.width * 0.4f);
            EditorGUI.PropertyField(new Rect(field.x, field.y, field.width - meanWidth - 4, Line), typeProp, GUIContent.none);
            if (meanWidth > 0)
                GUI.Label(new Rect(field.xMax - meanWidth, field.y, meanWidth, Line), "mean " + SimuLeanGUI.Format(mean) + " " + SimuLeanGUI.TimeUnit, EditorStyles.miniLabel);

            // line 2: the parameters side by side (or the expression)
            var row = new Rect(field.x, position.y + Line + Gap, field.width, Line);
            if (type == RandomProcessType.Expression)
            {
                var expr = property.FindPropertyRelative("expression");
                row.height = Line * 2;
                expr.stringValue = EditorGUI.TextArea(row, expr.stringValue);
            }
            else
            {
                var ps = Parameters(type);
                if (ps.Length > 0)
                {
                    float w = row.width / ps.Length;
                    float lw = EditorGUIUtility.labelWidth;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var q = property.FindPropertyRelative(ps[i].field);
                        if (q == null) continue;
                        var cell = new Rect(row.x + i * w, row.y, w - 4, Line);
                        EditorGUIUtility.labelWidth = Mathf.Min(48, w * 0.45f);
                        var content = new GUIContent(ps[i].label, q.tooltip);
                        if (q.propertyType == SerializedPropertyType.Float) SimuLeanGUI.Number(cell, q, content);
                        else EditorGUI.PropertyField(cell, q, content);
                    }
                    EditorGUIUtility.labelWidth = lw;
                }
            }
            EditorGUI.indentLevel = indent;
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var type = (RandomProcessType)property.FindPropertyRelative("strategyType").enumValueIndex;
            if (type == RandomProcessType.Expression) return Line + Gap + Line * 2;
            return Parameters(type).Length > 0 ? Line * 2 + Gap : Line;
        }
    }
}
