using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Experimentation.Core
{
    /// <summary>
    /// One row of the experiment design: a named scenario with one value per parameter of the Experimenter
    /// (<see cref="SimuLean.Optimization.ParameterSpec"/>, in the same order) and its number of replications.
    /// </summary>
    [Serializable]
    public class ExperimentScenario
    {
        [Tooltip("Name of the scenario (in the report).")]
        public string scenarioName = "Scenario";

        [Tooltip("Runs of the scenario (headless): replication r uses the seed base + r − 1, the same in every scenario.")]
        [Min(1)]
        public int replications = 1;

        [Tooltip("One value per parameter, in order, as text: a number, an option, true / false, or numbers separated by commas.")]
        public List<string> parameterValues = new List<string>();
    }
}
