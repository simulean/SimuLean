using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SimuLean.Expressions;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Every expression field of the Unity layer carries <see cref="SimuLeanExpressionAttribute"/>
    /// (so the shared drawer checks it as it is typed, and simulean_set / simulean_apply refuse a text that does not parse),
    /// with a context the engine knows; the check itself.
    /// </summary>
    public class ExpressionFieldTests
    {
        // the names expression fields go by; any serialized text field of a SimuLean type with one of them must be marked
        private static readonly HashSet<string> ExpressionNames = new HashSet<string>
        {
            "expression", "routingExpression", "condition", "priority", "maxWait", "pullMaxWait", "entryLabels", "exitLabels",
            "tallies", "createLabels", "noShow", "cancel", "applies", "filter", "emptiesFilter", "onlyIf", "fitness",
        };

        private static IEnumerable<FieldInfo> SerializedTextFields()
        {
            var asm = typeof(SElement).Assembly;
            foreach (var t in asm.GetTypes())
            {
                if (t.Namespace == null || !t.Namespace.StartsWith("SimuLean", StringComparison.Ordinal)) continue;
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType != typeof(string) && f.FieldType != typeof(List<string>)) continue;
                    if (!f.IsPublic && f.GetCustomAttribute<SerializeField>() == null) continue;
                    if (f.GetCustomAttribute<NonSerializedAttribute>() != null) continue;
                    yield return f;
                }
            }
        }

        [Test]
        public void EveryExpressionFieldUsesTheExpressionDrawer()
        {
            var marked = SerializedTextFields().Where(f => f.GetCustomAttribute<SimuLeanExpressionAttribute>() != null).ToList();
            var unmarked = SerializedTextFields()
                .Where(f => ExpressionNames.Contains(f.Name) && f.GetCustomAttribute<SimuLeanExpressionAttribute>() == null)
                .Select(f => f.DeclaringType.Name + "." + f.Name).ToList();
            Assert.IsEmpty(unmarked, "expression fields without [SimuLeanExpression]: " + string.Join(", ", unmarked));
            Assert.GreaterOrEqual(marked.Count, 20, "the ~20 expression fields are marked: " + string.Join(", ", marked.Select(f => f.Name)));
            foreach (var f in marked)
                Assert.IsNotNull(ExpressionContext.Of(f.GetCustomAttribute<SimuLeanExpressionAttribute>().Context),
                                 $"{f.DeclaringType.Name}.{f.Name}: unknown context");
            // the reaction value and the team member properties are expression fields too (their names are too common to scan)
            Assert.IsNotNull(ExpressionField.AttributeOf(typeof(ResourceMemberSpec), "properties.Array.data[0]"), "member properties");
            Assert.IsNotNull(ExpressionField.AttributeOf(typeof(UnityMultiServer), "outputStrategy.routingExpression"), "a nested field is found by its path");
        }

        [Test]
        public void TheCheckSaysWhatIsWrongAsItIsTyped()
        {
            Assert.AreEqual(ExpressionField.Severity.None, ExpressionField.Check("", "time").Severity);
            Assert.AreEqual(ExpressionField.Severity.Ok, ExpressionField.Check("item.weight * 2 + 1", "time").Severity);
            var syntax = ExpressionField.Check("item.weight *", "time");
            Assert.AreEqual(ExpressionField.Severity.Error, syntax.Severity);
            StringAssert.Contains("position", syntax.Message);
            var root = ExpressionField.Check("signal.time > 1", "routing");
            Assert.AreEqual(ExpressionField.Severity.Error, root.Severity, root.Message);
            StringAssert.Contains("not available in a routing expression", root.Message);
            var fn = ExpressionField.Check("lg(2)", "time");
            StringAssert.Contains("Unknown function 'lg'", fn.Message);
            var element = ExpressionField.Check("content('NoSuchElement_ExpressionFieldTests') > 2 ? 1 : 0", "routing");
            Assert.AreEqual(ExpressionField.Severity.Warning, element.Severity, element.Message);
            Assert.AreEqual(ExpressionField.Severity.Error, ExpressionField.Check("reps + 1", "label", records: true).Severity, "a record needs a name");
            Assert.AreEqual(ExpressionField.Severity.Ok, ExpressionField.Check("reps = (reps ?? 0) + 1", "label", records: true).Severity);
            Assert.AreEqual(ExpressionField.Severity.Error, ExpressionField.Check("rand() > 1", "fitness").Severity, "no random function in a fitness");
        }
    }
}
