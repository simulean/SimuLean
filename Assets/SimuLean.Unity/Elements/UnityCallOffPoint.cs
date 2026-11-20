using System;
using System.Collections.Generic;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A call-off family in the scene (shared by <see cref="UnityCallOffPoint"/> and the stations of
    /// <see cref="UnityPacedLine"/>): what is called off / fitted per vehicle and where the orders go. Engine:
    /// <see cref="CallOffFamily"/>.
    /// </summary>
    [Serializable]
    public class CallOffFamilyDef
    {
        [Tooltip("Family name (labels <family>.p<k>.<reference label> on the vehicle). Empty in a line station = the station name.")]
        public string name = "Family";
        [Tooltip("Item type of the call-off orders.")]
        public string orderType = "CallOff";
        [Tooltip("Pieces per vehicle (positions 1..q, one order each, in position order).")]
        [Min(1)] public int quantity = 1;
        [Tooltip("Only vehicles meeting this condition need the family (expression, e.g. item.model == \"K9\"; empty = all).")]
        [SimuLean.Unity.SimuLeanExpression("callOff")]
        public string condition = "";
        [Tooltip("Label of the drawn reference on each order.")]
        public string referenceLabel = "reference";
        [Tooltip("Reference choice tables 'R1:0.28|R2:0.24|…': one per position, or one for all positions (empty = no reference).")]
        public List<string> referenceTables = new List<string>();
        [Tooltip("Where the orders go (a queue, a list consumer, a sequencing station…). Empty = discarded (counted).")]
        public SElement destination;

        public CallOffFamilySettings ToSettings(string fallbackName = "") => new CallOffFamilySettings
        {
            Name = string.IsNullOrWhiteSpace(name) ? fallbackName ?? "" : name.Trim(),
            OrderType = orderType,
            Quantity = Mathf.Max(1, quantity),
            Condition = condition ?? "",
            ReferenceLabel = referenceLabel,
            ReferenceTables = referenceTables != null ? referenceTables.FindAll(t => !string.IsNullOrWhiteSpace(t)).ConvertAll(ReferenceTableSettings.Parse)
                                                      : new List<ReferenceTableSettings>(),
        };
    }

    /// <summary>
    /// Unity wrapper of <see cref="CallOffPoint"/>: the reporting point where each passing vehicle gets its sequence number
    /// and the orders of every family are sent to the family's destination. Put a vehicle buffer between it and the
    /// <see cref="UnityPacedLine"/>: the requisition time is then the buffer time + the line positions × takt.
    /// Built by <c>CallOffPointHandler</c> (one build path); the view only places the passing vehicle.
    /// </summary>
    [AddComponentMenu("SimuLean/Call-Off Point")]
    [SimuLeanElement(SimuLeanModules.Assembly, "Call-Off Point", Model = "AssemblyLineKit/WorkstationPost.fbx", Description = "Reporting point: calls off parts for each passing vehicle", Order = 20, Pack = SimuLeanPacks.Manufacturing, Icon = "Assets/SimuLean.Unity/Art/Models/AssemblyLineKit/AndonBoard.fbx")]
    public class UnityCallOffPoint : SElement, IVElement
    {
        [Header("Call-off")]
        [Tooltip("What is called off for each vehicle, one entry per family.")]
        public List<CallOffFamilyDef> families = new List<CallOffFamilyDef> { new CallOffFamilyDef() };
        [Tooltip("Label with the vehicle sequence number given to vehicles without one (empty = none).")]
        public string sequenceLabel = "vehicle";
        [Tooltip("Renumber vehicles that already carry the sequence label.")]
        public bool overwriteSequence = false;

        private CallOffPoint point;

        /// <summary>The engine element of the run (null outside a run).</summary>
        public CallOffPoint Point => point;

        /// <summary>Writes its settings into the element's (the extractor's).</summary>
        public void Fill(CallOffPointSettings s)
        {
            s.SequenceLabel = sequenceLabel ?? "";
            s.OverwriteSequence = overwriteSequence;
            s.Families = new List<CallOffFamilySettings>();
            int i = 0;
            foreach (var f in families ?? new List<CallOffFamilyDef>())
            {
                i++;
                if (f != null) s.Families.Add(f.ToSettings("Family" + i));
            }
        }

        protected override void OnBound(Element built) => point = (CallOffPoint)built;

        public override void ClearViews()
        {
            if (point?.Held?.vItem is GameObject go && go != null) Destroy(go);
        }

        void IVElement.ReportState(string msg) { }
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item item)
        {
            if (item?.vItem is GameObject go && go != null) go.transform.position = transform.position;
        }
        void IVElement.UnloadItem(Item item) { }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.9f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * 0.05f, new Vector3(1.5f, 0.1f, 0.3f));
        }
    }
}
