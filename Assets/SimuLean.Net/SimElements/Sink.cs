namespace SimuLean
{
    /// <summary>
    /// End of the line: accepts every item (unless its input strategy refuses it) and counts it. Flow statistics come
    /// from the link layer, as for every element. With <see cref="KeepItems"/> it also keeps them (a finished-goods area,
    /// the erected hull) — at rest for the run monitor (<see cref="IKeepsItems"/>).
    /// </summary>
    public class Sink : Element, IKeepsItems
    {
        int numberItems;
        private readonly System.Collections.Generic.List<Item> kept = new System.Collections.Generic.List<Item>();

        /// <summary>Keep the received items (default off: they are only counted).</summary>
        public bool KeepItems { get; set; }

        /// <summary>The items kept since the last <see cref="Start"/> (empty without <see cref="KeepItems"/>).</summary>
        public System.Collections.Generic.IReadOnlyList<Item> Kept => kept;

        public int KeptCount => kept.Count;
        public double LastKeptTime { get; private set; } = double.NaN;

        public Sink(string name, SimClock state, IVElement vElement = null) : base(name, state, vElement)
        {
            StatsCollector.TracksStay = false;   // nothing leaves a sink: no stay to measure, no entry times kept
        }

        /// <summary>Items received since the last <see cref="Start"/>.</summary>
        public int GetNumberItems() => numberItems;

        public override void Start()
        {
            numberItems = 0;
            kept.Clear();
            LastKeptTime = double.NaN;
            base.Start();
        }

        /// <summary>Nothing to send (Resume calls Unblock on every element).</summary>
        public override bool Unblock() => false;

        public override int GetQueueLength() => 0;

        /// <summary>Unbounded (negative).</summary>
        public override int GetFreeCapacity() => -1;

        public override bool Receive(Item theItem)
        {
            numberItems++;
            if (KeepItems) { kept.Add(theItem); LastKeptTime = simClock.GetSimulationTime(); }
            SetState(ElementState.PROCESSING);
            vElement.LoadItem(theItem);
            return true;
        }

        public override bool CheckAvailability(Item theItem) => true;   // admission: the input strategy, checked by the link (CanAccept)
    }
}
