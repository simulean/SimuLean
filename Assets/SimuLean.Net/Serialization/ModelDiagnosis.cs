using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Resources;

namespace SimuLean.Headless
{
    /// <summary>
    /// The state of a built model at the current time, for finding why it stalls or deadlocks (so no
    /// dump script of one's own is needed): the run monitor's verdict, every element
    /// that holds items, is stopped or is not idle (state, items, what it waits for — <see cref="RunMonitor.WaitsFor"/>),
    /// each resource team (its pending requests: who asks, how many, since when; each member: state, task, for whom, since
    /// when) and each list. <c>progress</c> (the last item movement, the oldest request, the longest task) and <c>notes</c>
    /// (<see cref="Notes"/>) report a model that has not moved for long although events keep the run monitor from calling it
    /// stalled, and each circular wait (<see cref="RunMonitor.CircularWaits"/>: elements that wait for room in each other
    /// round a loop). Read-only: it moves nothing and schedules nothing.
    /// </summary>
    public static class ModelDiagnosis
    {
        /// <param name="all">every element, not only those holding items, stopped or busy</param>
        public static Dictionary<string, object> Of(HeadlessModelFactory model, SimClock clock, bool all = false)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var run = clock.Context.Run;
            var monitor = model.Monitor;
            var elements = new List<object>();
            double now = clock.GetSimulationTime(), lastMovement = RunMonitor.LastMovement(model.GetAllElements().Values);
            int itemsInModel = 0;
            foreach (var e in model.GetAllElements().Values)
            {
                if (e is Resource) continue;
                if (!all && e is Sink) continue;                     // a sink's items have left the model
                int held = RunMonitor.ItemsHeld(e);
                if (!(e is Sink)) itemsInModel += held;
                bool idle = e.CurrentState == ElementState.IDLE;
                if (!all && held == 0 && idle && !e.IsDown) continue;
                var row = new Dictionary<string, object>
                {
                    ["name"] = e.Name,
                    ["type"] = e.GetType().Name,
                    ["state"] = e.CurrentState.Name,
                    ["items"] = held,
                    ["stopped"] = e.IsDown,
                    ["waitsFor"] = held > 0 && monitor != null ? After(monitor.WaitsFor(e), " → ") : null,
                };
                // an unpacker's content counts its carriers and the pieces in them: both apart, against its capacity in
                // carriers ("items 5" with capacity 2)
                if (e is Separator sep && sep.Mode == SeparatorMode.Unpack)
                {
                    row["carriers"] = sep.PlacesUsed;
                    row["pieces"] = Math.Max(0, held - sep.ItemsHeldCount);
                    row["capacity"] = sep.Capacity;
                }
                elements.Add(row);
            }

            var teams = new List<object>();
            double oldestRequest = double.NaN, longestTask = double.NaN;
            string oldestRequester = null, longestMember = null;
            foreach (var d in model.Teams.Values)
            {
                foreach (var r in d.Requests.Entries.Select(x => x.Value as ResourceRequest).Where(r => r != null && r.State == RequestState.Pending))
                    if (!double.IsNaN(r.RequestTime) && (double.IsNaN(oldestRequest) || now - r.RequestTime > oldestRequest))
                    { oldestRequest = now - r.RequestTime; oldestRequester = r.Requester?.Name; }
                foreach (var m in d.Members)
                    if (m.CurrentTask != null && !double.IsNaN(m.CurrentTask.StartTime) && (double.IsNaN(longestTask) || now - m.CurrentTask.StartTime > longestTask))
                    { longestTask = now - m.CurrentTask.StartTime; longestMember = m.Name; }
                var requests = d.Requests.Entries.Select(x => x.Value as ResourceRequest).Where(r => r != null && r.State == RequestState.Pending)
                    .Select(r => new Dictionary<string, object>
                    {
                        ["requester"] = r.Requester?.Name,
                        ["task"] = r.TaskType.ToString(),
                        ["count"] = r.Requirements.Sum(q => q.Count),
                        ["label"] = string.Join(", ", r.Requirements.Where(q => !string.IsNullOrEmpty(q.Label)).Select(q => q.Label)) is string l && l.Length > 0 ? l : null,
                        ["waiting"] = double.IsNaN(r.RequestTime) ? (double?)null : Math.Round(now - r.RequestTime, 6),
                    }).ToList<object>();
                var members = d.Members.Select(m => new Dictionary<string, object>
                {
                    ["name"] = m.Name,
                    ["state"] = m.CurrentState.Name,
                    ["stopped"] = m.IsDown,
                    ["task"] = TaskName(m.CurrentTask),
                    ["for"] = m.CurrentTask?.Requester?.Name,
                    ["step"] = m.CurrentTask == null ? (int?)null : m.CurrentTask.CurrentStep,
                    ["taskSince"] = m.CurrentTask == null || double.IsNaN(m.CurrentTask.StartTime) ? (double?)null : m.CurrentTask.StartTime,
                    ["carried"] = m.Carried.Count,
                }).ToList<object>();
                teams.Add(new Dictionary<string, object>
                {
                    ["team"] = d.Name,
                    ["free"] = d.Members.Count(m => m.IsAvailable),
                    ["pending"] = requests,
                    ["members"] = members,
                });
            }

