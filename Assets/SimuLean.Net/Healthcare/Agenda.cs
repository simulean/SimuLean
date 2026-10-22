using System;
using System.Collections.Generic;
using SimuLean.Downtime;
using SimuLean.Expressions;
using SimuLean.Statistics;

namespace SimuLean.Healthcare
{
    /// <summary>What a priority patient does when no reserved slot is free (<see cref="Agenda.Overflow"/>).</summary>
    public enum AgendaOverflow
    {
        /// <summary>The earliest free slot, reserved or ordinary.</summary>
        Ordinary,
        /// <summary>Only reserved slots: it waits for the next free one.</summary>
        WaitReserved,
        /// <summary>The next session: its free reserved slot (with no reserved slots: its free slot), else an extra slot
        /// squeezed into it (at most <see cref="Agenda.OverbookPerSession"/> a session); else the earliest free slot.</summary>
        Overbook,
    }

    /// <summary>
    /// An appointment book: a patient that
    /// enters (a referral) is <b>booked</b> into the first free slot of the agenda's sessions at least
    /// <see cref="LeadTime"/> ahead, waits in the agenda (at home) and is released at its appointment time to the next
    /// element (the consultation). Sessions are a weekly pattern (<see cref="WeeklyShiftPattern"/>, e.g.
    /// <c>Mon-Fri 08:30-12:30</c>) cut into slots of <see cref="SlotLength"/>, on the model calendar with the model's
    /// holidays (I7) and the agenda's own. The first <see cref="ReservedPerSession"/> slots of every session are kept for
    /// priority patients (<see cref="PriorityLabel"/> ≠ 0); <see cref="Overflow"/> says what they do when none is free.
    /// A <see cref="NoShow"/> probability (an expression over the patient, drawn from the agenda's stream) marks
    /// <c>no_show</c> at the appointment — routing decides what happens next. Labels stamped: <c>booked_at</c>,
    /// <c>appt_time</c>, <c>lead_time</c>, <c>no_show</c>. With load &lt; 1 the waits are those of the booking (weeks), not
    /// of a server (hours). The visit station needs no shift: the agenda releases patients at their slot times.
    /// </summary>
    public sealed class Agenda : Element
    {
        private sealed class Slot
        {
            public double Time;
            public bool Reserved;
            public Item Booked;
            public int Session;
            public bool Extra;      // squeezed into a session (overbooking): never offered when free
        }

        // the templates in force from a date (the first one from the start), sorted
        private readonly List<(DateTime from, WeeklyShiftPattern pattern, double slot)> templates =
            new List<(DateTime, WeeklyShiftPattern, double)>();
        private readonly double slotLength;
        private int sessionCount;
        private readonly Dictionary<int, int> extrasBySession = new Dictionary<int, int>();
        private readonly Dictionary<Item, Slot> bookedSlot = new Dictionary<Item, Slot>();
        private readonly Dictionary<Item, EventHandle> cancellations = new Dictionary<Item, EventHandle>();
        private int cancelled, overbooked;
        private Expression cancel;
        private readonly List<Slot> slots = new List<Slot>();
        private DateTime generatedUntil;
        private bool generationStarted;
        private int firstFreeOrdinary, firstFreeReserved;

        // patients waiting for their appointment; due patients that could not leave yet (oldest first)
        private readonly HashSet<Item> waiting = new HashSet<Item>();
        private readonly LinkedList<Item> due = new LinkedList<Item>();
        private readonly Dictionary<Item, LinkedListNode<Item>> dueNodes = new Dictionary<Item, LinkedListNode<Item>>();
        private readonly Dictionary<Item, EventHandle> appointments = new Dictionary<Item, EventHandle>();

        private double statsFrom;
        private int noShows;
        private readonly TallySamples leadAll = new TallySamples(), leadPriority = new TallySamples();
        private Expression noShow;
        [ThreadStatic] private static EntityScope scope;

        /// <summary>The longest look-ahead for a free slot from now (years of sessions): beyond it the booking fails.</summary>
        public const int HorizonDays = 3660;

