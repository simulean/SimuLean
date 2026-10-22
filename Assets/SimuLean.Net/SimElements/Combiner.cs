using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Combiner: a main item (the job) and the required number of components from each component input; when all are
    /// there they are assembled for the sampled delay. <see cref="Capacity"/> assemblies run in parallel,
    /// each an <see cref="Assembly"/> with its own main item, components, requirements and matching (a copy of
    /// the pull mode, updated by its own main item): a component goes to the first assembly (in slot order) that is
    /// gathering, has room on that input and admits it. With one assembly it is the single-job combiner it always was —
    /// an assembly waiting for its resources already gathers the components of its next job.
    /// </summary>
    public class Combiner : Element, IWorkStation, SimuLean.Resources.IResourceUser
    {
        /// <summary>One assembly of the combiner: its job slot, main item, components per input and matching.</summary>
        public sealed class Assembly
        {
            public int Index { get; internal set; }
            /// <summary>The job slot (state, item, timing; per-slot statistics).</summary>
            public ServerProcess Process { get; internal set; }
            /// <summary>What this assembly admits (the pull mode, updated by its own main item).</summary>
            public InputStrategy Match { get; internal set; }
            internal int[] Requirements;
            internal List<Item>[] Parts;
            internal readonly List<Item> Cycle = new List<Item>();

            public ElementState State => Process.GetState();
            /// <summary>The main item (with the components attached once assembled), or null when idle.</summary>
            public Item Main => Process.GetState() != ElementState.IDLE ? Process.GetItem() : null;
            /// <summary>The components gathered on input <paramref name="input"/> (not attached yet).</summary>
            public IReadOnlyList<Item> PartsOf(int input) => Parts[input];
            /// <summary>Gathering: the main item is here (also while waiting for resources: components of its next job).</summary>
            public bool Gathering => State == ElementState.RECEIVING || State == ElementState.WAITING_FOR_RESOURCE;
        }

        private readonly int[] requirements;      // the template of every assembly
        private readonly string name;
        private readonly IRandomSampler delay;
        private readonly CombinerInput[] inputs;
        private readonly bool batchMode;          // true = the components travel on as sub-items; false = consumed
        private readonly InputStrategy pullMode;
        private readonly bool updateRequirementsEnabled;
        private readonly List<string> updateLabels;
        private Assembly[] assemblies;
        private readonly Queue<Assembly> blocked = new Queue<Assembly>();   // finished, waiting to leave (in that order)
        private double totalServiceTime;
        private int completedJobs;

        /// <param name="requirements">Components per input.</param>
        /// <param name="delayStrategy">Assembly time.</param>
        /// <param name="batchMode">True = the components travel on as sub-items of the main item; false = consumed.</param>
        /// <param name="pullMode">What the component inputs admit (default: every item); each assembly has its own copy.</param>
        /// <param name="updateRequirements">Requirements read from the main item's labels (<paramref name="updateLabels"/>).</param>
        /// <param name="capacity">Assemblies in parallel (≥ 1).</param>
        public Combiner(
            int[] requirements,
            IRandomSampler delayStrategy,
            string name,
            SimClock simClock,
            bool batchMode = false,
            InputStrategy pullMode = null,
            bool updateRequirements = false,
            List<string> updateLabels = null,
            IVElement vElement = null,
            int capacity = 1)
            : base(name, simClock, vElement)
        {
            this.requirements = (int[])requirements.Clone();
            this.name = name;
            this.delay = delayStrategy;
            this.batchMode = batchMode;
            this.pullMode = pullMode ?? new DefaultStrategy();
            this.updateRequirementsEnabled = updateRequirements;
            this.updateLabels = updateLabels;
            Capacity = Math.Max(1, capacity);
            inputs = new CombinerInput[requirements.Length];
            for (int i = 0; i < requirements.Length; i++)
                inputs[i] = new CombinerInput(this, i, $"{name}.Input{i}", simClock, vElement);
        }

        /// <summary>Assemblies in parallel.</summary>
        public int Capacity { get; }

        /// <summary>Resources each assembly needs (dispatcher, requirements, release); null = none.</summary>
        public SimuLean.Resources.StationResources Resources { get; set; }

        /// <summary>The assemblies (views: one slot each).</summary>
        public IReadOnlyList<Assembly> Assemblies => assemblies;

        /// <summary>The first assembly's job slot (single-assembly views); null before <see cref="Start"/>.</summary>
        public ServerProcess ActiveProcess => assemblies?[0].Process;

        public override void Start()
        {
            base.Start();
            assemblies = new Assembly[Capacity];
            for (int k = 0; k < Capacity; k++)
            {
                var a = new Assembly
                {
                    Index = k, Process = new ServerProcess(this, delay, simClock, k), Match = pullMode.Copy(),
                    Requirements = (int[])requirements.Clone(), Parts = new List<Item>[inputs.Length],
                };
                for (int i = 0; i < inputs.Length; i++) a.Parts[i] = new List<Item>();
                a.Process.SetState(ElementState.IDLE);
                assemblies[k] = a;
            }
            blocked.Clear();
            totalServiceTime = 0;
            completedJobs = 0;
            foreach (var input in inputs) input.Start();
        }

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            totalServiceTime = 0;
            completedJobs = 0;
            if (assemblies != null) foreach (var a in assemblies) a.Process.ResetStatistics();
        }

        /// <summary>Time share processing: one assembly = the element's; several = the mean of the assemblies (as a Multi-Server).</summary>
        public double GetUtilization()
        {
            if (assemblies == null || Capacity == 1) return GetStateRatio(ElementState.PROCESSING);
            double sum = 0;
            foreach (var a in assemblies) sum += a.Process.GetStateRatio(ElementState.PROCESSING);
            return sum / assemblies.Length;
        }

        /// <summary>Mean sampled assembly time of the completed jobs.</summary>
        public double GetMeanServiceTime() => completedJobs > 0 ? totalServiceTime / completedJobs : 0.0;

        /// <summary>The main items (with their sub-items) — for views to clear.</summary>
        public IEnumerable<Item> GetItems()
        {
            if (assemblies == null) yield break;
            foreach (var a in assemblies) if (a.Main != null) yield return a.Main;
        }

        // ── ports ────────────────────────────────────────────────────────────

        public CombinerInput GetComponentInput(int i)
        {
            if (i < 0 || i >= inputs.Length) throw new ArgumentOutOfRangeException(nameof(i), "Index out of range");
            return inputs[i];
        }

        public int GetInputsCount() => inputs.Length;

        // Origins connected so far, in connection order (index 0 = main input).
        private readonly List<Element> connectedOrigins = new List<Element>();

        /// <summary>The element connected to the main input (null = none yet, or the main items come from a list).</summary>
        public Element MainOrigin => connectedOrigins.Count > 0 ? connectedOrigins[0] : null;

        /// <summary>The element wired to connection <paramref name="connectionIndex"/> (0 = main, k = component input k − 1);
        /// null = none (the input pulls from a list, or is not wired).</summary>
        public Element InputOrigin(int connectionIndex)
            => connectionIndex >= 0 && connectionIndex < connectedOrigins.Count ? connectedOrigins[connectionIndex] : null;

        /// <summary>
        /// Optional explicit connection order: element i of the list feeds connection i (0 = main input, k ≥ 1 =
        /// component input k − 1). Evaluated lazily the first time a link is wired (Unity: <c>UnityCombiner.inputOrder</c>;
        /// headless: the <c>inputOrder</c> config parameter). Origins not in the list fall back to connection order.
        /// </summary>
        public Func<IList<Element>> InputOrderProvider { get; set; }

        /// <summary>The element receiving connection <paramref name="connectionIndex"/>: 0 = this combiner (main item),
        /// k ≥ 1 = component input k − 1.</summary>
        public Element GetInputPort(int connectionIndex)
        {
            if (connectionIndex == 0) return this;
            if (connectionIndex < 1 || connectionIndex > inputs.Length)
                throw new ArgumentOutOfRangeException(nameof(connectionIndex),
                    $"Combiner '{name}' has {inputs.Length + 1} input connections (main + {inputs.Length} component inputs); " +
                    $"connection {connectionIndex} does not exist.");
            return inputs[connectionIndex - 1];
        }

        /// <summary>
        /// Connect every upstream element the normal way: the first connection (or position 0 of
        /// <see cref="InputOrderProvider"/>) becomes the main input, the next ones component inputs 0, 1, … in order. The
        /// same origin always maps to the same port.
        /// </summary>
        public override Element ResolveInputFor(Element origin)
        {
            int index = -1;
            IList<Element> order = InputOrderProvider?.Invoke();
            if (order != null) index = order.IndexOf(origin);

            if (index < 0) index = connectedOrigins.IndexOf(origin);
            if (index < 0)
            {
                index = 0;
                while ((index < connectedOrigins.Count && connectedOrigins[index] != null)
                       || (order != null && index < order.Count && order[index] != null && order[index] != origin))
                    index++;
            }

            while (connectedOrigins.Count <= index) connectedOrigins.Add(null);
            connectedOrigins[index] = origin;
            return GetInputPort(index);
        }

        // ── component inputs ─────────────────────────────────────────────────

        /// <summary>The assembly that takes <paramref name="item"/> on input <paramref name="input"/>: the first gathering
        /// one with room there whose matching admits it (<paramref name="matching"/> false = room only); null if none.</summary>
        internal Assembly TargetFor(int input, Item item, Element origin, bool matching)
        {
            if (assemblies == null) return null;
            foreach (var a in assemblies)
                if (a.Gathering && a.Parts[input].Count < a.Requirements[input]
                    && (!matching || a.Match.Admits(inputs[input], item, origin)))
                    return a;
            return null;
        }

        /// <summary>Components waiting on input <paramref name="input"/> (every assembly).</summary>
        internal int Waiting(int input)
        {
            int n = 0;
            if (assemblies != null) foreach (var a in assemblies) n += a.Parts[input].Count;
            return n;
        }

        /// <summary>Room on input <paramref name="input"/>: the gathering assemblies' (or one assembly's requirement when none gathers).</summary>
        internal int Room(int input)
        {
            if (assemblies == null) return requirements[input];
            int room = 0; bool any = false;
            foreach (var a in assemblies)
                if (a.Gathering) { any = true; room += Math.Max(0, a.Requirements[input] - a.Parts[input].Count); }
            if (any) return room;
            int free = assemblies[0].Requirements[input] - assemblies[0].Parts[input].Count;   // one assembly: its own room
            return Capacity == 1 ? free : assemblies[0].Requirements[input];
        }

        /// <summary>A component arrived on <paramref name="input"/> for <paramref name="a"/>: true when it completed the set.</summary>
        internal bool ComponentReceived(Assembly a, int input, Item item)
        {
            a.Parts[input].Add(item);
            vElement.LoadItem(item);
            return TryAssemble(a);
        }

        /// <summary>Every assembly checks its set (an input asked again).</summary>
        internal void CheckAssemblies()
        {
            if (assemblies == null) return;
            foreach (var a in assemblies) TryAssemble(a);
        }

        // ── the cycle ────────────────────────────────────────────────────────

        public override bool CheckAvailability(Item theItem)
        {
            if (assemblies == null) return false;
            foreach (var a in assemblies) if (a.State == ElementState.IDLE) return true;
            return false;
        }

        public override int GetFreeCapacity()
        {
            if (assemblies == null) return 0;
            int n = 0;
            foreach (var a in assemblies) if (a.State == ElementState.IDLE) n++;
            return n;
        }

        /// <summary>The components waiting in the inputs.</summary>
        public override int GetQueueLength()
        {
            int n = 0;
            for (int i = 0; i < inputs.Length; i++) n += Waiting(i);
            return n;
        }

        public override bool Receive(Item theItem)
        {
            Assembly a = null;
            foreach (var x in assemblies) if (x.State == ElementState.IDLE) { a = x; break; }
            if (a == null) return false;
            a.Process.SetItem(theItem);
            SetSlot(a, ElementState.RECEIVING);
            // The main item sets what its assembly admits and needs; the inputs offer what they have and ask for more.
            a.Match.UpdateStrategy(theItem);
            UpdateRequirements(a, theItem);
            for (int i = 0; i < inputs.Length; i++) inputs[i].Unblock();
            return true;
        }

        private void UpdateRequirements(Assembly a, Item theItem)
        {
            if (!updateRequirementsEnabled || updateLabels == null) return;
            for (int i = 0; i < updateLabels.Count && i < inputs.Length; i++)
            {
                var labelValue = theItem.GetLabelValue(updateLabels[i]);
                if (labelValue != null) a.Requirements[i] = Convert.ToInt32(labelValue);
            }
        }

        // All components there (and the main item gathering): attach them, then acquire the resources and process.
        private bool TryAssemble(Assembly a)
        {
            if (a.State != ElementState.RECEIVING) return false;
            for (int i = 0; i < inputs.Length; i++)
                if (a.Parts[i].Count < a.Requirements[i]) return false;

            // Attached in both modes so the views can show / dispose them when processing starts; without batchMode
            // they are detached again there (consumed).
            Item main = a.Process.GetItem();
            a.Cycle.Clear();
            for (int i = 0; i < inputs.Length; i++)
            {
                int take = a.Requirements[i];
                for (int k = 0; k < take && a.Parts[i].Count > 0; k++)
                {
                    Item c = a.Parts[i][0];
                    a.Parts[i].RemoveAt(0);
                    main.AddItem(c);
                    a.Cycle.Add(c);
                }
            }
            StartJob(a);
            return true;
        }

        private void StartJob(Assembly a)
        {
            if (Resources != null)
            {
                SetSlot(a, ElementState.WAITING_FOR_RESOURCE);
                var p = a.Process;
                p.job = Resources.Acquire(this, p.GetItem(), () => ((IWorkStation)this).StartProcessing(p));
            }
            else ((IWorkStation)this).StartProcessing(a.Process);
        }

        void IWorkStation.StartProcessing(ServerProcess p)
        {
            var a = assemblies[p.Index];
            SetSlot(a, ElementState.PROCESSING);
            double time = SimuLean.Expressions.Samplers.Sample(delay, p.GetItem(), this);
            p.loadTime = simClock.GetSimulationTime();
            p.lastDelay = time;
            vElement.UnloadItem(p.GetItem());
            if (!batchMode)
            {
                // Not batch mode: the components are consumed — only the main item (with what it carried in) continues.
                List<Item> subs = p.GetItem().GetSubItems();
                if (subs != null) foreach (Item c in a.Cycle) subs.Remove(c);
            }
            a.Cycle.Clear();
            ScheduleWork(p, time);
        }

        void IWorkStation.CompleteServerProcess(ServerProcess p)
        {
            var a = assemblies[p.Index];
            totalServiceTime += p.lastDelay;
            completedJobs++;
            RaiseProcessEnded(p.GetItem());
            Resources?.ProcessEnded(p.job);   // released now, or kept (HELD) until the job leaves
            if (blocked.Count == 0 && SendOut(p.GetItem())) JobLeft(a);
            else
            {
                // Output blocked (or others wait before it): the resources stay held until Unblock() sends the job.
                SetSlot(a, ElementState.BLOCKED);
                blocked.Enqueue(a);
            }
        }

        public override bool Unblock()
        {
            if (blocked.Count == 0) return false;
            var a = blocked.Peek();
            if (!SendOut(a.Process.GetItem())) return false;
            blocked.Dequeue();
            JobLeft(a);
            return true;
        }

        private void JobLeft(Assembly a)
        {
            SetSlot(a, ElementState.IDLE);
            Resources?.ItemLeft(a.Process.job);
            a.Process.job = null;
            vElement.ReportState("Exit");
            NotifyInput();
        }

        // The slot's state, and the element's: one assembly = its state; several = the busiest one (as a Multi-Server).
        private void SetSlot(Assembly a, ElementState state)
        {
            a.Process.SetState(state);
            if (Capacity == 1) { SetState(state); return; }
            bool processing = false, waiting = false, isBlocked = false, receiving = false;
            foreach (var x in assemblies)
            {
                var s = x.State;
                if (s == ElementState.PROCESSING) processing = true;
                else if (s == ElementState.WAITING_FOR_RESOURCE) waiting = true;
                else if (s == ElementState.BLOCKED) isBlocked = true;
                else if (s == ElementState.RECEIVING) receiving = true;
            }
            SetState(processing ? ElementState.PROCESSING : waiting ? ElementState.WAITING_FOR_RESOURCE
                   : isBlocked ? ElementState.BLOCKED : receiving ? ElementState.RECEIVING : ElementState.IDLE);
        }

        /// <summary>The assemblies' slots account the combiner's stop state while it is effective (utilisation without
        /// stopped time).</summary>
        protected override void OnStopShown(ElementState? stop)
        {
            if (assemblies != null) foreach (var a in assemblies) a.Process.ShowStop(stop);
        }

        /// <summary>Progress views: shift the running jobs' load times by the part of the pause they spent loaded.</summary>
        protected override void OnResumed(StopToken token)
        {
            if (!ResumedWork(token) || assemblies == null) return;
            foreach (var a in assemblies)
                if (a.State == ElementState.PROCESSING) a.Process.loadTime += PauseShiftFor(a.Process.loadTime);
        }

        string IWorkStation.GetName() => GetName();
    }
}
