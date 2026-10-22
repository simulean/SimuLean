using System.Collections.Generic;
using SimuLean.Stochastics.Streams;
using SimuLean.Collections;

namespace SimuLean
{
    /// <summary>
    /// Per-model simulation context owned by a <see cref="SimClock"/>.
    /// Replaces the former process-wide static element registry so that many models
    /// (e.g. parallel headless GA evaluations) can coexist without sharing state,
    /// and provides the model's random streams: one per stochastic source, each derived from the
    /// model seed and the source's stable key (owner element name + purpose), see
    /// <see cref="CreateRandomStream(string, string)"/>.
    /// </summary>
    public class SimContext
    {
        private readonly List<Element> elements = new List<Element>();
        private readonly List<SimList> lists = new List<SimList>();
        private readonly object sync = new object();
        private readonly IRandomEngine rootEngine;
        // Stream keys handed out since the last (re)seed → occurrence count (duplicate keys get "#n").
        private readonly Dictionary<string, int> streamKeys = new Dictionary<string, int>();

        /// <summary>The clock this context belongs to.</summary>
        public SimClock Clock { get; }

        /// <summary>
        /// Per-model parameters accessible to output-routing strategies (formerly the
        /// process-wide static <c>GeneralLink.GlobalModelParameters</c>). Kept per-context
        /// so parallel headless models never share routing state. Populate before a run;
        /// <see cref="GeneralLink"/> passes it into every <c>OutputStrategyContext</c>.
        /// Values are typed <see cref="ParameterValue"/>s (int / double / bool / string)
        /// rather than boxed <c>object</c>, so numeric/boolean parameters never allocate.
        /// </summary>
        public Dictionary<string, ParameterValue> ModelParameters { get; } = new Dictionary<string, ParameterValue>();

        /// <summary>
        /// Calendar that maps simulation time to dates (start date + seconds per unit).
        /// Defaults to <see cref="SimCalendar.Default"/>; set it before building
        /// date-driven data (order books, downtime traces, shifts).
        /// </summary>
        public SimCalendar Calendar { get; set; } = SimCalendar.Default;

        /// <summary>The model's holidays (dates): the working calendar shared by every shift — a shift
        /// pattern skips them as it skips its own. Set before building the shifts.</summary>
        public System.Collections.Generic.ISet<System.DateTime> Holidays { get; } = new System.Collections.Generic.HashSet<System.DateTime>();

        /// <summary>True when <paramref name="date"/>'s day is one of the model's <see cref="Holidays"/>.</summary>
        public bool IsHoliday(System.DateTime date) => Holidays.Contains(date.Date);

        /// <summary>The model's named data tables: the snapshot taken at the run's start, read-only.</summary>
        public SimuLean.Data.ModelData Data { get; internal set; } = new SimuLean.Data.ModelData();

        /// <summary>How the run is going / ended: end condition, end reason, deadlock (set by the run monitor).</summary>
        public RunStatus Run { get; } = new RunStatus();

        private SimuLean.Signals.SignalHub signals;
        /// <summary>The model's signal table: subscriptions made when the model is built; created on first use,
        /// so a model without reactions has none.</summary>
        public SimuLean.Signals.SignalHub Signals => signals ?? (signals = new SimuLean.Signals.SignalHub(Clock));
        /// <summary>The signal table if the model has one, else null (for views: reading it creates nothing).</summary>
        public SimuLean.Signals.SignalHub SignalsIfAny => signals;

        /// <summary>
        /// Model seed of this run. Every random stream derives from it (and from its owner's key).
        /// A clock built without a seed draws a fresh one (<see cref="NewRandomSeed"/>); read it here
        /// to reproduce the run (<c>new SimClock(seed)</c> or <see cref="Reseed"/>).
        /// </summary>
        public int Seed { get; private set; }

        internal SimContext(SimClock clock, int? seed)
        {
            Clock = clock;
            Seed  = seed ?? NewRandomSeed();
            rootEngine = new DefaultRandomEngine(Seed);
        }

        /// <summary>
        /// Sets a new model seed for the next run of a reused clock (the Unity scene clock reseeds
        /// before every Run / Restart). Resets the root engine and the stream-key bookkeeping, so
        /// samplers created afterwards get exactly the streams a fresh <c>new SimClock(seed)</c>
        /// would give them. Streams handed out before keep their state: reseed before building.
        /// </summary>
        public void Reseed(int seed)
        {
            lock (sync)
            {
                Seed = seed;
                rootEngine.SetSeed(seed);
                streamKeys.Clear();
            }
        }

        /// <summary>Registers an element with this model (called by the Element constructor).</summary>
        internal void Register(Element element)
        {
            lock (sync) { elements.Add(element); byName = null; }
        }

        private Dictionary<string, Element> byName;

        /// <summary>The model's element called <paramref name="name"/> — the first one created of that name (a station
        /// before its own parts) — or null (expressions that read other elements).</summary>
        public Element FindElement(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (sync)
            {
                if (byName == null)
                {
                    byName = new Dictionary<string, Element>();
                    foreach (var e in elements)
                    {
                        string n = e.GetName();
                        if (n != null && !byName.ContainsKey(n)) byName[n] = e;
                    }
                }
                return byName.TryGetValue(name, out var found) ? found : null;
            }
        }

