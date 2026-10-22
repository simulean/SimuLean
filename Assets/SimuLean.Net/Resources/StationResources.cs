using System;
using System.Collections.Generic;

namespace SimuLean.Resources
{
    /// <summary>When a process task gives its resources back.</summary>
    public enum ProcessRelease
    {
        /// <summary>At the end of the process (default).</summary>
        EndOfProcess,
        /// <summary>When the item leaves the station — kept while the station is BLOCKED (a shared operator stays with a blocked station).</summary>
        ItemLeaves,
        /// <summary>When the item enters another element (<see cref="StationResources.ReleaseAt"/>): the
        /// resource goes with the item — a device lent to a patient for a night at home, returned at the device desk; a
        /// fixture travelling with a part. HELD meanwhile; the station is free as soon as the item leaves it.</summary>
        AtElement,
    }

    /// <summary>An element that asks a dispatcher for resources to do its work (stations).</summary>
    public interface IResourceUser
    {
        /// <summary>Its resource requirements and dispatcher; null = it needs no resources.</summary>
        StationResources Resources { get; set; }
    }

    /// <summary>The resources one job of a station got (or is waiting for).</summary>
    public sealed class ResourceJob
    {
        internal ResourceJob(ResourceRequest request) { Request = request; }
        public ResourceRequest Request { get; }
        public IReadOnlyList<TaskExecutor> Executors => Request.Assigned;
        public bool ProcessEnded { get; internal set; }
        public bool Released { get; internal set; }
        /// <summary>The executors' tasks of this job (a released executor may already work elsewhere).</summary>
        internal readonly List<TaskSequence> Tasks = new List<TaskSequence>();
    }

    /// <summary>
    /// A station's resource needs for its process: the dispatcher, the requirements (default: one resource of the
    /// dispatcher), when they are released and the request priority. The station keeps its process time (its own
    /// stops pause it); its resources are held for the job — PROCESSING while the station processes, HELD while
    /// it keeps them after the process (<see cref="ProcessRelease.ItemLeaves"/>) — and free when released.
    /// Station side: <see cref="Acquire"/> when a job arrives (start processing in <c>onReady</c>),
    /// <see cref="ProcessEnded"/> when the process ends, <see cref="ItemLeft"/> when its item has left.
    /// </summary>
    public sealed class StationResources
    {
        private readonly List<ResourceRequirement> requirements = new List<ResourceRequirement>();

        public StationResources(ResourceDispatcher dispatcher, params ResourceRequirement[] requirements)
        {
            Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            if (requirements != null) this.requirements.AddRange(requirements);
        }

        public ResourceDispatcher Dispatcher { get; }

        // Stations whose Resumed event retries the dispatcher (once each).
        private readonly HashSet<Element> watched = new HashSet<Element>();
        public IReadOnlyList<ResourceRequirement> Requirements => requirements;
        public ProcessRelease Release { get; set; } = ProcessRelease.EndOfProcess;

        /// <summary><see cref="ProcessRelease.AtElement"/>: the element where the resources come back (when the item enters
        /// it). An item that never gets there keeps them.</summary>
        public Element ReleaseAt
        {
            get => releaseAt;
            set
            {
                if (releaseAt == value) return;
                if (releaseAt != null) releaseAt.ItemEntered -= OnItemAtReleasePoint;
                releaseAt = value;
                if (releaseAt != null) releaseAt.ItemEntered += OnItemAtReleasePoint;
            }
        }
        private Element releaseAt;
        // AtElement: the jobs whose resources travel with each item
        private readonly Dictionary<Item, List<ResourceJob>> travelling = new Dictionary<Item, List<ResourceJob>>();

        private void OnItemAtReleasePoint(Element at, Item item)
        {
            if (item == null || !travelling.TryGetValue(item, out var jobs)) return;
            travelling.Remove(item);
            foreach (var job in jobs)
            {
                if (job.Released) continue;
                job.Released = true;
                Next(job);
            }
        }

        /// <summary>
        /// The resources' own time at the station (null = the whole process, the default): with a sampler they work for
        /// its value (sampled per job, the item's labels readable) and are then free, while the item stays for the rest
        /// of the station's process — painting then curing, an erection lift then the joint welding (naval §6.2).
        /// <see cref="Release"/> does not apply then.
        /// </summary>
        public IRandomSampler HoldFor { get; set; }

        /// <summary>Priority of the station's requests (null = 0): usually an expression over the item and the station
        /// (<see cref="SimuLean.Serialization.ResourceBuild.PriorityOf"/>).</summary>
        public Func<Item, double> Priority { get; set; }

