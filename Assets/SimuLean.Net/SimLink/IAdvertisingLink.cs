namespace SimuLean
{
    /// <summary>
    /// An output link that wants to know <b>every</b> item an origin holds, not only the one it offers: a list
    /// (<see cref="ListLink"/>: each item is an entry), or any link that lets its puller choose among the items held. Elements that hold several outgoing items (queues, lanes,
    /// release sources, delays, agendas) advertise the ones behind the first through it; the link later asks for a given
    /// item with <see cref="Element.SendHeldItem"/>. Items waiting behind such a link wait for a puller: they are not
    /// blocked by a refusing receiver.
    /// </summary>
    public interface IAdvertisingLink : ILink
    {
        /// <summary>An item <paramref name="origin"/> holds behind the one it offers. Idempotent.</summary>
        void Advertise(Item item, Element origin);
    }
}
