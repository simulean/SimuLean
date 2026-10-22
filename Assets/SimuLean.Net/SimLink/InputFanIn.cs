using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// The input of an element fed by several links (several upstream elements, each with its own link, or a
    /// list plus connections). When the element has room (<see cref="NotifyAvailable"/>) every input link is
    /// asked in turn — round robin, starting after the one served last, so no upstream starves — until one
    /// sends. Created by <see cref="Element.SetInput"/> when a second input link is added; an element with one
    /// input keeps that link directly. Never used to send items.
    /// </summary>
    internal sealed class InputFanIn : ILink
    {
        private readonly List<ILink> links = new List<ILink>();
        private int next;

        public InputFanIn(ILink first, ILink second) { links.Add(first); links.Add(second); }

        public IReadOnlyList<ILink> Links => links;

        public void Add(ILink link) { if (!links.Contains(link)) links.Add(link); }

        public bool NotifyAvailable(Element source)
        {
            int n = links.Count;
            for (int k = 0; k < n; k++)
            {
                int i = (next + k) % n;
                if (links[i].NotifyAvailable(source))
                {
                    next = (i + 1) % n;
                    return true;
                }
            }
            return false;
        }

        public bool SendItem(Item theItem, Element source)
            => throw new InvalidOperationException("An element's input fan-in never sends items.");
    }
}
