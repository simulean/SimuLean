using System;

namespace SimuLean.Unity
{
    /// <summary>
    /// Simulation event bus. Owned and auto-created by <see cref="UnitySimClock"/>.
    /// No ScriptableObject asset or Inspector drag-and-drop is required.
    /// </summary>
    public class SimEvents
    {
        //-------------------------------------------------General Simulation Events-------------------------------------------------

        public delegate void OnStartSimulationCallback(double maxTime = 0);
        public OnStartSimulationCallback OnSimStart;

        public Action OnSimStop;
        public Action OnSimResume;
        public Action OnSimFinish;


        //Experimenter
        public Action OnExperimentStart;
        public Action OnExperimentFinish;

        //XML Serializer 
        public Action OnMonthPassed;

    }
}
