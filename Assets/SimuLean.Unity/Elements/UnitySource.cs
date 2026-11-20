using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SimuLean.Unity
{
    public enum SourceMode
    {
        InterArrival,
        Schedule,
        /// <summary>A Poisson process whose rate changes by period: <see cref="UnitySource.ratePeriods"/> or a
        /// data table with the columns Start and Rate.</summary>
        RateTable,
    }

    /// <summary>One period of a rate-table source: from <see cref="start"/> (model time) arrivals come at <see cref="rate"/>
    /// per model time unit (0 = none) until the next period's start; the last one's rate holds to the end of the run.</summary>
    [System.Serializable]
    public class RatePeriodSpec
    {
        [Tooltip("When the period starts (model time; the starts increase).")]
        [Min(0f)] public double start;
        [Tooltip("Arrivals per model time unit in the period (a Poisson process); 0 = none (the door closed).")]
        [Min(0f)] public double rate;
        [Tooltip("Labels set on the period's items, 'name = expression' (e.g. slot = 'morning').")]
        [SimuLean.Unity.SimuLeanExpression("label", Records = true)]
        public List<string> labels = new List<string>();
    }

    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Sources, "Source", Description = "Creates items: inter-arrival distribution, a schedule table or a rate per period", Prefab = "Source.prefab", Order = 0)]
    public class UnitySource : SElement, IVElement
    {
        public override bool HasInput => false;

        private Element theSource;  // Can be either ScheduleSource or InterArrivalSource

        public GameObject itemPrefab;
        [Tooltip("Items get the size of the Item Prefab (labels width / height / length, metres) where their labels set " +
                 "none — what a storage with physical dimensions, a stacker's height limit and a combiner's load height read.")]
        public bool sizeFromModel = true;
        public float separation = 1f;
        public Transform itemPosition;

        [SimuLeanHelp("InterArrival: the first item arrives at t = the first draw (not at 0), then one per drawn interval. Schedule: " +
                      "the table's rows at their Time, Q items each. RateTable: a Poisson process whose rate changes by period " +
                      "(ratePeriods[i].start / .rate, per model time unit; rate 0 = none; the last rate holds to the end, so end with " +
                      "rate 0 to close; or a table with the columns Start, Rate via simulean_load_table). A full receiver: an " +
                      "InterArrival / RateTable source holds its item (BLOCKED) and draws the next arrival only after it left — no " +
                      "arrival is lost, the arrivals are delayed; a Schedule source keeps its timetable and its items wait in order.")]
        public SourceMode mode = SourceMode.Schedule;

        [Tooltip("Labels set on every created item, 'name = expression' (e.g. prio = 5, sku = 'A-07', due = now() + 480, " +
                 "mix = pick(0.7, 'std', 0.3, 'rush')); text in quotes. Schedule-table columns of the same name override them.")]
        [SimuLean.Unity.SimuLeanExpression("label", Records = true)]
        public List<string> createLabels = new List<string> { "prio = 0" };


        [Tooltip("Demand factor: 1 = as given; 1.2 = 20 % more arrivals (the rate, or each table row's quantity, times the " +
                 "factor). A study parameter: run demand scenarios without editing the scene.")]
        [Min(0.01f)] public float demandFactor = 1f;

        [Tooltip("While the source is stopped (a downtime on it): Hold — the arrivals wait and leave when it ends (a schedule's " +
                 "items pile up; a time-between-arrivals source holds the one that came); Pause — no arrivals and the timetable " +
                 "shifts by the stop (a supplier that does not work); Lose — they come and are dropped, counted in lost_arrivals " +
                 "(customers who find the door closed).")]
        public SourceStopPolicy arrivalsOnStop = SourceStopPolicy.Hold;

        [Tooltip("Time between arrivals distribution. If null or returns 0, items arrive immediately.")]
        public RandomProcessConfig interArrivalDistribution =
            new RandomProcessConfig { strategyType = RandomProcessType.Exponential, exponentialRate = 0.2 };   // random arrivals, mean 5

        [Tooltip("RateTable mode: the periods — start (model time), rate (arrivals per model time unit, a Poisson process; " +
                 "0 = none) and the period's item labels. Before the first start the rate is 0; after the last start its rate " +
                 "holds to the end (end with rate 0 to close the door: the source then stops). Or a data table with the columns " +
                 "Start, Rate (and Name, labels) instead.")]
        [SimuLeanHelp("One item per period, by start: ratePeriods[0].start = 0, ratePeriods[0].rate = 0.5 (per model time unit), " +
                      "ratePeriods[1].start = 240, ratePeriods[1].rate = 0 (the door closes: no more arrivals, so a run that stops " +
                      "when empty ends at the last exit). Rate 0 before the first start. labels = 'name = expression' records for " +
                      "the period's items. A study varies ratePeriods[i].rate, or demandFactor for all.")]
        public List<RatePeriodSpec> ratePeriods = new List<RatePeriodSpec>();

        [Tooltip("Schedule mode: the model's data table with the arrivals — one row per arrival: Time, Name, Q and " +
                 "any label columns. RateTable mode: one row per period — Start, Rate, Name and label columns. Its source " +
                 "(typed rows, a CSV / Excel file, an SQL query) is read when each run starts.")]
        public UnityDataTable dataTable;

        [Tooltip("A table read when the model is built, instead of the data table: 'sql:SELECT …' (the project database) or a " +
                 "block breakdown part 'naval:blocks:block_breakdown' (subblocks / blocks / erection, naval library). Every " +
                 "run reads the current data; a study can vary it. Empty = the data table.")]
        public string tableSource = "";
        [Tooltip("The database of the table source. Empty = the project database (Project Settings: the Data tab).")]
        public string tableDatabase = "";

        // The inspector's preview copy of the table source, or rows set from code (SetArrivalsTable).
        [SerializeField]
        private SerializableDataDict serializedData;

        [System.NonSerialized]
        public Dictionary<string, List<string>> arrivalsTable;
        private bool arrivalsTableLoaded = false;

        protected override void OnBound(Element built)
        {
            theSource = built;
            if (itemPosition == null) itemPosition = transform;
        }

        // The item views this source created: the ones still here when the run is cleared (an inter-arrival source's
        // item waiting to leave was never destroyed: a box stayed at the source after Restart / Stop) go with it.
        private readonly List<GameObject> itemViews = new List<GameObject>();

        public override void ClearViews()
        {
            foreach (var go in itemViews) if (go != null) Destroy(go);
            itemViews.Clear();
        }

        void IVElement.ReportState(string msg) { }

        object IVElement.GenerateItem(int type)
        {
            if (itemPrefab == null) return null;
            GameObject newItem = Instantiate(itemPrefab);
            newItem.SetActive(true);
            if (itemViews.Count > 256) itemViews.RemoveAll(v => v == null);   // views destroyed downstream (sinks)
            itemViews.Add(newItem);
            // the engine passes the new item's id: views that vary by item (e.g. MaterialVariants) are told which one they show
            foreach (var init in newItem.GetComponentsInChildren<IItemViewInit>(true)) init.OnItemCreated(type);

            // Apply separation offset for visual stacking
            Vector3 offset = new Vector3(0f, separation, 0f);
            newItem.transform.position = itemPosition.position + offset;
            
            return newItem;
        }

        void IVElement.LoadItem(Item vItem)
        {
            GameObject gItem = (GameObject)vItem.vItem;
            if (gItem != null)
                gItem.transform.position = itemPosition.position;
        }

        void IVElement.UnloadItem(Item vItem) { }

        /// <summary>True when the schedule table comes from the data table <see cref="dataTable"/>.</summary>
        public bool ReadsDataTable => UsesTable && !ReadsTableSource && dataTable != null;

        /// <summary>The mode reads a table (a schedule, or a rate table given as a table).</summary>
        public bool UsesTable => mode == SourceMode.Schedule || mode == SourceMode.RateTable;

        /// <summary>True when the schedule table comes from <see cref="tableSource"/> (SQL / a block breakdown part).</summary>
        public bool ReadsTableSource => UsesTable && SimuLean.Data.TableSources.IsSpec(tableSource);

        /// <summary>The database the table source reads: <see cref="tableDatabase"/>, else the project database.</summary>
        public string TableDatabasePath() => string.IsNullOrWhiteSpace(tableDatabase)
            ? ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot)
            : tableDatabase;

        public void LoadDataInEditor()
        {
            #if UNITY_EDITOR
            if (!ReadsTableSource) return;
            try
            {
                // a preview copy of what a run will read
                arrivalsTable = SimuLean.Data.TableSources.Read(tableSource, TableDatabasePath());
                if (arrivalsTable != null && arrivalsTable.Count > 0)
                {
                    serializedData = SerializableDataDict.FromDict(arrivalsTable);
                    arrivalsTableLoaded = true;
                    UnityEditor.EditorUtility.SetDirty(this);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[UnitySource] Error: {ex.Message}");
            }
            #endif
        }

        public void ClearData()
        {
            arrivalsTable = null;
            serializedData = null;
            arrivalsTableLoaded = false;
            #if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
            #endif
        }

        public List<string> GetColumnNames()
        {
            EnsureDataLoaded();
            return arrivalsTable != null ? new List<string>(arrivalsTable.Keys) : new List<string>();
        }

        public List<string> GetItemNames()
        {
            EnsureDataLoaded();
            return arrivalsTable != null && arrivalsTable.ContainsKey("Name") ? new List<string>(arrivalsTable["Name"]) : new List<string>();
        }

        public bool IsDataLoaded() => serializedData != null && serializedData.columnNames.Count > 0;

        public Dictionary<string, List<string>> GetArrivalsTable()
        {
            // Only ScheduleSource has ArrivalsTable
            if (theSource is ScheduleSource schedSource)
                return schedSource.ArrivalsTable;
            
            // For InterArrival mode, return the cached data if available
            EnsureDataLoaded();
            return arrivalsTable != null ? new Dictionary<string, List<string>>(arrivalsTable) : null;
        }

        /// <summary>The table this component holds (loaded in the inspector or set), never the last run's: a run starts from
        /// it (else a Restart after reloading the data would use the previous run's table).</summary>
        private Dictionary<string, List<string>> LoadedCopy()
        {
            EnsureDataLoaded();
            return arrivalsTable != null ? new Dictionary<string, List<string>>(arrivalsTable) : null;
        }

        public void SetArrivalsTable(Dictionary<string, List<string>> newDataDict)
        {
            arrivalsTable = newDataDict;
            arrivalsTableLoaded = true;
        }

        public int GetItemCount()
        {
            // Only ScheduleSource has data item count
            if (theSource is ScheduleSource schedSource)
                return schedSource.GetDataItemCount();
            
            // For InterArrival mode, use cached data
            EnsureDataLoaded();
            return arrivalsTable != null && arrivalsTable.Count > 0 ? arrivalsTable.Values.First().Count : 0;
        }

        private void EnsureDataLoaded()
        {
            if (arrivalsTableLoaded || arrivalsTable != null) return;
            if (serializedData != null && serializedData.columnNames.Count > 0)
            {
                arrivalsTable = serializedData.ToDict();
                arrivalsTableLoaded = true;
            }
        }

        /// <summary>
        /// The schedule table a run starts with when the source has no data table: the table source read now, else the
        /// loaded copy (the inspector preview, or rows set from code). <paramref name="warning"/> tells when the table source
        /// could not be read and the copy is used. The loaded copy (the scene) is not changed.
        /// </summary>
        public Dictionary<string, List<string>> TableForRun(out string warning)
        {
            warning = null;
            if (ReadsTableSource)
            {
                try
                {
                    var read = SimuLean.Data.TableSources.Read(tableSource, TableDatabasePath());
                    if (read != null && read.Count > 0) return read;
                    warning = $"Source '{name}': the table '{tableSource}' is empty; the run uses the loaded copy.";
                }
                catch (System.Exception ex)
                {
                    warning = $"Source '{name}': the table '{tableSource}' could not be read ({ex.Message}); the run uses the loaded copy.";
                }
            }
            return LoadedCopy();
        }
    }

    [System.Serializable]
    public class StringList
    {
        public List<string> values;

        public StringList()
        {
            values = new List<string>();
        }

        public StringList(List<string> values)
        {
            this.values = values;
        }
    }

    [System.Serializable]
    public class SerializableDataDict
    {
        public List<string> columnNames;
        public List<StringList> columnData;

        public SerializableDataDict()
        {
            columnNames = new List<string>();
            columnData = new List<StringList>();
        }

        public Dictionary<string, List<string>> ToDict()
        {
            if (columnNames == null || columnData == null || columnNames.Count != columnData.Count)
                return null;

            var dict = new Dictionary<string, List<string>>();
            for (int i = 0; i < columnNames.Count; i++)
            {
                dict[columnNames[i]] = columnData[i].values;
            }
            return dict;
        }

        public static SerializableDataDict FromDict(Dictionary<string, List<string>> dict)
        {
            if (dict == null)
                return null;

            var serializable = new SerializableDataDict();
            foreach (var kvp in dict)
            {
                serializable.columnNames.Add(kvp.Key);
                serializable.columnData.Add(new StringList(kvp.Value));
            }
            return serializable;
        }
    }
}
