using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The SimuLean Library is generated from the registry attributes: keep it complete.</summary>
    public class LibraryRegistryTests
    {
        [Test]
        public void EveryModelElementIsInTheLibrary()
        {
            var missing = TypeCache.GetTypesDerivedFrom<SElement>()
                .Where(t => !t.IsAbstract && !Attribute.IsDefined(t, typeof(SimuLeanElementAttribute), false))
                .Select(t => t.FullName).OrderBy(n => n).ToList();
            Assert.IsEmpty(missing, "SElement types without [SimuLeanElement]: " + string.Join(", ", missing));
        }

        [Test]
        public void RegisteredEntriesAreUsable()
        {
            foreach (var t in TypeCache.GetTypesWithAttribute<SimuLeanElementAttribute>())
            {
                var a = (SimuLeanElementAttribute)Attribute.GetCustomAttribute(t, typeof(SimuLeanElementAttribute));
                Assert.IsTrue(typeof(SElement).IsAssignableFrom(t), $"{t.Name} is registered as an element but is not an SElement");
                Assert.Contains(a.Module, SimuLeanModules.Order, $"{t.Name}: unknown module '{a.Module}'");
                if (!string.IsNullOrEmpty(a.Prefab))
                    Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SimuLean.Unity/Prefabs/Elements/" + a.Prefab),
                                     $"{t.Name}: prefab '{a.Prefab}' not found");
            }
            foreach (var t in TypeCache.GetTypesWithAttribute<SimuLeanToolAttribute>())
            {
                var a = (SimuLeanToolAttribute)Attribute.GetCustomAttribute(t, typeof(SimuLeanToolAttribute));
                Assert.IsTrue(typeof(Component).IsAssignableFrom(t), $"{t.Name} is registered as a tool but is not a Component");
                Assert.IsFalse(typeof(SElement).IsAssignableFrom(t), $"{t.Name} is a model element, register it with [SimuLeanElement]");
                Assert.Contains(a.Category, SimuLeanToolCategories.Order, $"{t.Name}: unknown category '{a.Category}'");
            }
        }
    }
}