        /// <param name="sessions">The weekly sessions (its holidays are the agenda's own; the model's are added).</param>
        /// <param name="slotLength">Length of a slot, in model time units.</param>
        public Agenda(string name, SimClock clock, WeeklyShiftPattern sessions, double slotLength, IVElement vElement = null)
            : base(name, clock, vElement)
        {
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            if (sessions.Windows.Count == 0) throw new ArgumentException($"Agenda '{name}': no sessions.");
            if (!(slotLength > 0)) throw new ArgumentOutOfRangeException(nameof(slotLength), $"Agenda '{name}': the slot length must be > 0.");
            this.slotLength = slotLength;
            templates.Add((DateTime.MinValue, sessions, slotLength));
        }

        /// <summary>Another template from a date on (the template of a period: e.g. from April, 14 slots a session instead
        /// of 12). Its holidays are its own; the model's are added. Add them before the run.</summary>
        public void AddTemplate(DateTime from, WeeklyShiftPattern sessions, double slotLength)
        {
            if (sessions == null || sessions.Windows.Count == 0) throw new ArgumentException($"Agenda '{GetName()}': a template from {from:yyyy-MM-dd} has no sessions.");
            if (!(slotLength > 0)) throw new ArgumentOutOfRangeException(nameof(slotLength), $"Agenda '{GetName()}': the slot length must be > 0.");
            templates.Add((from.Date, sessions, slotLength));
            templates.Sort((a, b) => a.from.CompareTo(b.from));
        }

        /// <summary>The templates (from-date, sessions, slot length), the first from the start.</summary>
        public IReadOnlyList<(DateTime from, WeeklyShiftPattern pattern, double slot)> Templates => templates;

        /// <summary>Overbook: extra slots allowed in a session for priority patients.</summary>
        public int OverbookPerSession { get; set; } = 1;

        /// <summary>Cancellation probability, an expression over the patient and the agenda, drawn at the booking: a patient
        /// that cancels does so at a random time before the appointment, its slot is freed and it books again (labels
        /// <c>rescheduled</c>); null = none.</summary>
        public string Cancel
        {
            get => cancelText;
            set
            {
                cancelText = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (cancelText == null) { cancel = null; return; }
                string error = Expression.Check(cancelText);
                if (error != null) throw new FormatException($"Agenda '{GetName()}': cancellation '{cancelText}': {error}");
                cancel = Expression.Parse(cancelText);
            }
        }
        private string cancelText;

        /// <summary>The patient walks to its visit (the agenda's output is a <see cref="WalkLink"/>): release it its walk
        /// earlier, so that it arrives at its slot time. The walk is the one the model plans on the network from
        /// the agenda's location — the longest towards its destinations when it has several. False = released at the slot
        /// time (it arrives its walk late).</summary>
        public bool ArriveOnTime { get; set; }

        // the longest walk to the destinations (model time; 0 without a walking output)
        private double WalkAhead() => GetOutput() is WalkLink walk ? walk.LongestWalk() : 0.0;

        /// <summary>Minimum time between the booking and the appointment (model time units; 0 = the next free slot).</summary>
        public double LeadTime { get; set; }
        /// <summary>Slots at the start of every session kept for priority patients.</summary>
        public int ReservedPerSession { get; set; }
        /// <summary>The label that makes a patient a priority one (a value other than 0 / empty).</summary>
        public string PriorityLabel { get; set; } = "priority";
        public AgendaOverflow Overflow { get; set; } = AgendaOverflow.Ordinary;

        /// <summary>No-show probability, an expression over the patient and the agenda (e.g. <c>0.08</c>,
        /// <c>item.dx == 'control' ? 0.12 : 0.05</c>); null = none.</summary>
        public string NoShow
        {
            get => noShowText;
            set
            {
                noShowText = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (noShowText == null) { noShow = null; return; }
                string error = Expression.Check(noShowText);
                if (error != null) throw new FormatException($"Agenda '{GetName()}': no-show '{noShowText}': {error}");
                noShow = Expression.Parse(noShowText);
            }
        }
        private string noShowText;

