using System.Collections.Generic;

namespace SimuLean.Statistics
{
    /// <summary>
    /// Per-element statistics collector attached to every <see cref="Element"/>.
    /// Driven by the link layer: <see cref="OnEntry"/> is called when an item
    /// arrives at this element and <see cref="OnExit"/> when it leaves.
    /// </summary>
    public class ElementStatsCollector : StatisticsCollector
    {
        // ── stat variables ────────────────────────────────────────────────
        /// <summary>Total items received by this element (+1 per arrival).</summary>
        public readonly StatLevelVariable VarInput    = new StatLevelVariable();

        /// <summary>Total items sent out of this element (+1 per departure).</summary>
        public readonly StatLevelVariable VarOutput   = new StatLevelVariable();

        /// <summary>
        /// Current number of items inside the element (+1 on entry, -1 on exit).
        /// Max reflects the peak occupancy seen during the run.
        /// TIME-WEIGHTED: its average integrates the level over simulation time
        /// (true average WIP), unlike the per-update averages of counters.
        /// </summary>
        public readonly StatTimeWeightedVariable VarContent = new StatTimeWeightedVariable();

        /// <summary>Time each item spent inside this element (exit time − entry time).</summary>
        public readonly StatTally         VarStayTime = new StatTally();

        // ── entry-time dictionary ─────────────────────────────────────────
        // Key: Item reference  Value: simulation time at which it entered
        private readonly Dictionary<Item, double> _entryTimes
            = new Dictionary<Item, double>();

        /// <summary>The items inside the element now, as the link layer saw them enter (read-only; empty for an element that
        /// keeps no stay times, e.g. a sink): what <c>count(element, label, value)</c> reads.</summary>
        public Dictionary<Item, double>.KeyCollection ItemsInside => _entryTimes.Keys;

        // Items that left before their entry was recorded, at _passTime: an element that forwards an item inside
        // the Receive that brought it (a queue with a free successor) makes the link call OnExit before OnEntry.
        // The entry that follows at the same time is a zero stay. Kept for one instant only (cleared when the
        // clock moves), so items created inside an element and sent out never accumulate here.
        private readonly HashSet<Item> _passedThrough = new HashSet<Item>();
        private double _passTime = double.NaN;

        // ─────────────────────────────────────────────────────────────────
        public ElementStatsCollector(Element element, SimClock clock)
            : base(element, clock) { }

        /// <summary>
        /// Compound exits (opt-in, default false; set by elements that release batches built
        /// inside them, e.g. <see cref="Stacker"/>). When true and an item leaving through the
        /// link was <b>not</b> tracked as having entered this element, each of its
        /// <see cref="Item.subItems"/> that did enter (and has not left) is recorded as leaving
        /// with it — output count, content and stay time per sub-item — and the compound
        /// itself is not counted. Still driven only by the link layer's <see cref="OnExit"/>,
        /// so the stacked items' input / output / content balance stays exact. With the flag
        /// off (every other element) <see cref="OnExit"/> behaves as an ordinary element does.
        /// </summary>
        public bool CountSubItemsOnExit { get; set; }

        /// <summary>
        /// Compound entries (opt-in, default false; set by elements that take compound items apart, e.g. an unpacker).
        /// When true, an item entering through the link also records each of its <see cref="Item.subItems"/> as entering
        /// (input count, content, entry time), so the contents can later leave one by one through the link with exact
        /// stay times while the emptied compound leaves on its own. Still driven only by the link layer's
        /// <see cref="OnEntry"/>.
        /// </summary>
        public bool CountSubItemsOnEntry { get; set; }

        /// <summary>
        /// Whether this element holds content (default true). Generators (sources) set it to
        /// false: their items are created inside and leave through the link without ever
        /// entering, so <see cref="OnExit"/> would drive content to −1, −2, … (negative current,
        /// peak and average). With the flag off, content stays 0 (current, peak and time-weighted
        /// average); input / output counts and stay times are recorded as usual.
        /// </summary>
        public bool TracksContent { get; set; } = true;