            var lists = model.Lists.Values.Select(l => new Dictionary<string, object> { ["list"] = l.Name, ["entries"] = l.Count })
                             .ToList<object>();

            return new Dictionary<string, object>
            {
                ["time"] = clock.GetSimulationTime(),
                ["stalled"] = run.Stalled,
                ["stallTime"] = double.IsNaN(run.StallTime) ? (double?)null : run.StallTime,
                ["deadlocked"] = run.Deadlocked,
                ["deadlockTime"] = double.IsNaN(run.DeadlockTime) ? (double?)null : run.DeadlockTime,
                ["endReason"] = run.EndReason.ToString(),
                // the time the end condition "no progress" ended the run (null = it did not)
                ["noProgress"] = !run.NoProgress || double.IsNaN(run.NoProgressTime) ? (double?)null : run.NoProgressTime,
                ["waiting"] = new List<string>(run.Waiting),
                ["progress"] = new Dictionary<string, object>
                {
                    ["lastMovement"] = Nullable(lastMovement),
                    ["noMovementFor"] = double.IsNaN(lastMovement) ? (double?)null : Math.Round(now - lastMovement, 6),
                    ["itemsInModel"] = itemsInModel,
                    ["oldestRequestWait"] = Nullable(oldestRequest),
                    ["oldestRequester"] = oldestRequester,
                    ["longestTask"] = Nullable(longestTask),
                    ["longestTaskMember"] = longestMember,
                },
                ["notes"] = Notes(now, lastMovement, itemsInModel, oldestRequest, oldestRequester, longestTask, longestMember, run.Stalled || run.Deadlocked || run.NoProgress)
                            .Concat(monitor == null ? Enumerable.Empty<string>() : monitor.CircularWaits().Select(CircularWaitNote)).ToList(),
                ["elements"] = elements,
                ["teams"] = teams,
                ["lists"] = lists,
                // what the model warned about while it ran: an unroutable item, an item label read but not set...
                ["warnings"] = new List<string>(clock.Context.ModelWarnings),
            };
        }

        /// <summary>The share of the run without progress that the notes report (a quarter of the time run so far).</summary>
        public const double NoProgressShare = 0.25;

        /// <summary>
        /// Facts about a model that has not moved for a long time while the run monitor sees no stall (events are still
        /// scheduled: shifts, reactions, sources that wait): no item moved, a request or a task that has lasted a quarter of
        /// the run or more. They report, they do not judge — a blocking can be what the design does (too little capacity, a
        /// rule that holds a resource) or a logic error of the model; which one is for the modeller to decide.
        /// </summary>
        public static List<string> Notes(double now, double lastMovement, int itemsInModel, double oldestRequest, string requester,
                                         double longestTask, string member, bool verdict)
        {
            var notes = new List<string>();
            if (now <= 0 || verdict) return notes;   // a stall / deadlock verdict already says it
            double share = NoProgressShare * now;
            if (itemsInModel > 0 && !double.IsNaN(lastMovement) && now - lastMovement >= share)
                notes.Add($"No item has moved since t = {R(lastMovement)} ({R(now - lastMovement)} time units, {Math.Round(100 * (now - lastMovement) / now)} % of the run) " +
                          $"while {itemsInModel} item(s) are in the model; the run monitor reports no stall because events are still " +
                          "scheduled (shifts, reactions, sources). Check whether this blocking is what the design does or comes from the model's logic.");
            if (!double.IsNaN(oldestRequest) && oldestRequest >= share)
                notes.Add($"A request of '{requester}' has waited {R(oldestRequest)} time units for its resources.");
            if (!double.IsNaN(longestTask) && longestTask >= share)
                notes.Add($"'{member}' has been on one task for {R(longestTask)} time units.");
            return notes;
        }

        /// <summary>The note of a circular wait (<see cref="RunMonitor.CircularWaits"/>): given even beside a deadlock
        /// verdict, which names the elements but not the loop they form.</summary>
        public static string CircularWaitNote(List<string> cycle)
            => $"Circular wait: {string.Join(" → ", cycle)} — each holds items and waits for room in the next, which is full: " +
               "none can move. Give the loop more room than the items it can hold, or limit the items let into it.";

        private static double R(double v) => Math.Round(v, 3);
        // a member's task as "Process" or "Transport Move 12": the type, then the name when it says more (a task named by
        // its type read "Process Process")
        internal static string TaskName(SimuLean.Resources.TaskSequence task)
        {
            if (task == null) return null;
            string type = task.Type.ToString(), name = task.Name ?? "";
            return name.Length == 0 || string.Equals(name, type, StringComparison.OrdinalIgnoreCase) ? type : $"{type} {name}";
        }

        private static double? Nullable(double v) => double.IsNaN(v) ? (double?)null : Math.Round(v, 6);

        private static string After(string text, string mark)
        {
            int i = text.IndexOf(mark, StringComparison.Ordinal);
            return i < 0 ? null : text.Substring(i + mark.Length);
        }
    }
}
