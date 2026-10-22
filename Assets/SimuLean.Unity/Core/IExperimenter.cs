namespace SimuLean.Unity
{
    /// <summary>
    /// Contract that any experiment runner must fulfil so that UnitySimClock can call
    /// NextScenario() without knowing the concrete type.
    /// </summary>
    public interface IExperimenter
    {
        /// <summary>True while the experiment run is active.</summary>
        bool IsRunning { get; }

        /// <summary>Starts (or re-starts) the experiment run.</summary>
        void StartExperiments();

        /// <summary>Cancels a running experiment.</summary>
        void CancelExperiments();

        /// <summary>
        /// Advances to the next scenario when the current simulation run ends.
        /// Invoked internally via the <see cref="SimEvents.OnSimFinish"/> event.
        /// </summary>
        void NextScenario();
    }
}
