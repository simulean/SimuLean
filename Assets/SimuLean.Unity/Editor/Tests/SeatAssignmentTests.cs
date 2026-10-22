using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>Seats layout: a waiting patient keeps its seat until it leaves; a newcomer takes the first free one.</summary>
    public class SeatAssignmentTests
    {
        [Test]
        public void EachItemKeepsItsSeatAndNewcomersTakeTheFirstFreeOne()
        {
            var go = new GameObject("Waiting room");
            try
            {
                var q = go.AddComponent<UnityQueue>();
                Item a = new Item(0), b = new Item(0), c = new Item(0), d = new Item(0), e = new Item(0);
                var seats = ItemLayout.SeatsTaken(q, new[] { a, b, c }, 3);
                Assert.AreEqual(0, seats[a]); Assert.AreEqual(1, seats[b]); Assert.AreEqual(2, seats[c]);
                seats = ItemLayout.SeatsTaken(q, new[] { b, c, d }, 3);   // the first one left
                Assert.AreEqual(1, seats[b], "the others do not move up");
                Assert.AreEqual(2, seats[c]);
                Assert.AreEqual(0, seats[d], "the newcomer takes the free seat");
                seats = ItemLayout.SeatsTaken(q, new[] { b, c, d, e }, 3);
                Assert.IsFalse(seats.ContainsKey(e), "no seat left: it stands");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