        /// <summary>Keep each item's entry time for its stay (default). Off for an element its items never leave (a sink):
        /// it kept every item it received, the memory growing with the run.</summary>
        public bool TracksStay { get; set; } = true;

        /// <summary>Values recorded for every item that enters (<see cref="ItemTallies"/>): null = none.</summary>
        public ItemTallies Tallies { get; set; }

        /// <summary>Every stay time kept, for its percentiles (<c>stay_p90</c>), once <see cref="KeepStayValues"/> is on
        /// (null otherwise). A queue keeps them — its stay is the wait asked about; elsewhere they would only cost memory
        /// (8 bytes per item that left).</summary>
        public TallySamples StayValues { get; private set; }

        /// <summary>Starts keeping the stay times (call when the element is made).</summary>
        public void KeepStayValues() => StayValues = StayValues ?? new TallySamples();

        /// <summary>The time in system of every item that enters — its age, now − its creation time, as the tally
        /// <c>age</c> reads it — once <see cref="RecordTimeInSystem"/> is on (null otherwise): a sink's <c>tis_*</c>,
        /// whose stays are 0 since nothing leaves it.</summary>
        public TallySamples TimeInSystem { get; private set; }

        /// <summary>Starts recording the time in system of the items that enter (call when the element is made).</summary>
        public void RecordTimeInSystem() => TimeInSystem = TimeInSystem ?? new TallySamples();

        // ── raw samples (optional: fitting distributions to a run's times) ──

        /// <summary>Every stay time, entry time and exit time, once <see cref="RecordSamples"/> is on (null otherwise):
        /// the data of a run to fit distributions to (stay = process / waiting times, entries = inter-arrival, exits =
        /// inter-departure). Off by default — a run records only the summary statistics.</summary>
        public List<double> StaySamples { get; private set; }
        public List<double> EntryTimes { get; private set; }
        public List<double> ExitTimes { get; private set; }

        /// <summary>Starts keeping the raw samples (call before the run).</summary>
        public void RecordSamples()
        {
            StaySamples = StaySamples ?? new List<double>();
            EntryTimes = EntryTimes ?? new List<double>();
            ExitTimes = ExitTimes ?? new List<double>();
        }

        private void ClearSamples() { StaySamples?.Clear(); EntryTimes?.Clear(); ExitTimes?.Clear(); }

        // ── hook methods (called by the links) ─────────────

        /// <summary>
        /// Called by the link layer when <paramref name="theItem"/> arrives at
        /// this element. Updates input count, content level and records entry time.
        /// </summary>
        /// <summary>Time of the last entry / exit (NaN = none yet; absolute times, kept by the warm-up reset) — the
        /// makespan of a sink.</summary>
        public double LastEntryTime { get; private set; } = double.NaN;
        public double LastExitTime { get; private set; } = double.NaN;

        public void OnEntry(Item theItem)
        {
            double now = Clock.GetSimulationTime();
            VarInput.Update(1);
            LastEntryTime = now;
            EntryTimes?.Add(now);
            if (TracksContent) VarContent.Update(1, now);
            if (_passTime == now && _passedThrough.Remove(theItem))
            {
                VarStayTime.Update(0);          // it already left: passed through without waiting
                StaySamples?.Add(0);
                StayValues?.Add(0);
            }
            else if (TracksStay)
                _entryTimes[theItem] = now;
            if (theItem != null) TimeInSystem?.Add(now - theItem.GetCreationTime());
            if (theItem != null) Tallies?.Record(theItem, Element);
            Element?.RaiseItemEntered(theItem);
            Element?.CountAdmittedEntry();   // read-only counter (logic viewer): one per item entered

            if (CountSubItemsOnEntry && theItem != null && theItem.subItems != null)
                foreach (Item sub in theItem.subItems)
                {
                    if (sub == null || sub == theItem || _entryTimes.ContainsKey(sub)) continue;
                    VarInput.Update(1);
                    EntryTimes?.Add(now);
                    if (TracksContent) VarContent.Update(1, now);
                    if (TracksStay) _entryTimes[sub] = now;
                    Element?.RaiseItemEntered(sub);
                }
        }

