using NUnit.Framework;
using SimuLean.Headless;
using SimuLean.Testing;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// In a real deadlock through a list, a Combiner holding its main item waits for its components — from
    /// the list its component input pulls from, not from the elements after it — and the element pushing to the list
    /// waits for the list's pullers. Minimal model: the yard as a list (capacity 1), two Combiners with SingleLabel.
    /// </summary>
    public class DeadlockAttributionTests
    {
        [Test]
        public void ACombinerWaitsForItsComponentListAndTheYardForItsPullers()
        {
            var clock = new SimClock(1);
            var model = new HeadlessModelFactory(clock);
            model.BuildModel(ReferenceModels.YardListDeadlock());
            model.StartModel();
            clock.AdvanceClock(200);
            var run = clock.Context.Run;
            Assert.IsTrue(run.Deadlocked, "the yard holds a sub-block nobody wants: a real deadlock");

            string bayA = run.Waiting.Find(w => w.StartsWith("bayA:")), bayB = run.Waiting.Find(w => w.StartsWith("bayB:"));
            string yard = run.Waiting.Find(w => w.StartsWith("yard:"));
            Assert.IsNotNull(bayA, string.Join(" | ", run.Waiting));
            StringAssert.Contains("RECEIVING", bayA);
            StringAssert.Contains("waits for components from list yardList", bayA);
            StringAssert.DoesNotContain("erected", bayA, "not the elements after it");
            StringAssert.Contains("waits for components from list yardList", bayB);
            StringAssert.Contains("waits for list yardList (pulled by bayA, bayB)", yard, "the list holder names the list's pullers");
        }
    }
}
