using NUnit.Framework;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The inspector's live history records from the first sample after it is attached.</summary>
    public class ElementLiveHistoryTests
    {
        [Test]
        public void RecordsFromTheFirstSample()
        {
            var clock = new SimClock(1);
            var sink = new Sink("Sink", clock);
            sink.Start();
            var history = new ElementLiveHistory();
            history.Attach(clock, 10);
            clock.AdvanceClock(5);
            history.Sample();
            Assert.IsNotNull(history.Of(sink), "the element is recorded at the first sample (the refresh counter wrapped to int.MinValue)");
            clock.AdvanceClock(25);
            history.Sample();
            Assert.AreEqual(3, history.Of(sink).Avg.Count, "one bucket per 10 time units up to t = 25");
        }
    }
}