        /// <summary>Asks for the job's resources; <paramref name="onReady"/> runs when they are assigned (maybe at once).</summary>
        public ResourceJob Acquire(Element station, Item item, Action onReady)
        {
            if (onReady == null) throw new ArgumentNullException(nameof(onReady));
            ResourceJob job = null;
            var request = new ResourceRequest(station, TaskType.Process, (r, got) =>
            {
                // Spatial resources first walk / travel to the station (if it has a location); the process starts
                // when every one of them has arrived.
                var at = new string[got.Count];
                for (int k = 0; k < got.Count; k++) at[k] = WorkPointFor(station, got[k], job);
                // One count per walker plus one for this loop, released after it: a walk of zero length ends inside its
                // own Execute, and the process must start once, after every resource has its task.
                int walking = 1;
                for (int k = 0; k < got.Count; k++) if (Walks(got[k], at[k])) walking++;
                for (int k = 0; k < got.Count; k++)
                {
                    var ex = got[k];
                    var task = new TaskSequence(TaskType.Process, station);
                    if (Walks(ex, at[k]))
                        task.Travel(at[k]).Callback(() => { if (--walking == 0) onReady(); });
                    if (HoldFor != null)
                        task.Process(() => SimuLean.Expressions.Samplers.Sample(HoldFor, item, station));   // their own time
                    else
                    {
                        task.HoldUntil(() => job.ProcessEnded, ElementState.PROCESSING);
                        if (Release == ProcessRelease.ItemLeaves || Release == ProcessRelease.AtElement)
                            task.HoldUntil(() => job.Released, ResourceStates.HELD);
                    }
                    job.Tasks.Add(task);
                    ex.Execute(task);
                }
                if (--walking == 0) onReady();
            }, requirements.ToArray());
            request.Priority = Priority?.Invoke(item) ?? 0;
            request.Location = station.Location;   // "nearest operator" rules
            job = new ResourceJob(request);
            if (Release == ProcessRelease.AtElement && HoldFor == null && item != null)
            {
                if (!travelling.TryGetValue(item, out var jobs)) travelling[item] = jobs = new List<ResourceJob>();
                jobs.Add(job);
            }
            // a stopped station's request waits: the dispatcher tries again when the station resumes
            if (station != null && watched.Add(station)) station.Resumed += (e, token) => Dispatcher.Retry();
            Dispatcher.Request(request);
            return job;
        }

        private static bool Walks(TaskExecutor ex, string at)
            => ex.Mode == ResourceMode.Spatial && at != null && ex.Location != at;

        // Work points taken (S4): point → the resource working there and the job it works for, until that job releases
        // it (keyed by job: with HoldFor a resource may already work there for a later job when an earlier one is
        // released, and the point must stay taken).
        private readonly Dictionary<string, (TaskExecutor who, ResourceJob job)> occupied = new Dictionary<string, (TaskExecutor, ResourceJob)>();

        // Where a resource of this station stands: a free work point (in order; all taken → shared, in turn), else the
        // station's location.
        private string WorkPointFor(Element station, TaskExecutor ex, ResourceJob job)
        {
            var points = station.WorkLocations;
            if (points.Count == 0 || ex.Mode != ResourceMode.Spatial) return station.Location;
            foreach (string p in points)
                if (!occupied.TryGetValue(p, out var taken) || taken.who == ex) { occupied[p] = (ex, job); return p; }
            string shared = points[occupied.Count % points.Count];
            return shared;
        }

        /// <summary>The station's process ended: resources released now (<see cref="ProcessRelease.EndOfProcess"/>)
        /// or kept, HELD, until <see cref="ItemLeft"/>.</summary>
        public void ProcessEnded(ResourceJob job)
        {
            if (job == null || job.ProcessEnded || job.Request.State != RequestState.Assigned) return;
            job.ProcessEnded = true;
            if (Release == ProcessRelease.EndOfProcess) job.Released = true;
            Next(job);
        }

        /// <summary>The job's item left the station: resources kept until now are released.</summary>
        public void ItemLeft(ResourceJob job)
        {
            if (job == null || !job.ProcessEnded || job.Released || Release == ProcessRelease.AtElement) return;
            job.Released = true;
            Next(job);
        }

        /// <summary>Withdraws a job still waiting for resources (e.g. the station was reset).</summary>
        public void Cancel(ResourceJob job)
        {
            if (job != null && job.Request.State == RequestState.Pending) Dispatcher.Cancel(job.Request);
        }

        private void Next(ResourceJob job)
        {
            if (job.Released && occupied.Count > 0)   // its work points are free again (not one taken by a later job)
            {
                List<string> mine = null;
                foreach (var kv in occupied) if (kv.Value.job == job) (mine ?? (mine = new List<string>())).Add(kv.Key);
                if (mine != null) foreach (string p in mine) occupied.Remove(p);
            }
            // only an executor still on this job's task (with HoldFor it may have finished and taken another job)
            foreach (var task in job.Tasks)
                if (task.Executor != null && task.Executor.CurrentTask == task) task.Executor.Continue();
        }
    }
}
