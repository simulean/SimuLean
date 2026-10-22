using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Tracks time spent in each <see cref="ElementState"/> for a single
    /// <see cref="Element"/>. Also keeps a chronological log of every state
    /// transition so a statistics collector can replay or aggregate the data later.
    ///
    /// This is the "stats class" the state flows into: <see cref="Element.SetState"/>
    /// calls <see cref="Transition"/>, which is the single place where per-state
    /// time is accumulated.
    /// </summary>
    public class ElementStateTracker
    {
        // ── current state ────────────────────────────────────────────────────
        private ElementState _currentState;
        private double       _stateEnteredAt;
        private double       _resetAt;          // start of the observation period (creation / last reset)

        // ── time accumulators ────────────────────────────────────────────────
        // key = state,  value = total time accumulated so far
        private readonly Dictionary<ElementState, double> _accumulated
            = new Dictionary<ElementState, double>();

        // ── chronological state log ──────────────────────────────────────────
        private readonly List<StateLogEntry> _log = new List<StateLogEntry>();
        /// <summary>Keep the chronological log (default for elements). Off: only the totals — a headless study run that
        /// records no timeline keeps no log (its memory grew with every state change; review2 #19).</summary>
        public bool KeepLog { get; set; }

        // ── public read-only view ────────────────────────────────────────────
        /// <summary>The state the element is currently in.</summary>
        public ElementState CurrentState => _currentState;

        /// <summary>Changes since creation (a transition or a reset): readers may cache what they computed from it.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// Read-only chronological list of every state transition.
        /// Each entry records the sim-time and the new state.
        /// </summary>
        public IReadOnlyList<StateLogEntry> Log => _log;

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Initialises the tracker and records the first (starting) state.
        /// </summary>
        /// <param name="initialState">State to enter at time 0 (default: IDLE).</param>
        /// <param name="simTime">Current simulation time (usually 0.0 at start).</param>
        /// <param name="keepLog">Keep the chronological log (elements: timelines, inspector splits). A tracker nobody reads
        /// the log of (a Multi-Server slot) keeps only the totals: its log grew without limit.</param>
        public ElementStateTracker(ElementState initialState = default, double simTime = 0.0, bool keepLog = true)
        {
            KeepLog = keepLog;
            if (initialState.IsNone) initialState = ElementState.IDLE;
            _currentState   = initialState;
            _stateEnteredAt = simTime;
            _resetAt        = simTime;
            _log.Add(new StateLogEntry(simTime, initialState));
            EnsureKey(initialState);
        }

        /// <summary>Start of the observation period: creation or the last <see cref="Reset"/>.</summary>
        public double ResetAt => _resetAt;

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Transitions to <paramref name="newState"/>.
        /// Accumulates the time spent in the previous state and appends a log entry.
        /// If the new state is identical to the current one nothing is recorded.
        /// </summary>
        public void Transition(ElementState newState, double simTime)
        {
            if (newState == _currentState)
                return;

            // Accumulate elapsed time in the outgoing state
            double elapsed = simTime - _stateEnteredAt;
            if (elapsed < 0) elapsed = 0;          // guard against clock oddities
            Accumulate(_currentState, elapsed);

            // Enter new state
            _currentState   = newState;
            _stateEnteredAt = simTime;
            Version++;

            EnsureKey(newState);
            if (KeepLog) _log.Add(new StateLogEntry(simTime, newState));
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Returns the total time accumulated in <paramref name="state"/>,
        /// including any time still accruing in the current state up to
        /// <paramref name="simTime"/>.
        /// </summary>
        public double GetTimeInState(ElementState state, double simTime)
        {
            double total = GetAccumulated(state);
            if (_currentState == state)
                total += System.Math.Max(0, simTime - _stateEnteredAt);
            return total;
        }

        /// <summary>
        /// Returns the fraction (0–1) of the observation period (since creation or the last
        /// <see cref="Reset"/>, e.g. a warm-up reset) that was spent in <paramref name="state"/>.
        /// </summary>
        public double GetStateRatio(ElementState state, double simTime)
        {
            double total = simTime - _resetAt; // observed time since the last reset
            if (total <= 0) return 0;
            return GetTimeInState(state, simTime) / total;
        }

        /// <summary>
        /// Returns a snapshot dictionary: state name → time accumulated.
        /// The current (still-open) state is closed at <paramref name="simTime"/>.
        /// Keys are the stable <see cref="ElementState.Name"/> strings so existing
        /// report / CSV / GA consumers are unaffected.
        /// </summary>
        public Dictionary<string, double> GetAllAccumulated(double simTime)
        {
            var result = new Dictionary<string, double>();
            foreach (var kv in _accumulated)
                result[kv.Key.Name] = kv.Value;

            // Close current open interval
            double elapsed = System.Math.Max(0, simTime - _stateEnteredAt);
            string currentName = _currentState.Name;
            if (result.ContainsKey(currentName))
                result[currentName] += elapsed;
            else
                result[currentName] = elapsed;

            return result;
        }

        /// <summary>Resets all accumulators and the log, entering <paramref name="initialState"/>.</summary>
        public void Reset(ElementState initialState = default, double simTime = 0.0)
        {
            if (initialState.IsNone) initialState = ElementState.IDLE;
            _accumulated.Clear();
            _log.Clear();
            Version++;
            _currentState   = initialState;
            _stateEnteredAt = simTime;
            _resetAt        = simTime;
            _log.Add(new StateLogEntry(simTime, initialState));
            EnsureKey(initialState);
        }

        // ── helpers ──────────────────────────────────────────────────────────
        private void EnsureKey(ElementState state)
        {
            if (!_accumulated.ContainsKey(state))
                _accumulated[state] = 0.0;
        }

        private void Accumulate(ElementState state, double elapsed)
        {
            if (_accumulated.ContainsKey(state))
                _accumulated[state] += elapsed;
            else
                _accumulated[state] = elapsed;
        }

        private double GetAccumulated(ElementState state)
        {
            return _accumulated.TryGetValue(state, out double v) ? v : 0.0;
        }
    }
}