        public double SlotLength => slotLength;

        public override void Start()
        {
            slots.Clear();
            generationStarted = false;
            StartCalendar();
            firstFreeOrdinary = firstFreeReserved = 0;
            sessionCount = 0;
            extrasBySession.Clear(); bookedSlot.Clear(); cancellations.Clear();
            waiting.Clear(); due.Clear(); dueNodes.Clear(); appointments.Clear();
            ResetAgendaStatistics();
            // a patient on time goes to its appointment even if the room is still busy: it waits at the door
            if (ArriveOnTime && GetOutput() is WalkLink walk) walk.WaitAtDoor = true;
            base.Start();
            SetState(ElementState.IDLE);
        }

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            ResetAgendaStatistics();
        }

        private void ResetAgendaStatistics()
        {
            statsFrom = simClock.GetSimulationTime();
            noShows = 0;
            cancelled = overbooked = 0;
            leadAll.Clear(); leadPriority.Clear();
        }

        // ── the element contract ──────────────────────────────────────────────

        public override int GetQueueLength() => waiting.Count + due.Count;
        public override int GetFreeCapacity() => -1;
        public override bool CheckAvailability(Item theItem) => true;

        /// <summary>Patients waiting for their appointment, then those due that could not leave yet.</summary>
        public IEnumerable<Item> Items
        {
            get
            {
                foreach (var it in waiting) yield return it;
                foreach (var it in due) yield return it;
            }
        }

        public override bool Receive(Item theItem)
        {
            theItem.SetLabelValue("booked_at", simClock.GetSimulationTime());
            waiting.Add(theItem);
            BookAppointment(theItem);
            vElement.LoadItem(theItem);
            UpdateState();
            return true;
        }

        // books the patient's slot (from now + the lead time) and its appointment, and draws its cancellation
        private void BookAppointment(Item theItem)
        {
            double now = simClock.GetSimulationTime();
            bool priority = IsPriority(theItem);
            Slot slot = Book(now + Math.Max(0, LeadTime), priority);
            slot.Booked = theItem;
            bookedSlot[theItem] = slot;
            if (!slot.Extra) AdvanceFirstFree(slot.Reserved);
            double booked = theItem.GetLabelOrDefault("booked_at", now);
            theItem.SetLabelValue("appt_time", slot.Time);
            theItem.SetLabelValue("lead_time", slot.Time - booked);
            // released at its slot, or its walk earlier so that it arrives at the slot (ArriveOnTime)
            double release = Math.Max(now, slot.Time - (ArriveOnTime ? WalkAhead() : 0.0));
            appointments[theItem] = simClock.ScheduleEvent(new AppointmentEvent(this, theItem, priority), release - now);
            if (cancel != null && release > now)
            {
                var sc = (scope ?? (scope = new EntityScope())).Set(theItem, this);
                double p = cancel.Number(sc);
                sc.Set(null, null);
                if (p > 0 && ExpressionRandom.NextDouble() < p)
                    cancellations[theItem] = simClock.ScheduleEvent(new CancelEvent(this, theItem),
                                                                     ExpressionRandom.NextDouble() * (release - now));
            }
        }

        // the patient cancels: its slot is free again and it books another one
        private void Cancelled(Item item)
        {
            cancellations.Remove(item);
            if (!waiting.Contains(item) || !bookedSlot.TryGetValue(item, out var slot)) return;
            if (appointments.TryGetValue(item, out var handle)) { handle.Cancel(); appointments.Remove(item); }
            slot.Booked = null;
            int i = IndexOf(slot);
            if (slot.Extra)
            {
                // an extra slot exists only for its patient: it goes, and the session gets its overbooking back (review2 #6)
                slots.RemoveAt(i);
                if (firstFreeOrdinary > i) firstFreeOrdinary--;
                if (firstFreeReserved > i) firstFreeReserved--;
                if (extrasBySession.TryGetValue(slot.Session, out int extras)) extrasBySession[slot.Session] = Math.Max(0, extras - 1);
                overbooked = Math.Max(0, overbooked - 1);
            }
            else if (slot.Reserved) firstFreeReserved = Math.Min(firstFreeReserved, i);
            else firstFreeOrdinary = Math.Min(firstFreeOrdinary, i);
            cancelled++;
            item.SetLabelValue("rescheduled", item.GetLabelOrDefault("rescheduled") + 1);
            BookAppointment(item);
        }

