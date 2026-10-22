using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity Component for GateQueue Element: a buffer whose items leave only when they are released
    /// (<see cref="Release"/>, from the inspector in Play mode, a UI button or a script).
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Buffers, "Gate Queue", Description = "Buffer that releases items when its gate opens", Prefab = "GateQueue.prefab", Order = 1)]
    public class UnityGateQueue : SElement, IVElement
    {
        [Tooltip("Maximum number of items the gate queue holds")]
        public int capacity = 1;



        [Tooltip("Spacing of the stored items (x, y, z)")]
        public double xSeparation = 1.0, ySeparation = 1.0, zSeparation = 1.0;
        [Tooltip("Stored items per column (y) and per row (z) before the next layer")]
        public int yLevels = 2, zLevels = 5;

        [Tooltip("Where the stored items are placed (empty = this object)")]
        public Transform storagePosition;

        GateQueue myGateQueue;

        /// <summary>Opens the gate for <paramref name="quantity"/> more items.</summary>
        public void Release(int quantity)
        {
            if (myGateQueue != null && quantity > 0) myGateQueue.Release(quantity);
        }

        /// <summary>Items released but not sent yet (the output was full).</summary>
        public int PendingRelease => myGateQueue?.GetPendingItems() ?? 0;

        protected override void OnBound(Element built)
        {
            myGateQueue = (GateQueue)built;
            if (storagePosition == null) storagePosition = transform;
        }

        void IVElement.ReportState(string msg)
        {
            Queue<Item> items = myGateQueue.GetItems();
            int i = 0;
            foreach (Item it in items)
            {
                int yL = i % yLevels;
                int zL = i / yLevels % zLevels;
                int xL = i / (yLevels * zLevels);
                if (it.vItem is GameObject gItem)
                    gItem.transform.position = storagePosition.position + new Vector3((float)(xL * xSeparation), (float)(yL * ySeparation), (float)(zL * zSeparation));
                i++;
            }
        }

        object IVElement.GenerateItem(int type)
        {
            return null;
        }

        void IVElement.LoadItem(Item vItem)
        {
            ((IVElement)this).ReportState("");
        }

        void IVElement.UnloadItem(Item vItem)
        {
            ((IVElement)this).ReportState("");
        }

        public override void ClearViews()
        {
            foreach (Item it in myGateQueue.GetItems())
                if (it.vItem is GameObject go) Destroy(go);
        }
    }
}