        /// <summary>Snapshot of all elements created for this model.</summary>
        public IReadOnlyList<Element> Elements
        {
            get { lock (sync) return elements.ToArray(); }
        }

        internal void RegisterList(SimList list)
        {
            lock (sync) lists.Add(list);
        }

        /// <summary>The model's lists, in creation order.</summary>
        public IReadOnlyList<SimList> Lists
        {
            get { lock (sync) return lists.ToArray(); }
        }

        /// <summary>The list called <paramref name="name"/>, or null.</summary>
        public SimList GetList(string name)
        {
            lock (sync) return lists.Find(l => l.Name == name);
        }

        /// <summary>
        /// The context's root random engine (thread-safe, seeded with <see cref="Seed"/>). Shared by
        /// everything that uses it, so draws depend on call order — model elements should use their
        /// own stream (<see cref="CreateRandomStream(string, string)"/>) instead.
        /// </summary>
        public IRandomEngine RandomEngine => rootEngine;

        /// <summary>
        /// Creates the random stream of one stochastic source: seeded with
        /// <see cref="DeriveStreamSeed"/>(<see cref="Seed"/>, key), key = <c>owner/purpose</c>
        /// (e.g. <c>"Lathe/delay"</c>, <c>"Src/interArrival"</c>). The key, not the creation order,
        /// decides the numbers, so adding or removing other elements does not change what this source
        /// draws (common random numbers), and the Unity scene and a headless rebuild of the same model
        /// draw the same numbers per source. A key requested again since the last (re)seed — several
        /// slots of one element, elements with the same name — gets <c>key#1</c>, <c>key#2</c>… in
        /// request order.
        /// </summary>
        /// <param name="owner">Stable identity of the owner, normally the element name.</param>
        /// <param name="purpose">What the stream samples (<c>delay</c>, <c>interArrival</c>, <c>ttf</c>…).</param>
        public IRandomEngine CreateRandomStream(string owner, string purpose = null)
        {
            string key = StreamKey(owner, purpose);
            lock (sync)
            {
                if (streamKeys.TryGetValue(key, out int n)) { streamKeys[key] = n + 1; key = key + "#" + n; }
                else streamKeys[key] = 1;
                return new DefaultRandomEngine(DeriveStreamSeed(Seed, key));
            }
        }

        /// <summary>
        /// Gives <paramref name="sampler"/> its own stream (<see cref="CreateRandomStream(string, string)"/>)
        /// when it draws random numbers (<see cref="IRandomStreamUser"/>); deterministic samplers
        /// (constants, label expressions) are returned untouched and consume no key.
        /// </summary>
        public T AssignRandomStream<T>(T sampler, string owner, string purpose = null) where T : class
        {
            if (sampler is IRandomStreamUser user)
                user.SetRandomEngine(CreateRandomStream(owner, purpose));
            return sampler;
        }

        /// <summary>Stream key of <paramref name="owner"/> / <paramref name="purpose"/> (<c>owner/purpose</c>).</summary>
        public static string StreamKey(string owner, string purpose)
            => (owner ?? "") + "/" + (string.IsNullOrEmpty(purpose) ? "sampler" : purpose);

        /// <summary>
        /// Seed of the stream <paramref name="key"/> under <paramref name="modelSeed"/>: a stable
        /// 64-bit FNV-1a hash of the key's UTF-16 code units (NOT <c>string.GetHashCode</c>, which is
        /// randomised per process) mixed with the model seed by the SplitMix64 finaliser, folded to a
        /// non-negative int (System.Random treats ±seed alike). Same inputs → same seed on every
        /// runtime (Unity Mono / IL2CPP, .NET) and thread.
        /// </summary>
        public static int DeriveStreamSeed(int modelSeed, string key)
        {
            const ulong FnvOffset = 14695981039346656037UL, FnvPrime = 1099511628211UL;
            ulong h = FnvOffset;
            foreach (char c in key ?? "")
            {
                h ^= (byte)c;        h *= FnvPrime;
                h ^= (byte)(c >> 8); h *= FnvPrime;
            }
            ulong z = h ^ ((ulong)(uint)modelSeed * 0x9E3779B97F4A7C15UL);
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)((z ^ (z >> 32)) & 0x7FFFFFFFUL);
        }

        /// <summary>
        /// A fresh nondeterministic model seed in 1..int.MaxValue (thread-safe; Guid-based, so models
        /// created in the same millisecond or on parallel threads get different seeds).
        /// </summary>
        public static int NewRandomSeed()
        {
            byte[] b = System.Guid.NewGuid().ToByteArray();
            int v = (System.BitConverter.ToInt32(b, 0) ^ System.BitConverter.ToInt32(b, 8)) & 0x7FFFFFFF;
            return v == 0 ? 1 : v;
        }

        /// <summary>
        /// Total number of items currently held across all elements of this model.
        /// (Renamed from <c>GetInventory</c>: this is a raw item count, not necessarily
        /// "inventory" in a warehousing sense.)
        /// </summary>
        public int GetItemCount()
        {
            lock (sync)
            {
                int count = 0;
                foreach (Element e in elements)
                    count += e.GetQueueLength();
                return count;
            }
        }
    }
}