        private int IndexOf(Slot slot)
        {
            int i = Math.Max(0, LowerBound(slot.Time));
            while (i < slots.Count && !ReferenceEquals(slots[i], slot)) i++;
            return i;
        }

        private bool IsPriority(Item item)
        {
            if (string.IsNullOrEmpty(PriorityLabel) || item == null || !item.HasLabel(PriorityLabel)) return false;
            double? v = item.GetLabelValue(PriorityLabel);
            if (v.HasValue) return v.Value != 0;
            string t = item.GetLabelText(PriorityLabel);
            return !string.IsNullOrEmpty(t) && t != "0";
        }

        private void Appointment(Item item, bool priority)
        {
            appointments.Remove(item);
            bookedSlot.Remove(item);
            if (!waiting.Remove(item)) return;
            double now = simClock.GetSimulationTime();
            double lead = item.GetLabelOrDefault("lead_time");
            leadAll.Add(lead);
            if (priority) leadPriority.Add(lead);
            bool absent = false;
            if (noShow != null)
            {
                var s = (scope ?? (scope = new EntityScope())).Set(item, this);
                double p = noShow.Number(s);
                s.Set(null, null);
                absent = p > 0 && ExpressionRandom.NextDouble() < p;
            }
            item.SetLabelValue("no_show", absent ? 1 : 0);
            if (absent) noShows++;
            // in order: a patient due while others wait to leave goes behind them
            if (due.Count == 0 && SendOut(item)) vElement.ReportState("Exit");
            else
            {
                dueNodes[item] = due.AddLast(item);
                if (due.Count > 1 && GetOutput() is ListLink list) list.Advertise(item, this);
            }
            UpdateState();
        }

        public override bool Unblock()
        {
            if (due.Count == 0) return false;
            Item head = due.First.Value;
            if (!SendOut(head)) { UpdateState(); return false; }
            Take(head);
            vElement.ReportState("Exit");
            UpdateState();
            return true;
        }

        public override bool SendHeldItem(Item item)
        {
            if (item == null || !dueNodes.ContainsKey(item) || !SendOut(item)) return false;
            Take(item);
            vElement.ReportState("Exit");
            UpdateState();
            return true;
        }

        private void Take(Item item)
        {
            if (dueNodes.TryGetValue(item, out var node)) { due.Remove(node); dueNodes.Remove(item); }
        }

        private void UpdateState()
            => SetState(due.Count > 0 ? ElementState.BLOCKED : waiting.Count > 0 ? ElementState.PROCESSING : ElementState.IDLE);

        // ── slots ─────────────────────────────────────────────────────────────

        // The first free slot at or after `from` that the patient may take.
        private Slot Book(double from, bool priority)
        {
            bool reservedOnly = priority && ReservedPerSession > 0 && Overflow == AgendaOverflow.WaitReserved;
            Slot ordinary = reservedOnly ? null : FirstFree(from, reserved: false);
            Slot reserved = priority && ReservedPerSession > 0 ? FirstFree(from, reserved: true) : null;
            if (priority && Overflow == AgendaOverflow.Overbook)
            {
                // the next session: its free reserved slot, else an extra slot squeezed into it
                int next = LowerBound(from);
                while (next >= slots.Count) if (!Generate()) break;
                if (next < slots.Count)
                {
                    int session = slots[next].Session;
                    if (reserved != null && reserved.Session == session) return reserved;
                    // no reserved slots: a free slot of that session before an extra one (review2 #28: every priority patient
                    // was squeezed in as an extra although the session had room); with reserved slots the ordinary ones
                    // stay for the ordinary patients (by design)
                    if (ReservedPerSession == 0 && ordinary != null && ordinary.Session == session) return ordinary;
                    extrasBySession.TryGetValue(session, out int extras);
                    if (extras < OverbookPerSession) return Squeeze(session, next);
                }
            }
            Slot pick = reserved == null ? ordinary : ordinary == null ? reserved : (reserved.Time <= ordinary.Time ? reserved : ordinary);
            return pick ?? throw new InvalidOperationException($"Agenda '{GetName()}': no free slot within {HorizonDays} days.");
        }

