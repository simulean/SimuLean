using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// A Gate Queue is a normal queue that simulates manufacturing orders.
    /// </summary>
    public class GateQueue : Element
    {
        int capacity;
        int pendingRelease;
        int currentItems;

        Queue<Item> itemsQ;

        /// <param name="vElement">The view (null = headless).</param>
        public GateQueue(int capacity, String myName, SimClock sClock, IVElement vElement = null) 
            : base(myName, sClock, vElement)
        {
            this.capacity = capacity;
            itemsQ = new Queue<Item>(capacity);
        }

        public override void Start()
        {
            itemsQ.Clear();
            currentItems = 0;
            pendingRelease = 0;
            base.Start();
        }

        /// <summary>
        /// Releases <paramref name="quantity"/> number of manufacturing orders.
        /// </summary>
        /// <param name="quantity"></param>
        public void Release(int quantity)
        {
            pendingRelease += quantity;
            DoTransfers();
        }

        /// <summary>
        /// Do all pending manufacturing orders if possible. Returns how many items left.
        /// </summary>
        int DoTransfers()
        {
            int sent = 0;
            bool refused = false;
            while (pendingRelease > 0 && currentItems > 0)
            {
                Item theItem = itemsQ.Peek();

                pendingRelease--;
                currentItems--;

                if (SendOut(theItem))
                {
                    itemsQ.Dequeue();
                    sent++;
                    vElement.ReportState("Exit");
                    NotifyInput();   // a place is free: let a waiting upstream element send
                }
                else
                {
                    pendingRelease++;
                    currentItems++;
                    refused = true;   // output refused
                    break;
                }
            }

            // Final state: BLOCKED while a released order cannot leave (it was overwritten at once)
            if (refused) SetState(ElementState.BLOCKED);
            else if (currentItems == 0) SetState(ElementState.IDLE);
            else SetState(ElementState.PROCESSING);    // items waiting for their release
            return sent;
        }

        override public int GetQueueLength()
        {
            return currentItems;
        }

        public int GetPendingItems()
        {
            return pendingRelease;
        }

        override public int GetFreeCapacity()
        {
            return capacity - currentItems;
        }

        public override bool Unblock()
        {
            // true only when an item left: a false "unblocked" stopped the link from waking the other origins and made
            // the end of a stop pull in a loop
            return pendingRelease > 0 && currentItems > 0 && DoTransfers() > 0;
        }

        public override bool Receive(Item theItem)
        {
            // Note: Validation with origin context is done in GeneralLink.SendItem
            // This method assumes the item has already passed validation
            
            if (currentItems < capacity)
            {
                currentItems++;
                itemsQ.Enqueue(theItem);
                vElement.LoadItem(theItem);
                SetState(ElementState.PROCESSING); // item stored, awaiting gate release
                DoTransfers();
                return true;
            }
            else
            {
                return false;
            }
        }

        public override bool CheckAvailability(Item theItem)
        {
            if (currentItems >= capacity)
                return false;
            
            return true;   // admission: the input strategy, checked by the link (CanAccept)
        }

        public Queue<Item> GetItems()
        {
            Queue<Item> wholeItems = new Queue<Item>();

            foreach (Item it in itemsQ)
            {
                wholeItems.Enqueue(it);
            }

            return wholeItems;
        }

        public void SetCapacity(int capacity)
        {
            this.capacity = capacity;
        }
    }
}

