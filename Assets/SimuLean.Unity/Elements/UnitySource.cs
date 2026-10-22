using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SimuLean.Unity
{
    public enum SourceMode
    {
        InterArrival,
        Schedule
    }

    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Sources, "Source", Description = "Creates items: inter-arrival distribution or a schedule table", Prefab = "Source.prefab", Order = 0)]
    public class UnitySource : SElement, IVElement
    {
        public override bool HasInput => false;

        private Element theSource;  // Can be either ScheduleSource or InterArrivalSource

        public GameObject itemPrefab;
        public float separation = 1f;
        public Transform itemPosition;

        public SourceMode mode = SourceMode.Schedule;

        [Tooltip("Labels given to every created item, e.g. prio = 5 (schedule-table columns of the same name override them).")]
        public List<LabelAssignment> assignLabels = new List<LabelAssignment> { new LabelAssignment { label = "prio", value = "0" } };


        [Tooltip("Demand factor: 1 = as given; 1.2 = 20 % more arrivals (the rate, or each table row's quantity, times the " +
                 "factor). A study parameter: run demand scenarios without editing the scene.")]
        [Min(0.01f)] public float demandFactor = 1f;

        [Tooltip("Time between arrivals distribution. If null or returns 0, items arrive immediately.")]
        public RandomProcessConfig interArrivalDistribution =
            new RandomProcessConfig { strategyType = RandomProcessType.Exponential, exponentialRate = 0.2 };   // random arrivals, mean 5 (review2 #43)

        [Tooltip("Schedule mode: the model's data table with the arrivals — one row per arrival: Time, Name, Q and " +
                 "any label columns. Its source (typed rows, a CSV / Excel file, an SQL query) is read when each run starts.")]
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
        public bool ReadsDataTable => mode == SourceMode.Schedule && !ReadsTableSource && dataTable != null;

        /// <summary>True when the schedule table comes from <see cref="tableSource"/> (SQL / a block breakdown part).</summary>
        public bool ReadsTableSource => mode == SourceMode.Schedule && SimuLean.Data.TableSources.IsSpec(tableSource);

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
        /// it (a Restart after reloading the data used the previous run's table).</summary>
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