        // an extra slot at the end of a session (the time of its last slot), kept in time order
        private Slot Squeeze(int session, int from)
        {
            int last = from;
            while (last + 1 < slots.Count && slots[last + 1].Session == session) last++;
            var extra = new Slot { Time = slots[last].Time, Reserved = true, Session = session, Extra = true };
            int at = last + 1;
            slots.Insert(at, extra);
            if (firstFreeOrdinary >= at) firstFreeOrdinary++;
            if (firstFreeReserved >= at) firstFreeReserved++;
            extrasBySession[session] = (extrasBySession.TryGetValue(session, out int n) ? n : 0) + 1;
            overbooked++;
            return extra;
        }

        private Slot FirstFree(double from, bool reserved)
        {
            int i = Math.Max(reserved ? firstFreeReserved : firstFreeOrdinary, LowerBound(from));
            while (true)
            {
                while (i >= slots.Count)
                    if (!Generate()) return null;
                var s = slots[i];
                if (s.Reserved == reserved && !s.Extra && s.Booked == null && s.Time >= from - 1e-9) return s;
                i++;
            }
        }

        // the first free slot of a kind moves on once it is taken (slots are never freed: a no-show is in the past)
        private void AdvanceFirstFree(bool reserved)
        {
            ref int k = ref (reserved ? ref firstFreeReserved : ref firstFreeOrdinary);
            while (k < slots.Count && (slots[k].Reserved != reserved || slots[k].Extra || slots[k].Booked != null)) k++;
        }

