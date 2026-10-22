using System.Collections.Generic;
using SimuLean.Healthcare;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity component of an <see cref="Agenda"/>: an appointment book — patients are booked
    /// into the first free slot of its weekly sessions (at least the lead time ahead), wait at home, and are released to
    /// the next element (the consultation) at their appointment. The visit station needs no shift. Its waiting patients
    /// are drawn with the queue's layouts (<see cref="ItemLayout"/>), or hidden behind a count with an
    /// <see cref="UnityElementLabel"/>.
    /// </summary>
    [SimuLeanElement(SimuLeanModules.Healthcare, "Agenda",
        Description = "Appointment book: books each patient into the first free slot of its sessions and releases it at its appointment", Order = 0)]
    public class UnityAgenda : SElement, IVElement
    {
        private Agenda theAgenda;

        [Tooltip("The weekly sessions, e.g. 'Mon-Fri 08:30-12:30' or 'Mon,Wed 09:00-13:00; Fri 15:00-18:00' (model calendar).")]
        public string sessions = "Mon-Fri 08:30-12:30";
        [Tooltip("Length of a slot, in minutes (calendar time, whatever the model's time unit).")]
        [Min(0.01f)] public float slotMinutes = 20f;
        [Tooltip("Minimum time between the booking and the appointment, in days (0 = the next free slot).")]
        [Min(0f)] public float leadDays = 0f;
        [Tooltip("Slots at the start of every session kept for priority patients.")]
        [Min(0)] public int reservedPerSession = 0;
        [Tooltip("The label that makes a patient a priority one (a value other than 0 / empty).")]
        public string priorityLabel = "priority";
        [Tooltip("Ordinary: a priority patient takes the earliest free slot, reserved or not. Wait Reserved: only reserved slots.")]
        public AgendaOverflow overflow = AgendaOverflow.Ordinary;
        [Tooltip("No-show probability, an expression over the patient (e.g. 0.08, or item.dx == 'control' ? 0.12 : 0.05). " +
                 "Empty = none. Sets the label no_show (0 / 1): route on it.")]
        public string noShow = "";
        [Tooltip("The agenda's own holidays (dates), on top of the model's.")]
        public List<string> holidays = new List<string>();
        [Tooltip("Overbook: extra slots a session may take for priority patients.")]
        [Min(0)] public int overbookPerSession = 1;
        [Tooltip("Patients walk to their visit (Walk By on this agenda): release each one its walk earlier, so that it arrives " +
                 "at its slot time (the walk the model plans from this agenda's location).")]
        public bool arriveOnTime;
        [Tooltip("Cancellation probability, an expression over the patient (e.g. 0.05). A patient that cancels frees its slot " +
                 "and books again (label rescheduled). Empty = none.")]
        public string cancel = "";
        [Tooltip("Templates of later periods, 'date | sessions | slot minutes' (e.g. '2026-04-01 | Mon-Fri 08:30-13:10 | 20'): " +
                 "from that date the agenda uses them.")]
        public List<string> templates = new List<string>();
        [Tooltip("Or a data table of templates (columns From, Sessions, SlotMinutes).")]
        public UnityDataTable templateTable;

        // ── Visual Configuration (as a queue's) ─────────────────────────────
        [Tooltip("How the waiting patients are drawn: a grid on the footprint, a stack, the Seat nodes or a single line.")]
        public QueueLayoutMode layoutMode = QueueLayoutMode.Grid;
        [Tooltip("Distance between item centres. 0 = automatic, from the size of the items.")]
        public float separation = 0f;
        [Tooltip("Grid area (width X, depth Z). 0 = the element's footprint.")]
        public Vector2 gridArea = Vector2.zero;
        [Tooltip("Where the items go. Empty = the top of the element's footprint.")]
        public Transform itemPosition;
        [Tooltip("Line layout: length of one run before it folds back. 0 = one straight line.")]
        [Min(0f)] public float lineLength = 0f;

        /// <summary>Patients held now (waiting for their appointment or due).</summary>
        public int Content => theAgenda?.GetQueueLength() ?? 0;

        protected override void OnBound(Element built) => theAgenda = (Agenda)built;

        private void Reposition()
        {
            if (theAgenda != null) ItemLayout.Arrange(this, theAgenda.Items, layoutMode, separation, gridArea, itemPosition, lineLength);
        }

        void IVElement.ReportState(string msg) => Reposition();
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item vItem) => Reposition();
        void IVElement.UnloadItem(Item vItem) => Reposition();

        public override void ClearViews()
        {
            if (theAgenda == null) return;
            foreach (Item it in new List<Item>(theAgenda.Items))
                if (it.vItem is GameObject g && g != null) Destroy(g);
        }
    }
}