        /// <summary>
        /// Called by the link layer when <paramref name="theItem"/> leaves this
        /// element. Updates output count, content level and records stay time.
        /// </summary>
        public void OnExit(Item theItem)
        {
            // Compound exit (opt-in, see CountSubItemsOnExit): an item that never entered this
            // element but carries tracked sub-items takes them out with it.
            if (CountSubItemsOnExit && theItem != null && theItem.subItems != null
                && !_entryTimes.ContainsKey(theItem))
            {
                foreach (Item sub in theItem.subItems)
                    if (sub != null && sub != theItem && _entryTimes.ContainsKey(sub))
                        OnExit(sub);
                return;
            }

            VarOutput.Update(1);
            LastExitTime = Clock.GetSimulationTime();
            ExitTimes?.Add(Clock.GetSimulationTime());
            if (TracksContent) VarContent.Update(-1, Clock.GetSimulationTime());

            if (_entryTimes.TryGetValue(theItem, out double entryTime))
            {
                _entryTimes.Remove(theItem);
                double stayTime = Clock.GetSimulationTime() - entryTime;
                VarStayTime.Update(stayTime);
                StaySamples?.Add(stayTime);
                StayValues?.Add(stayTime);
            }
            else if (TracksContent && theItem != null)
            {
                // left before its entry was recorded (see _passedThrough); generators never get here
                double now = Clock.GetSimulationTime();
                if (_passTime != now) { _passedThrough.Clear(); _passTime = now; }
                _passedThrough.Add(theItem);
            }
            Element?.RaiseItemExited(theItem);
        }

        // ── reset ─────────────────────────────────────────────────────────
        public override void Reset()
        {
            VarInput.Reset();
            VarOutput.Reset();
            VarContent.Reset();
            VarStayTime.Reset();
            ClearSamples();
            StayValues?.Clear();
            TimeInSystem?.Clear();
            Tallies?.Reset();
            if (Clock != null) VarContent.Reset(Clock.GetSimulationTime());   // average over the whole run
            _entryTimes.Clear();
            _passedThrough.Clear();
            _passTime = double.NaN;
        }

        /// <summary>
        /// Warm-up reset: clears counts and stay times observed so far but keeps what is inside
        /// the element — content continues from its current level and items already inside keep
        /// their entry time (their stay time is recorded when they leave).
        /// </summary>
        public void ResetForWarmup()
        {
            VarInput.Reset();
            VarOutput.Reset();
            VarStayTime.Reset();
            ClearSamples();
            StayValues?.Clear();
            TimeInSystem?.Clear();
            Tallies?.Reset();
            if (TracksContent) VarContent.Restart(Clock.GetSimulationTime());
            else VarContent.Reset();
        }

        // ── convenience summary getters ───────────────────────────────────

        /// <summary>Current number of items inside the element.</summary>
        public double ContentCurrent  => VarContent.Value;

        /// <summary>Peak occupancy seen during the run.</summary>
        public double ContentMax      => VarContent.Max;

        /// <summary>
        /// Time-weighted average content (true average WIP) up to the current
        /// simulation time.
        /// </summary>
        public double ContentAvgTimeWeighted
            => VarContent.GetTimeWeightedAverage(Clock.GetSimulationTime());

        /// <summary>Total items received.</summary>
        public int    TotalInput      => VarInput.Count;

        /// <summary>Total items sent out.</summary>
        public int    TotalOutput     => VarOutput.Count;

        /// <summary>Average stay time per item (0 if no items have left yet).</summary>
        public double AvgStayTime     => VarStayTime.Average;

        /// <summary>Sample standard deviation of the stay times.</summary>
        public double StdDevStayTime  => VarStayTime.StdDev;

        /// <summary>Maximum stay time observed.</summary>
        public double MaxStayTime     => VarStayTime.Max;

        /// <summary>Minimum stay time observed.</summary>
        public double MinStayTime     => VarStayTime.Min;
    }
}