        private int LowerBound(double time)
        {
            if (!generationStarted) Generate();
            int lo = 0, hi = slots.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (slots[mid].Time < time - 1e-9) lo = mid + 1; else hi = mid;
            }
            // the slots up to `time` may not be generated yet
            while (lo == slots.Count && slots.Count > 0 && slots[slots.Count - 1].Time < time - 1e-9 && Generate())
                return LowerBound(time);
            return lo;
        }

        // Adds the slots of the next week of sessions; false past the horizon.
        private bool Generate()
        {
            var cal = simClock.Context.Calendar;
            if (!generationStarted) StartCalendar();
            // the look-ahead counts from now, not from the start of the model (review2 #4: bookings failed after ten years)
            DateTime origin = cal.ToDateTime(simClock.GetSimulationTime()).Date;
            if ((generatedUntil - origin).TotalDays > HorizonDays) return false;
            DateTime to = generatedUntil.AddDays(7);
            for (DateTime day = generatedUntil; day < to; day = day.AddDays(1))
                foreach (var (ts, slot, n) in SessionsOn(day))
                {
                    int session = sessionCount++;
                    for (int k = 0; k < n; k++)
                        slots.Add(new Slot { Time = ts + k * slot, Reserved = k < ReservedPerSession, Session = session });
                }
            generatedUntil = to;
            return true;
        }

        // the sessions starting on a day: start time, slot length and number of slots
        private IEnumerable<(double start, double slot, int count)> SessionsOn(DateTime day)
        {
            var cal = simClock.Context.Calendar;
            var (_, pattern, slot) = TemplateOn(day);
            foreach (var (s, e) in pattern.MergedWindows(day, day.AddDays(1)))
            {
                if (s < day || s >= day.AddDays(1)) continue;   // each window once: the day it starts on
                double ts = cal.ToSimTime(s), te = cal.ToSimTime(e);
                yield return (ts, slot, (int)Math.Floor((te - ts) / slot + 1e-6));
            }
        }

        // the model's holidays into the sessions (I7) and the first day to generate
        private void StartCalendar()
        {
            if (generationStarted) return;
            generationStarted = true;
            foreach (var t in templates)
                foreach (var h in simClock.Context.Holidays) t.pattern.AddHoliday(h);   // the model's working calendar (I7)
            generatedUntil = simClock.Context.Calendar.ToDateTime(0).Date;
        }

        // the template in force on a day: the last one from that date or before
        private (DateTime from, WeeklyShiftPattern pattern, double slot) TemplateOn(DateTime day)
        {
            var t = templates[0];
            foreach (var x in templates) if (x.from <= day) t = x;
            return t;
        }

        // ── statistics (RunResults reads them, ResultMetrics names) ───────────

        /// <summary>Slots whose time has come since the statistics started (warm-up included), the booked ones among them,
        /// the reserved ones nobody took.</summary>
        public (int offered, int booked, int reservedUnused) SlotCounts()
        {
            // only reads: the sessions up to now that no booking has generated yet are counted, not generated (a capture
            // must not change the model; review2 #7) - none of their slots is booked
            double now = simClock.GetSimulationTime();
            int offered = 0, booked = 0, unused = 0;
            foreach (var s in slots)
            {
                if (s.Time < statsFrom - 1e-9) continue;
                if (s.Time >= now) break;
                if (s.Extra) continue;          // overbooked: counted apart
                offered++;
                if (s.Booked != null) booked++;
                else if (s.Reserved) unused++;
            }
            if (!generationStarted) return (offered, booked, unused);
            DateTime last = simClock.Context.Calendar.ToDateTime(now).Date;
            for (DateTime day = generatedUntil; day <= last; day = day.AddDays(1))
                foreach (var (ts, slot, n) in SessionsOn(day))
                    for (int k = 0; k < n; k++)
                    {
                        double t = ts + k * slot;
                        if (t < statsFrom - 1e-9 || t >= now) continue;
                        offered++;
                        if (k < ReservedPerSession) unused++;
                    }
            return (offered, booked, unused);
        }

        /// <summary>Slots generated so far (they are generated a week at a time as bookings need them).</summary>
        public int GeneratedSlotCount => slots.Count;

        public int NoShows => noShows;
        /// <summary>Appointments cancelled (and booked again) and extra slots squeezed in, since the statistics started.</summary>
        public int Cancellations => cancelled;
        public int Overbooked => overbooked;
        /// <summary>Booking → appointment of the patients seen (all, and the priority ones).</summary>
        public TallySamples LeadTimes => leadAll;
        public TallySamples PriorityLeadTimes => leadPriority;

        /// <summary>Time from now to the first free ordinary slot a new referral could take (the booking horizon).</summary>
        public double BookingHorizon()
        {
            // only reads (review2 #7): the generated slots first, then the sessions not generated yet (all free)
            double now = simClock.GetSimulationTime(), from = now + Math.Max(0, LeadTime);
            for (int i = firstFreeOrdinary; i < slots.Count; i++)
                if (!slots[i].Reserved && !slots[i].Extra && slots[i].Booked == null && slots[i].Time >= from - 1e-9) return slots[i].Time - now;
            if (!generationStarted) return double.NaN;
            DateTime end = simClock.Context.Calendar.ToDateTime(now).Date.AddDays(HorizonDays);
            for (DateTime day = generatedUntil; day <= end; day = day.AddDays(1))
                foreach (var (ts, slot, n) in SessionsOn(day))
                    for (int k = ReservedPerSession; k < n; k++)
                        if (ts + k * slot >= from - 1e-9) return ts + k * slot - now;
            return double.NaN;
        }

        private sealed class CancelEvent : IEvent
        {
            private readonly Agenda owner; private readonly Item item;
            public CancelEvent(Agenda owner, Item item) { this.owner = owner; this.item = item; }
            void IEvent.Execute() => owner.Cancelled(item);
        }

        private sealed class AppointmentEvent : IEvent
        {
            private readonly Agenda owner; private readonly Item item; private readonly bool priority;
            public AppointmentEvent(Agenda owner, Item item, bool priority) { this.owner = owner; this.item = item; this.priority = priority; }
            void IEvent.Execute() => owner.Appointment(item, priority);
        }
    }
}
