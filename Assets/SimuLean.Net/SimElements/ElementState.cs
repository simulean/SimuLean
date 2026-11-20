using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// A typed, interned element-state token. Replaces the former loose
    /// <c>string</c> state (and the string-constant <c>ElementState</c> class):
    /// setting an element's state now goes through <see cref="Element.SetState"/>,
    /// which is the single point that updates the state statistics
    /// (<see cref="ElementStateTracker"/>).
    ///
    /// <para><b>Closed core set, open extension.</b> The engine defines a small
    /// predefined set (<see cref="IDLE"/>, <see cref="RECEIVING"/>,
    /// <see cref="PROCESSING"/>, <see cref="BLOCKED"/>, <see cref="TRAVELING"/>,
    /// <see cref="WAITING_FOR_RESOURCE"/>). Subsystems that need their own states
    /// (e.g. resources: TRAVELING_EMPTY / LOADING / TRAVELING_LOADED
    /// / UNLOADING) obtain a token via <see cref="Of"/>, which <b>interns</b> by
    /// name so equal names always resolve to the same value. This gives a limited,
    /// predefined vocabulary for the common cases while still allowing
    /// subsystem-specific states — chosen over a plain <c>enum</c> for exactly that
    /// reason.</para>
    ///
    /// <para>The token is a <c>readonly struct</c> carrying only the interned
    /// <see cref="Name"/> reference, so it is cheap to copy and compare. The
    /// <see cref="Name"/> is the stable string that surfaces in reports / CSV /
    /// the Unity inspector, so existing outputs are unchanged.</para>
    /// </summary>
    public readonly struct ElementState : IEquatable<ElementState>
    {
        // Intern table: name -> canonical token. Concurrent because parallel
        // headless models create elements / set states on different threads.
        private static readonly ConcurrentDictionary<string, ElementState> Registry
            = new ConcurrentDictionary<string, ElementState>(StringComparer.Ordinal);

        /// <summary>The stable state name (what shows up in reports / CSV / inspector).</summary>
        public string Name { get; }

        // the name's hash, computed once: every state lookup (trackers, utilisation) hashed the name again
        private readonly int hash;

        private ElementState(string name)
        {
            Name = name;
            hash = name.GetHashCode();
        }

        /// <summary>
        /// Returns the interned token for <paramref name="name"/>, creating it on
        /// first use. Equal names always yield equal tokens. Used by subsystems to
        /// register their own states (and by the implicit string conversion).
        /// </summary>
        public static ElementState Of(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("State name must be non-empty.", nameof(name));
            return Registry.GetOrAdd(name, n => new ElementState(n));
        }

        /// <summary>True when a state of this name exists (predefined, or registered by a subsystem or a built model):
        /// a study checks <c>state:NAME</c> metrics with it, where an unknown name would silently read 0.</summary>
        public static bool IsKnown(string name) => !string.IsNullOrEmpty(name) && Registry.ContainsKey(name);

        /// <summary>The names of the states registered so far.</summary>
        public static IReadOnlyCollection<string> KnownNames => (IReadOnlyCollection<string>)Registry.Keys;

        // ── predefined core states (stable names preserved) ──────────────────
        /// <summary>Element has no items and is waiting for input.</summary>
        public static readonly ElementState IDLE                 = Of("IDLE");

        /// <summary>Element is actively processing or holding at least one item.</summary>
        public static readonly ElementState PROCESSING           = Of("PROCESSING");

        /// <summary>Processing is done but output is not available yet.</summary>
        public static readonly ElementState BLOCKED              = Of("BLOCKED");

        /// <summary>Item / carrier is in transit.</summary>
        public static readonly ElementState TRAVELING           = Of("TRAVELING");

        /// <summary>Element is waiting to collect items.</summary>
        public static readonly ElementState RECEIVING            = Of("RECEIVING");

        /// <summary>Element is waiting for a shared operator resource.</summary>
        public static readonly ElementState WAITING_FOR_RESOURCE = Of("WAITING_FOR_RESOURCE");

        /// <summary>Element is changing over (setup) between jobs: a station with a setup rule (<see cref="StationSetup"/>)
        /// while its setup runs, an erection dock's front; a setup crew's members hold in it. Its share of time is the
        /// metric <c>state:SETUP</c> (not part of <c>utilization</c>).</summary>
        public static readonly ElementState SETUP                = Of("SETUP");

        /// <summary>Element cannot go on because material / components are missing: e.g. a paced
        /// line that cannot index because a station has no part, a pick face out of stock. Distinct from IDLE (nothing to
        /// do) and BLOCKED (output refused).</summary>
        public static readonly ElementState STARVED              = Of("STARVED");

        // ── downtime states (set by Element.Stop, see Downtime/) ─────────────
        /// <summary>Generic unplanned stop (default state of a stop request).</summary>
        public static readonly ElementState STOPPED              = Of("STOPPED");

        /// <summary>Failure / breakdown (e.g. MTBF/MTTR generated).</summary>
        public static readonly ElementState BREAKDOWN            = Of("BREAKDOWN");

        /// <summary>Stopped by a downtime with a repair crew, waiting for the crew to arrive (its request queued, then its
        /// travel): the repair itself shows the downtime's state (BREAKDOWN by default).</summary>
        public static readonly ElementState WAITING_FOR_REPAIR   = Of("WAITING_FOR_REPAIR");

        /// <summary>Planned stop: breaks, planned maintenance, start-up.</summary>
        public static readonly ElementState SCHEDULED_DOWN       = Of("SCHEDULED_DOWN");

        /// <summary>Outside working hours (shift calendar).</summary>
        public static readonly ElementState OFF_SHIFT            = Of("OFF_SHIFT");

        /// <summary>True for a default(<see cref="ElementState"/>) with no name.</summary>
        public bool IsNone => Name == null;

        // ── implicit conversion so string call-sites keep working ────────────
        // (One-way: string -> ElementState. Read the name back via .Name / ToString.)
        public static implicit operator ElementState(string name) => Of(name);

        // ── equality / display ───────────────────────────────────────────────
        public bool Equals(ElementState other) => string.Equals(Name, other.Name, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ElementState other && Equals(other);
        public override int GetHashCode() => hash;

        public static bool operator ==(ElementState a, ElementState b) =>  a.Equals(b);
        public static bool operator !=(ElementState a, ElementState b) => !a.Equals(b);

        public override string ToString() => Name ?? string.Empty;
    }

    /// <summary>
    /// A single row in an element's state history log.
    /// Recorded every time the element changes state.
    /// </summary>
    public struct StateLogEntry
    {
        /// <summary>Simulation time at which the state was entered.</summary>
        public double Time;

        /// <summary>The new state.</summary>
        public ElementState State;

        public StateLogEntry(double time, ElementState state)
        {
            Time  = time;
            State = state;
        }

        public override string ToString() => $"[t={Time:F4}] {State.Name}";
    }
}
