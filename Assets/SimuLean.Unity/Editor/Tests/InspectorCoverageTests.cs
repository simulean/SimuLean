using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Every serialized field of a model element is in its inspector (<c>reserveExit</c> and older
    /// fields never reached the Multi-Server and Combiner inspectors). An element inspector draws its own fields
    /// (<c>DrawnFields</c>), hides some on purpose (<c>HiddenFields</c>), and the base draws the shared sections and every
    /// remaining field. This test makes the custom inspectors decide each field: a declared name must be a serialized
    /// field, and a field neither declared nor shared fails (it would only show among the remaining ones, out of place).
    /// </summary>
    public class InspectorCoverageTests
    {
        // fields every element has that no section draws on purpose: they show among the remaining fields of every inspector
        private static readonly HashSet<string> CommonRemaining = new HashSet<string> { "workNodes", "walkBy" };

        [Test]
        public void EverySerializedFieldIsDrawnOrExplicitlyHidden()
        {
            var problems = new List<string>();
            var shared = new HashSet<string>(SElementInspector.SharedFieldNames);
            foreach (var type in TypeCache.GetTypesDerivedFrom<SElement>())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
                var go = new GameObject("InspectorCoverage") { hideFlags = HideFlags.HideAndDontSave };
                UnityEditor.Editor editor = null;
                try
                {
                    var component = go.AddComponent(type);
                    if (component == null) continue;
                    editor = UnityEditor.Editor.CreateEditor(component);
                    if (!(editor is SElementInspector inspector))
                    {
                        problems.Add($"{type.Name}: its inspector is not an SElementInspector ({editor?.GetType().Name})");
                        continue;
                    }
                    var covered = new HashSet<string>(inspector.CoveredFields);
                    if (covered.Count == 0) continue;   // declares nothing: every field among the remaining ones (the generic inspector)
                    var fields = new HashSet<string>();
                    var it = new SerializedObject(component).GetIterator();
                    for (bool enter = true; it.NextVisible(enter); enter = false) fields.Add(it.name);
                    foreach (string f in covered)
                        if (!fields.Contains(f)) problems.Add($"{type.Name}: '{f}' is declared drawn / hidden but is not a serialized field");
                    foreach (string f in fields)
                        if (!shared.Contains(f) && !covered.Contains(f) && !CommonRemaining.Contains(f))
                            problems.Add($"{type.Name}: '{f}' is neither drawn by its inspector nor listed in its HiddenFields");
                }
                finally
                {
                    if (editor != null) Object.DestroyImmediate(editor);
                    Object.DestroyImmediate(go);
                }
            }
            Assert.IsEmpty(problems, string.Join("\n", problems));
        }
    }
}
