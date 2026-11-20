using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// The run-time counters of one element's decisions (the logic viewer in Play; read through
    /// <see cref="SimuLean.Logic.ModelLogic.Live"/>): how many items its routing sent to each output and its last choice with
    /// the scores of a best-score or rule routing and the item it was for, how often a waiting routing held an item for its
    /// full best output, how many items entered through its admission, how often the admission was checked and said no and
    /// was woken, how many items it released and the last one. Kept on the element
    /// (<see cref="Element.Decisions"/>, made at its first decision), updated where the decision is made. Read-only for the
    /// model: no random draw, no event, no allocation per event (fixed buffers grown with the outputs) — a run's results are
    /// the same whether they are read or not (<c>logictests</c>).
    /// </summary>
    public sealed class DecisionCounters
    {
        private long[] routed = new long[0];
        private IReadOnlyList<Element> outputs;
        private double[] working = new double[0], scores = new double[0];

        // ── routing ──────────────────────────────────────────────────────

        /// <summary>The outputs counted (the link's destinations, in connection order), or null before the first item routed.</summary>
        public IReadOnlyList<Element> Outputs => outputs;

        /// <summary>Items handed over to output <paramref name="index"/> (in <see cref="Outputs"/>' order).</summary>
        public long RoutedTo(int index) => index >= 0 && index < routed.Length ? routed[index] : 0;

        /// <summary>Items handed over through the element's routing.</summary>
        public long Routed { get; private set; }

        /// <summary>The output of the last choice (null: none yet).</summary>
        public Element LastOutput { get; private set; }

        /// <summary>The item of the last choice (null: none yet) — its labels say why. Read when the counters
        /// are read: labels written after the choice (an entry label downstream) show too.</summary>
        public Item LastItem { get; private set; }

        /// <summary>The scores of the last choice of a best-score or rule routing, by output (NaN = not scored: full or not
        /// admitting); empty for a routing without scores.</summary>
        public IReadOnlyList<double> LastScores => scores;

        /// <summary>The index of the last choice among <see cref="LastScores"/> (−1: no scored choice).</summary>
        public int LastScored { get; private set; } = -1;

        /// <summary>Times a routing that waits for its best output (<c>whenBestFull = Wait</c>) held an item because that output
        /// was full.</summary>
        public long HeldForBest { get; private set; }

        /// <summary>The item <paramref name="item"/> handed over to <paramref name="outputs"/>[<paramref name="index"/>].</summary>
        public void Handed(IReadOnlyList<Element> outputs, int index, Item item)
        {
            if (index < 0 || outputs == null || index >= outputs.Count) return;
            if (!ReferenceEquals(this.outputs, outputs) || routed.Length < outputs.Count)
            {
                if (routed.Length < outputs.Count) Array.Resize(ref routed, outputs.Count);
                this.outputs = outputs;
            }
            routed[index]++;
            Routed++;
            LastOutput = outputs[index];
            LastItem = item;
        }

        /// <summary>A routing chose <paramref name="output"/> for <paramref name="item"/> (the hand-over may still fail).</summary>
        public void Chose(Element output, Item item)
        {
            if (output == null) return;
            LastOutput = output;
            LastItem = item;
        }

        /// <summary>A scored choice starts over <paramref name="count"/> outputs (every score NaN until given).</summary>
        public void BeginScores(int count)
        {
            if (working.Length != count) working = new double[count];   // only when the outputs change
            for (int i = 0; i < count; i++) working[i] = double.NaN;
        }

        /// <summary>Output <paramref name="index"/> scored <paramref name="value"/>.</summary>
        public void Score(int index, double value) { if (index >= 0 && index < working.Length) working[index] = value; }

        /// <summary>The scored choice picked <paramref name="best"/> (−1: none): its scores become the last choice's.</summary>
        public void EndScores(int best)
        {
            if (best < 0) return;
            var t = scores; scores = working; working = t.Length == scores.Length ? t : new double[scores.Length];
            LastScored = best;
        }

        /// <summary>A waiting routing held an item for its full best output.</summary>
        public void HeldBack() => HeldForBest++;

        // ── admission ────────────────────────────────────────────────────

        /// <summary>Items that entered the element while it had an admission condition — real entries, one per item (not the condition's passes, which are several per item).</summary>
        public long Entered { get; private set; }

        /// <summary>Evaluations of the admission condition: each offer, and each check by a sender's routing, counts — an
        /// item may be checked several times before it enters.</summary>
        public long Checks { get; private set; }

        /// <summary>The <see cref="Checks"/> that said no.</summary>
        public long Refused { get; private set; }

        /// <summary>Wake-ups of its admission: a property write its condition reads, or a reaction's Retry, offered its refused
        /// items again (<see cref="Element.WakeInput"/>).</summary>
        public long WakeUps { get; private set; }

        /// <summary>An admission evaluation.</summary>
        public void Admission(bool admitted) { Checks++; if (!admitted) Refused++; }

        /// <summary>An item entered through the admission.</summary>
        public void Entry() => Entered++;

        /// <summary>A wake-up of the admission.</summary>
        public void WokenUp() => WakeUps++;

        // ── release order ────────────────────────────────────────────────

        /// <summary>Items released after waiting in the element — the ones its release order chose among — the last one and
        /// when. Items that passed straight through (nobody waiting, the receiver free) are <see cref="PassedThrough"/>: the
        /// order did not decide them.</summary>
        public long Released { get; private set; }

        /// <summary>Items that left at once on arrival without waiting (no release decision).</summary>
        public long PassedThrough { get; private set; }

        /// <summary>Every item that left: <see cref="Released"/> + <see cref="PassedThrough"/>.</summary>
        public long Left => Released + PassedThrough;

        /// <summary>An item that passed straight through.</summary>
        public void PassThrough() => PassedThrough++;

        /// <summary>See <see cref="Released"/>.</summary>
        public Item LastReleased { get; private set; }

        /// <summary>See <see cref="Released"/>.</summary>
        public double LastReleasedAt { get; private set; } = double.NaN;

        /// <summary>An item released at <paramref name="time"/>.</summary>
        public void Release(Item item, double time)
        {
            Released++;
            LastReleased = item;
            LastReleasedAt = time;
        }
    }
}
