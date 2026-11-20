namespace SimuLean
{
    /// <summary>
    /// A fixed one-to-one link from an element to one of its own port elements (e.g. an <see cref="Separator"/> handing
    /// an emptied carrier to its empties port). No routing strategy (the owner's output strategy is for its main
    /// output): the item goes to <see cref="Target"/> or nowhere. Like every link it is the one that records the exit
    /// and the entry (<c>OnExit</c> / <c>OnEntry</c>), so the statistics stay link-driven. Not set as anybody's output
    /// or input link: the owner keeps it privately and calls <see cref="SendItem"/>.
    /// </summary>
    public sealed class DirectLink : ILink
    {
        public DirectLink(Element target) { Target = target; }

        public Element Target { get; }

        public bool SendItem(Item theItem, Element source)
        {
            if (theItem == null || Target == null || source.IsOutputBlocked || Target.IsInputBlocked) return false;
            if (!Target.CheckAvailability(theItem) || !Target.Receive(theItem)) return false;
            source.StatsCollector.OnExit(theItem);
            Target.StatsCollector.OnEntry(theItem);
            return true;
        }

        public bool NotifyAvailable(Element source) => false;
    }

    /// <summary>
    /// The output link of a buffer whose items are <b>consumed</b> by its owner (e.g. a line station's buffer whose parts
    /// are fitted to the product): sending an item through it records its exit
    /// from the buffer (output count, content, stay time) and hands it to nobody — the owner keeps it (as a sub-item of
    /// the product, or discards it). This way consumption goes through the link layer like any other departure.
    /// </summary>
    public sealed class ConsumeLink : ILink
    {
        public static readonly ConsumeLink Instance = new ConsumeLink();

        public bool SendItem(Item theItem, Element source)
        {
            if (theItem == null || source == null) return false;
            source.StatsCollector.OnExit(theItem);
            return true;
        }

        public bool NotifyAvailable(Element source) => false;
    }
}
