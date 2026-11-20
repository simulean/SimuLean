using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimuLean
{
    /// <summary>
    /// What a source does with its arrivals while it is stopped (<c>arrivalsOnStop</c>): <see cref="Hold"/> - the
    /// default - keeps them (a time-between-arrivals source stops creating and holds the one item that came; a schedule
    /// source keeps its timetable and its items wait, leaving together when the stop ends); <see cref="Pause"/> freezes the
    /// arrivals (the time to the next one, and a schedule's remaining timetable, are shifted by the stop: nothing arrives
    /// and nothing piles up - a supplier that does not work); <see cref="Lose"/> lets them come and drops them (counted in
    /// <c>lost_arrivals</c>: customers who find the door closed).
    /// </summary>
    public enum SourceStopPolicy { Hold, Pause, Lose }

    /// <summary>
    /// What the sources share (<see cref="ScheduleSource"/>, <see cref="InterArrivalSource"/>, <see cref="RateTableSource"/>):
    /// a generator that receives nothing and creates its items itself (content not tracked), its creation labels and model
    /// size, its demand factor, what its arrivals do while it is stopped (<see cref="ArrivalsOnStop"/>: the lost ones counted,
    /// the pending arrival frozen and resumed) and the lost-arrivals statistic. Each source decides when its items arrive
    /// (<see cref="OnArrival"/>) and how a pause is kept (<see cref="PauseArrivals"/> / <see cref="ResumeArrivals"/>).
    /// </summary>
    public abstract class SourceBase : Element, IEvent, IElementMetrics
    {
        /// <summary>Labels set on every created item, <c>name = expression</c> records (<see cref="LabelAssignments"/>,
        /// streams of purpose <c>createLabel</c>; null = none), set at creation before the source's own labels (a table's
        /// columns, a rate period's labels), which win.</summary>
        public LabelAssignments CreateLabels { get; set; }

        /// <summary>Size (width, height, length) given to every created item where its labels set none (the item's 3D
        /// model in a scene, <see cref="Item.SetSizeWhereMissing"/>); null = none.</summary>
        public double[] ModelSize { get; set; }

        /// <summary>What the arrivals do while the source is stopped (<see cref="SourceStopPolicy"/>; default Hold).</summary>
        public SourceStopPolicy ArrivalsOnStop { get; set; } = SourceStopPolicy.Hold;

        /// <summary>Arrivals dropped while stopped (<see cref="SourceStopPolicy.Lose"/>), since the run start or the warm-up.</summary>
        public int LostArrivals { get; private set; }

        /// <summary>The demand factor (default 1, a study parameter <c>demandFactor</c>): how each source scales its arrivals.</summary>
        public double DemandFactor
        {
            get => demandFactor;
            set => demandFactor = value > 0 && !double.IsInfinity(value) ? value : throw new ArgumentOutOfRangeException(nameof(DemandFactor), "The demand factor must be > 0.");
        }
        private double demandFactor = 1.0;

        /// <summary>The item labels its own tables write (label, value, where): the build checks the names expressions read
        /// against them. None by default.</summary>
        internal virtual IEnumerable<(string label, string value, string origin)> TableLabelWrites() { yield break; }

        /// <summary>The pending arrival event (a pause cancels and re-schedules it; null = none).</summary>
        protected EventHandle nextArrival;

        protected SourceBase(string name, SimClock state, IVElement vElement)
            : base(name, state, vElement)
        {
            StatsCollector.TracksContent = false;   // generator: items leave without entering (content stays 0)
        }

        /// <summary>An arrival came while the source is stopped and its arrivals are lost (<see cref="SourceStopPolicy.Lose"/>).</summary>
        protected bool LosesArrival => ArrivalsOnStop == SourceStopPolicy.Lose && IsDown;

        /// <summary>Counts arrivals lost at a stopped source.</summary>
        protected void Lose(int arrivals) => LostArrivals += arrivals;

        /// <summary>Clears the lost arrivals and the pending arrival: the start of a run.</summary>
        protected void ResetArrivals()
        {
            LostArrivals = 0;
            nextArrival = null;
        }

        /// <summary>A new item: its id first (the creation labels may read its id and type), the creation labels, then
        /// <paramref name="ownLabels"/> (they win), the model size where the labels set none, and its view.</summary>
        protected Item NewItem(double now, string name, int number, Action<Item> ownLabels = null)
        {
            var item = new Item(now);
            item.SetId(name, number, 0);
            CreateLabels?.Apply(item, this);
            ownLabels?.Invoke(item);
            item.SetSizeWhereMissing(ModelSize);   // after the labels: they win
            item.vItem = vElement.GenerateItem(item.GetId());
            return item;
        }

        void IEvent.Execute()
        {
            nextArrival = null;
            OnArrival();
        }

        /// <summary>The pending arrival event fired (<see cref="nextArrival"/> already cleared).</summary>
        protected abstract void OnArrival();

        /// <summary>The first stop of a pausing source: the pending arrival (its time, when it was cancelled; null = none)
        /// is kept for <see cref="ResumeArrivals"/>.</summary>
        protected abstract void PauseArrivals(double now, double? pendingTime);

        /// <summary>The last stop of a pausing source ended: the arrivals go on.</summary>
        protected abstract void ResumeArrivals(double now);

        protected override void OnStopped(StopToken token)
        {
            if (ArrivalsOnStop != SourceStopPolicy.Pause || ActiveStops.Count != 1) return;   // the first stop pauses the arrivals
            double now = simClock.GetSimulationTime();
            bool cancelled = nextArrival != null && nextArrival.Cancel();
            PauseArrivals(now, cancelled ? nextArrival.Time : (double?)null);
            nextArrival = null;
        }

        protected override void OnResumed(StopToken token)
        {
            if (ArrivalsOnStop != SourceStopPolicy.Pause || IsDown) return;   // the last stop ended
            ResumeArrivals(simClock.GetSimulationTime());
        }

        public override bool Receive(Item theItem) => throw new InvalidOperationException("The Source cannot receive Items.");
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item theItem) => false;

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            LostArrivals = 0;
        }

        void IElementMetrics.ReportMetrics(Action<string, double> report)
        {
            if (ArrivalsOnStop == SourceStopPolicy.Lose) report(SimuLean.Results.ResultMetrics.LostArrivals, LostArrivals);   // only then: other results keep their shape
        }
    }

    /// <summary>
    /// Schedule-based source that creates items from a pre-loaded data table.
    /// Each row in the data table becomes one or more items (based on Q column).
    /// All columns (except Time, Name, Q) become labels on the created items.
    /// </summary>
    public class ScheduleSource : SourceBase
    {
        Queue<Item> itemsInQueue;

        private double pausedRemaining = -1;    // Pause: the time left to it when the stop began (-1 = none)
        private double pauseStart, timetableShift; // Pause: when the stop began; the whole timetable's delay so far

        // Core data structure: column-oriented dictionary
        // Key = column name (e.g., "Name", "Time", "Priority", "tSoldadura", etc.)
        // Value = list of values for that column (one per row/item)
        private Dictionary<string, List<string>> arrivalsTable;
        
        private Item modelItem;

        // State variables for event-driven item creation
        private int currentRowIndex = 0;  // Next row to schedule
        private int emittingRowIndex = 0; // Row whose items the scheduled event creates
        private int currentPendingQ;      // How many items to create from current row
        
        // Counter for assigning unique IDs
        private int itemCounter = 0;
        
        /// <summary>
        /// Public property to access the data dictionary for optimization purposes.
        /// Returns a copy to prevent external modification during simulation.
        /// </summary>
        public Dictionary<string, List<string>> ArrivalsTable 
        { 
            get 
            {
                if (arrivalsTable == null)
                    return null;
                    
                // Return a deep copy to prevent external modification
                var copy = new Dictionary<string, List<string>>();
                foreach (var kvp in arrivalsTable)
                {
                    copy[kvp.Key] = new List<string>(kvp.Value);
                }
                return copy;
            }
        }
        
        /// <summary>
        /// Gets the number of items (rows) in the data dictionary.
        /// Returns 0 if no data is loaded.
        /// </summary>
        public int GetDataItemCount()
        {
            if (arrivalsTable == null || arrivalsTable.Count == 0)
                return 0;
                
            return arrivalsTable.Values.First()?.Count ?? 0;
        }

        /// <summary>
        /// A source that creates the items of <paramref name="arrivalsTable"/> (column name → values, one per row), in
        /// the order given. Files are read outside the element (<see cref="SimuLean.Data.TableReader"/>; Excel in Unity).
        /// </summary>
        public ScheduleSource(
            String name,
            SimClock state,
            Dictionary<string, List<string>> arrivalsTable,
            Item modelItem = null,
            IVElement vElement = null)
            : base(name, state, vElement)
        {
            this.modelItem = modelItem;
            itemsInQueue = new Queue<Item>();
            if (arrivalsTable == null || arrivalsTable.Count == 0)
                throw new ArgumentException($"ScheduleSource '{name}': the arrivals table is empty.");
            this.arrivalsTable = arrivalsTable;
        }

        /// <summary>
        /// Helper: Get string value from arrivalsTable at specific row index.
        /// </summary>
        private string GetStringValue(string columnName, int rowIndex)
        {
            if (arrivalsTable.TryGetValue(columnName, out var column) && rowIndex < column.Count)
                return column[rowIndex];
            return null;
        }

        /// <summary>
        /// Helper: Get double value from arrivalsTable at specific row index.
        /// </summary>
        private double GetDoubleValue(string columnName, int rowIndex, double defaultValue)
        {
            string str = GetStringValue(columnName, rowIndex);
            if (str != null && double.TryParse(str.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return value;
            return defaultValue;
        }

        /// <summary>
        /// Helper: Get int value from arrivalsTable at specific row index.
        /// </summary>
        private int GetIntValue(string columnName, int rowIndex, int defaultValue)
        {
            string str = GetStringValue(columnName, rowIndex);
            if (int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return value;
            return defaultValue;
        }

        // The demand factor (DemandFactor) multiplies each row's quantity — 1.2 = 20 % more arrivals at the same times. The
        // fractions carry over from row to row (deterministic: 1.2 on rows of one gives a second item every fifth row), so
        // the total is the table's total times the factor.
        private double demandCarry;

        public override void Start()
        {
            itemCounter = 0;
            demandCarry = 0;
            currentRowIndex = 0;
            emittingRowIndex = 0;
            itemsInQueue.Clear();     // a restart on the same clock sent the previous run's held items
            currentPendingQ = 0;
            ResetArrivals();
            pausedRemaining = -1; timetableShift = 0;
            base.Start();
            ScheduleNext();
        }

        public override bool Unblock()
        {
            bool sentAny = false;

            while (itemsInQueue.Count > 0)
            {
                Item theItem = itemsInQueue.Peek();
                if (SendOut(theItem))
                {
                    itemsInQueue.Dequeue();
                    sentAny = true;
                }
                else
                {
                    break;
                }
            }

            if (sentAny && itemsInQueue.Count == 0)
                SetState(ElementState.IDLE);     // backlog cleared

            return sentAny;
        }

        /// <summary>Hands over a specific held item (pulled from a list), wherever it is in the backlog.</summary>
        public override bool SendHeldItem(Item item)
        {
            if (!itemsInQueue.Contains(item) || !SendOut(item))
                return false;
            var rest = new Queue<Item>(itemsInQueue.Count);
            foreach (var it in itemsInQueue) if (it != item) rest.Enqueue(it);
            itemsInQueue = rest;
            if (itemsInQueue.Count == 0) SetState(ElementState.IDLE);   // backlog cleared
            return true;
        }

        protected override void OnArrival()
        {
            if (LosesArrival)
            {
                // the row's items arrive at a stopped source and are lost (the timetable goes on)
                Lose(currentPendingQ);
                currentPendingQ = 0;
                ScheduleNext();
                return;
            }
            SetState(ElementState.PROCESSING);    // emitting items this event

            // The backlog goes first, and a new item queues behind it: it was offered before the held items and could
            // overtake them. Pushing to a list, every item is advertised (the pullers choose).
            bool toList = GetOutput() is IAdvertisingLink;
            if (!toList && itemsInQueue.Count > 0) Unblock();
            Item newItem = CreateItem();
            while (newItem != null)
            {
                if ((!toList && itemsInQueue.Count > 0) || !SendOut(newItem))
                {
                    itemsInQueue.Enqueue(newItem);
                }
                newItem = CreateItem();
            }

            if (itemsInQueue.Count > 0)
                SetState(ElementState.BLOCKED); // items could not be forwarded
            else
                SetState(ElementState.IDLE);    // all items sent — waiting for next arrival

            ScheduleNext();
        }

        /// <summary>
        /// Creates an item from the current row data.
        /// All columns (except Time, Name, Q) become labels on the item.
        /// </summary>
        Item CreateItem()
        {
            if (currentPendingQ <= 0)
                return null;

            currentPendingQ--;

            string itemName = GetStringValue("Name", emittingRowIndex) ?? "Default";
            itemCounter++;
            return NewItem(simClock.GetSimulationTime(), itemName, itemCounter, ApplyColumns);
        }

        // every value of its table's label columns (all but Time, Name, Q)
        internal override IEnumerable<(string label, string value, string origin)> TableLabelWrites()
        {
            foreach (var column in arrivalsTable)
            {
                if (column.Key == "Name" || column.Key == "Time" || column.Key == "Q") continue;
                foreach (string v in column.Value) if (!string.IsNullOrEmpty(v)) yield return (column.Key, v, $"'{Name}' (arrivals table)");
            }
        }

        // Every column but the structural ones (Time, Name, Q) becomes a label of the item: they override the creation labels.
        private void ApplyColumns(Item newItem)
        {
            foreach (var kvp in arrivalsTable)
            {
                string columnName = kvp.Key;
                if (columnName == "Name" || columnName == "Time" || columnName == "Q")
                    continue;
                if (emittingRowIndex < kvp.Value.Count)
                {
                    string value = kvp.Value[emittingRowIndex];
                    if (!string.IsNullOrEmpty(value))
                        newItem.SetLabelValue(columnName, value);
                }
            }
        }

        /// <summary>
        /// Schedules the next event by reading the next row from arrivalsTable.
        /// </summary>
        private void ScheduleNext()
        {
            int rowCount = arrivalsTable.Values.First().Count;
            
            // Check if we've processed all rows
            if (currentRowIndex >= rowCount)
            {
                return; // No more rows
            }

            // Get data for current row
            double arrivalTime = GetDoubleValue("Time", currentRowIndex, simClock.GetSimulationTime());
            currentPendingQ = GetIntValue("Q", currentRowIndex, 1);
            if (DemandFactor != 1.0)
            {
                double scaled = currentPendingQ * DemandFactor + demandCarry;
                int n = (int)Math.Floor(scaled + 1e-9);
                demandCarry = Math.Max(0, scaled - n);
                currentPendingQ = n;
            }

            // Calculate delay
            double delay = Math.Max(0, arrivalTime + timetableShift - simClock.GetSimulationTime());   // Pause: delayed by the stops

            // Schedule event
            nextArrival = simClock.ScheduleEvent(this, delay);

            // The event emits this row; move on to the next one
            emittingRowIndex = currentRowIndex;
            currentRowIndex++;
        }

        // the first stop pauses the timetable
        protected override void PauseArrivals(double now, double? pendingTime)
        {
            pauseStart = now;
            if (pendingTime is double t) pausedRemaining = Math.Max(0, t - pauseStart);
        }

        // the last stop ended: the timetable goes on, delayed
        protected override void ResumeArrivals(double now)
        {
            timetableShift += now - pauseStart;
            if (pausedRemaining >= 0) nextArrival = simClock.ScheduleEvent(this, pausedRemaining);
            pausedRemaining = -1;
        }

        public Queue<Item> GetItems()
        {
            return itemsInQueue;
        }
    }

    /// <summary>
    /// Inter-arrival time source that generates items based on time-between-arrivals distribution.
    /// If interArrivalTime is null or returns 0, items are created immediately (at current simulation time).
    /// Always uses event-driven scheduling - no special "infinite" mode needed.
    /// </summary>
    public class InterArrivalSource : SourceBase
    {
        Item lastItem;
        int numberItems;

        private double pausedRemaining = -1;    // Pause: the time left to it when the stop began (-1 = none)
        
        // Inter-arrival time random process
        private IRandomSampler interArrivalTime;

        /// <summary>
        /// Constructor for Inter-Arrival source.
        /// </summary>
        /// <param name="name">Element name</param>
        /// <param name="state">Simulation clock</param>
        /// <param name="interArrivalTime">Time-between-arrivals random process. Can be null for immediate arrivals.</param>
        /// <param name="vElement">Visual element implementation (null for headless)</param>
        public InterArrivalSource(
            string name, 
            SimClock state, 
            IRandomSampler interArrivalTime = null,
            IVElement vElement = null) 
            : base(name, state, vElement)
        {
            this.interArrivalTime = interArrivalTime;
        }

        public override void Start()
        {
            numberItems = 0;
            lastItem = null;          // a restart on the same clock sent the previous run's held item
            ResetArrivals();
            pausedRemaining = -1;
            base.Start();
            
            // Schedule first arrival
            double firstDelay = GetNextInterArrivalTime();
            nextArrival = simClock.ScheduleEvent(this, firstDelay);
            SetState(ElementState.IDLE);
        }

        public override bool Unblock()
        {
            if (lastItem == null)
                return false; // No pending item to send

            if (SendOut(lastItem))
            {
                SetState(ElementState.IDLE);
                lastItem = null;
                double nextDelay = GetNextInterArrivalTime();
                nextArrival = simClock.ScheduleEvent(this, nextDelay);
                return true;
            }
            else
            {
                SetState(ElementState.BLOCKED);
                return false;
            }
        }

        public int GetNumberItems()
        {
            return numberItems;
        }

        protected override void OnArrival()
        {
            if (LosesArrival)
            {
                // an arrival at a stopped source is lost; the arrivals go on
                Lose(1);
                nextArrival = simClock.ScheduleEvent(this, GetNextInterArrivalTime());
                return;
            }
            SetState(ElementState.PROCESSING);
            
            // Create one item per event
            lastItem = CreateItem();
            numberItems++;
            
            if (!SendOut(lastItem))
            {
                SetState(ElementState.BLOCKED); // item pending
                return;
            }
            else
            {
                SetState(ElementState.IDLE);
                lastItem = null;
            }
            
            // Schedule next arrival
            double nextDelay = GetNextInterArrivalTime();
            nextArrival = simClock.ScheduleEvent(this, nextDelay);
        }

        // the first stop freezes the arrivals
        protected override void PauseArrivals(double now, double? pendingTime)
        {
            if (pendingTime is double t) pausedRemaining = Math.Max(0, t - now);
        }

        // the last stop ended: the time left to the next arrival
        protected override void ResumeArrivals(double now)
        {
            if (pausedRemaining >= 0) nextArrival = simClock.ScheduleEvent(this, pausedRemaining);
            pausedRemaining = -1;
        }

        /// <summary>
        /// Gets the next inter-arrival time from the random process.
        /// Returns 0 if process is null (immediate arrival).
        /// Ensures non-negative values.
        /// </summary>
        private double GetNextInterArrivalTime()
        {
            if (interArrivalTime == null)
                return 0.0;
            
            // an expression draws from the source's stream (exponential(), rand())
            double delay = SimuLean.Expressions.Samplers.Sample(interArrivalTime, null, this);
            // non-negative; the demand factor multiplies the arrival rate (divides the times between arrivals): 1.2 = 20 % more
            return Math.Max(0.0, delay) / DemandFactor;
        }

        Item CreateItem() => NewItem(simClock.GetSimulationTime(), "Item", numberItems + 1);
    }

    /// <summary>One period of a <see cref="RateTableSource"/>: from <see cref="Start"/> (model time) arrivals come at
    /// <see cref="Rate"/> per model time unit until the next period's start; the last period's rate holds to the end of the
    /// run. Its items get <see cref="Labels"/> (<c>name = expression</c> records) and <see cref="Columns"/> (a table's other
    /// columns: text values, as a schedule's), named <see cref="ItemName"/> (null = "Item").</summary>
    public sealed class RatePeriod
    {
        public double Start;
        public double Rate;
        public LabelAssignments Labels;
        public IReadOnlyList<KeyValuePair<string, string>> Columns;
        public string ItemName;
    }

    /// <summary>
    /// A source whose arrivals follow a <b>rate table</b>: a non-stationary Poisson process with a
    /// piecewise-constant rate — the rate of each period (arrivals per model time unit, times <see cref="DemandFactor"/>),
    /// 0 before the first period's start; after the last period's start its rate holds to the end. Rate 0 = no arrivals
    /// (the door closed). Each next arrival is the exact inversion of the cumulative rate: a unit-exponential draw
    /// (<see cref="UnitExponential"/>, the element's own stream) is the expected number of arrivals until it, walked across
    /// the periods — never drawn with one period's rate and carried past its end. When the rate is 0 from some time on,
    /// nothing more is scheduled: a run that stops when the model is empty ends at its last exit.
    /// <para>As <see cref="InterArrivalSource"/>: one item per arrival; while its receiver is full the item waits (BLOCKED)
    /// and the next arrival is drawn only once it has left (no arrival is lost, the process is delayed); a stop holds,
    /// pauses (the remaining expected arrivals kept, the periods not shifted) or loses arrivals (<see cref="ArrivalsOnStop"/>).</para>
    /// </summary>
    public class RateTableSource : SourceBase
    {
        /// <summary>The draws of the expected number of arrivals to the next one (an exponential of mean 1, the element's stream).</summary>
        public IRandomSampler UnitExponential { get; }

        /// <summary>The periods, by start.</summary>
        public IReadOnlyList<RatePeriod> Periods => periods;

        // The demand factor (DemandFactor) multiplies the rates.
        private readonly RatePeriod[] periods;
        private Item lastItem;
        private int numberItems;
        private double pausedMass = -1;   // Pause: the expected arrivals left to the next one when the stop began (-1 = none)

        /// <param name="periods">At least one; starts ≥ 0 and strictly increasing, rates ≥ 0 and finite.</param>
        /// <param name="unitExponential">Exponential draws of mean 1 (its own stream).</param>
        public RateTableSource(string name, SimClock state, IList<RatePeriod> periods, IRandomSampler unitExponential, IVElement vElement = null)
            : base(name, state, vElement)
        {
            UnitExponential =unitExponential ?? throw new ArgumentNullException(nameof(unitExponential));
            if (periods == null || periods.Count == 0) throw new FormatException($"Source '{name}': a rate table needs at least one period.");
            for (int i = 0; i < periods.Count; i++)
            {
                var p = periods[i] ?? throw new FormatException($"Source '{name}': rate period {i} is empty.");
                if (!(p.Start >= 0) || double.IsInfinity(p.Start))
                    throw new FormatException($"Source '{name}': rate period {i} starts at {p.Start} (a time ≥ 0).");
                if (i > 0 && !(p.Start > periods[i - 1].Start))
                    throw new FormatException($"Source '{name}': rate period {i} starts at {p.Start}, not after period {i - 1} ({periods[i - 1].Start}): the starts must increase.");
                if (!(p.Rate >= 0) || double.IsInfinity(p.Rate))
                    throw new FormatException($"Source '{name}': rate period {i} has rate {p.Rate} (arrivals per time unit, ≥ 0; 0 = none).");
            }
            this.periods = new RatePeriod[periods.Count];
            periods.CopyTo(this.periods, 0);
        }

        /// <summary>The rate at time <paramref name="t"/> (demand factor included): 0 before the first period.</summary>
        public double RateAt(double t)
        {
            int i = PeriodIndex(t);
            return i < 0 ? 0 : periods[i].Rate * DemandFactor;
        }

        /// <summary>The period in force at <paramref name="t"/> (-1 before the first one).</summary>
        public int PeriodIndex(double t)
        {
            int i = -1;
            for (int k = 0; k < periods.Length && periods[k].Start <= t; k++) i = k;
            return i;
        }

        /// <summary>The expected number of arrivals between <paramref name="from"/> and <paramref name="to"/> (the integral of
        /// the rate).</summary>
        public double Mass(double from, double to)
        {
            if (!(to > from)) return 0;
            double m = 0;
            for (int k = 0; k < periods.Length; k++)
            {
                double a = Math.Max(from, periods[k].Start);
                double b = Math.Min(to, k + 1 < periods.Length ? periods[k + 1].Start : double.PositiveInfinity);
                if (b > a && periods[k].Rate > 0) m += periods[k].Rate * DemandFactor * (b - a);
            }
            return m;
        }

        /// <summary>The time at which <paramref name="mass"/> expected arrivals have come after <paramref name="from"/> — the
        /// inversion of the cumulative rate across the periods; +∞ when the rate stays 0 before that.</summary>
        public double TimeAfter(double from, double mass)
        {
            double t = from;
            int k = PeriodIndex(from);
            if (k < 0) { k = 0; t = Math.Max(from, periods[0].Start); }
            for (; k < periods.Length; k++)
            {
                double end = k + 1 < periods.Length ? periods[k + 1].Start : double.PositiveInfinity;
                double rate = periods[k].Rate * DemandFactor;
                if (rate > 0)
                {
                    double span = mass / rate;
                    if (t + span <= end) return t + span;
                    mass -= rate * (end - t);
                }
                t = end;
            }
            return double.PositiveInfinity;
        }

        public override void Start()
        {
            numberItems = 0;
            lastItem = null;
            ResetArrivals();
            pausedMass = -1;
            base.Start();
            ScheduleNext(simClock.GetSimulationTime());
            SetState(ElementState.IDLE);
        }

        // the next arrival after `from` (none when the rate stays 0)
        private void ScheduleNext(double from)
        {
            double e = UnitExponential.NextValue();
            if (!(e >= 0)) e = 0;
            double at = TimeAfter(from, e);
            if (double.IsInfinity(at)) { nextArrival = null; return; }   // the door closed for good: no more events
            nextArrival = simClock.ScheduleEvent(this, Math.Max(0, at - simClock.GetSimulationTime()));
        }

        public override bool Unblock()
        {
            if (lastItem == null) return false;
            if (SendOut(lastItem))
            {
                SetState(ElementState.IDLE);
                lastItem = null;
                ScheduleNext(simClock.GetSimulationTime());
                return true;
            }
            SetState(ElementState.BLOCKED);
            return false;
        }

        /// <summary>Items created (since the run start).</summary>
        public int GetNumberItems() => numberItems;

        protected override void OnArrival()
        {
            double now = simClock.GetSimulationTime();
            if (LosesArrival)
            {
                Lose(1);   // an arrival at a stopped source is lost; the arrivals go on
                ScheduleNext(now);
                return;
            }
            SetState(ElementState.PROCESSING);
            lastItem = CreateItem(now);
            numberItems++;
            if (!SendOut(lastItem))
            {
                SetState(ElementState.BLOCKED);   // the item waits; the next arrival is drawn once it has left
                return;
            }
            SetState(ElementState.IDLE);
            lastItem = null;
            ScheduleNext(now);
        }

        private Item CreateItem(double now)
        {
            int k = PeriodIndex(now);
            var period = k >= 0 ? periods[k] : null;
            return NewItem(now, string.IsNullOrEmpty(period?.ItemName) ? "Item" : period.ItemName, numberItems + 1, item =>
            {
                period?.Labels?.Apply(item, this);
                if (period?.Columns != null)
                    foreach (var c in period.Columns) if (!string.IsNullOrEmpty(c.Value)) item.SetLabelValue(c.Key, c.Value);
            });
        }

        // every label value of its periods' columns
        internal override IEnumerable<(string label, string value, string origin)> TableLabelWrites()
        {
            foreach (var p in periods)
                if (p.Columns != null)
                    foreach (var c in p.Columns) if (!string.IsNullOrEmpty(c.Value)) yield return (c.Key, c.Value, $"'{Name}' (rate table)");
        }

        // the first stop freezes the arrivals
        protected override void PauseArrivals(double now, double? pendingTime)
        {
            if (pendingTime is double t) pausedMass = Mass(now, t);
        }

        // the last stop ended: the arrivals left go on
        protected override void ResumeArrivals(double now)
        {
            if (pausedMass >= 0)
            {
                double at = TimeAfter(now, pausedMass);
                if (!double.IsInfinity(at)) nextArrival = simClock.ScheduleEvent(this, Math.Max(0, at - now));
            }
            pausedMass = -1;
        }
    }
}

