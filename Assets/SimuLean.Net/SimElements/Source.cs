using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimuLean
{
    /// <summary>
    /// Schedule-based source that creates items from a pre-loaded data table.
    /// Each row in the data table becomes one or more items (based on Q column).
    /// All columns (except Time, Name, Q) become labels on the created items.
    /// </summary>
    public class ScheduleSource : Element, IEvent
    {
        /// <summary>Labels given to every created item (label → value; numeric text is stored as a number).
        /// Table columns of the same name override them.</summary>
        public Dictionary<string, string> AssignedLabels { get; } = new Dictionary<string, string>();

        Queue<Item> itemsInQueue;

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
            StatsCollector.TracksContent = false;   // generator: items leave without entering (content stays 0)
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

        /// <summary>
        /// Demand factor (default 1): each row's quantity is multiplied by it — 1.2 = 20 % more
        /// arrivals at the same times. The fractions carry over from row to row (deterministic: 1.2 on rows of one gives a
        /// second item every fifth row), so the total is the table's total times the factor. A study parameter
        /// (<c>demandFactor</c>).
        /// </summary>
        public double DemandFactor
        {
            get => demandFactor;
            set => demandFactor = value > 0 && !double.IsInfinity(value) ? value : throw new ArgumentOutOfRangeException(nameof(DemandFactor), "The demand factor must be > 0.");
        }
        private double demandFactor = 1.0, demandCarry;

        public override void Start()
        {
            itemCounter = 0;
            demandCarry = 0;
            currentRowIndex = 0;
            emittingRowIndex = 0;
            itemsInQueue.Clear();     // a restart on the same clock sent the previous run's held items
            currentPendingQ = 0;
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

        public override bool Receive(Item theItem)
        {
            throw new InvalidOperationException("The Source cannot receive Items.");
        }

        override public int GetQueueLength()
        {
            return 0;
        }
        
        override public int GetFreeCapacity()
        {
            return 0;
        }

        void IEvent.Execute()
        {
            SetState(ElementState.PROCESSING);    // emitting items this event

            Item newItem = CreateItem();
            while (newItem != null)
            {
                if (!SendOut(newItem))
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

            // Get item name
            string itemName = GetStringValue("Name", emittingRowIndex) ?? "Default";

            // Create item
            Item newItem = new Item(simClock.GetSimulationTime());
            foreach (var kv in AssignedLabels) newItem.SetLabelValue(kv.Key, kv.Value);   // table columns override

            // Apply ALL columns as labels (except Time, Name, Q which are structural)
            foreach (var kvp in arrivalsTable)
            {
                string columnName = kvp.Key;
                
                // Skip structural columns
                if (columnName == "Name" || columnName == "Time" || columnName == "Q")
                    continue;

                // Get value for current row
                if (emittingRowIndex < kvp.Value.Count)
                {
                    string value = kvp.Value[emittingRowIndex];
                    if (!string.IsNullOrEmpty(value))
                    {
                        newItem.SetLabelValue(columnName, value);
                    }
                }
            }

            // Set item ID
            itemCounter++;
            newItem.SetId(itemName, itemCounter, 0);

            // Create visual item
            newItem.vItem = vElement.GenerateItem(newItem.GetId());

            return newItem;
        }

        public override bool CheckAvailability(Item theItem)
        {
            return false;
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
            if (demandFactor != 1.0)
            {
                double scaled = currentPendingQ * demandFactor + demandCarry;
                int n = (int)Math.Floor(scaled + 1e-9);
                demandCarry = Math.Max(0, scaled - n);
                currentPendingQ = n;
            }

            // Calculate delay
            double delay = Math.Max(0, arrivalTime - simClock.GetSimulationTime());

            // Schedule event
            simClock.ScheduleEvent(this, delay);

            // The event emits this row; move on to the next one
            emittingRowIndex = currentRowIndex;
            currentRowIndex++;
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
    public class InterArrivalSource : Element, IEvent
    {
        /// <summary>Labels given to every created item (label → value; numeric text is stored as a number).</summary>
        public Dictionary<string, string> AssignedLabels { get; } = new Dictionary<string, string>();

        Item lastItem;
        int numberItems;
        
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
            StatsCollector.TracksContent = false;   // generator: items leave without entering (content stays 0)
            this.interArrivalTime = interArrivalTime;
        }

        /// <summary>Demand factor (default 1): the arrival rate is multiplied by it (the times between
        /// arrivals divided) — 1.2 = 20 % more arrivals. A study parameter (<c>demandFactor</c>).</summary>
        public double DemandFactor
        {
            get => demandFactor;
            set => demandFactor = value > 0 && !double.IsInfinity(value) ? value : throw new ArgumentOutOfRangeException(nameof(DemandFactor), "The demand factor must be > 0.");
        }
        private double demandFactor = 1.0;

        public override void Start()
        {
            numberItems = 0;
            lastItem = null;          // a restart on the same clock sent the previous run's held item
            base.Start();
            
            // Schedule first arrival
            double firstDelay = GetNextInterArrivalTime();
            simClock.ScheduleEvent(this, firstDelay);
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
                simClock.ScheduleEvent(this, nextDelay);
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

        public override bool Receive(Item theItem)
        {
            throw new InvalidOperationException("The Source cannot receive Items.");
        }

        override public int GetQueueLength()
        {
            return 0;
        }
        
        override public int GetFreeCapacity()
        {
            return 0;
        }

        void IEvent.Execute()
        {
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
            simClock.ScheduleEvent(this, nextDelay);
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
            
            // an expression draws from the source's stream (exponential(), rand(); review2 #3)
            double delay = SimuLean.Expressions.Samplers.Sample(interArrivalTime, null, this);
            return Math.Max(0.0, delay) / demandFactor; // non-negative; a demand factor scales the rate
        }

        /// <summary>
        /// Creates new item with simple ID.
        /// </summary>
        Item CreateItem()
        {
            Item nItem = new Item(simClock.GetSimulationTime());
            nItem.SetId("Item", numberItems + 1, 0);
            foreach (var kv in AssignedLabels) nItem.SetLabelValue(kv.Key, kv.Value);
            nItem.vItem = vElement.GenerateItem(nItem.GetId());

            return nItem;
        }

        public override bool CheckAvailability(Item theItem)
        {
            return false;
        }
    }
}

