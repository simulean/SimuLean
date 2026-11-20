using System;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>A station wrapper whose engine element can set up before its process (shows the Setup section):
    /// Multi-Server, Combiner, Picking Station, Assembly Station, Stacker, Erection Dock.</summary>
    public interface ISetupUser
    {
        StationSetupConfig Setup { get; }
    }

    /// <summary>The crew that sets a station up (<see cref="StationSetupConfig.setupBy"/>): the same fields as the process's
    /// use of a team — team, requirements, priority, preemption — of its own.</summary>
    [Serializable]
    public class SetupResourceUse : WaitingResourceUse
    {
        /// <summary>Writes the setup crew's keys (<c>setup.setupBy.team</c>, <c>setup.setupBy.requirements</c>, <c>setup.setupBy.priority</c>,
        /// <c>setup.setupBy.preemption</c>, <c>setup.setupBy.preemptable</c>).</summary>
        public void Export(ElementConfig ec, string teamId)
        {
            ExportUse(ec, teamId, "setup.setupBy.team", "setup.setupBy.requirements", "setup.setupBy.priority");
            ExportPreemption(ec, "setup.setupBy.preemption", "setup.setupBy.preemptable");
            ExportAcquire(ec, "setup.setupBy.acquire");
            ExportMaxWait(ec, "setup.setupBy.maxWait", "setup.setupBy.onTimeout");
        }
    }

    /// <summary>
    /// A station's setup (changeover) before its process — the shared Setup section of the stations that have one (engine
    /// <c>StationSetup</c>; one build path: the extractors write its <c>setup.*</c> settings, the engine builds it). Default
    /// <see cref="SetupWhen.None"/>: no setup, nothing exported, the model unchanged.
    /// </summary>
    [Serializable]
    public class StationSetupConfig
    {
        [Tooltip("When the station sets up before an item's process. None: never. Each Item: before every item. Label Change: " +
                 "only when the item's label (below) differs from the previous item's at the same slot (server / assembly).")]
        public SetupWhen when = SetupWhen.None;

        [Tooltip("Label Change: the item label whose change needs a setup (e.g. type, ref).")]
        public string label = "type";

        [Tooltip("Label Change: the first item of each slot needs a setup too (the station does not know how it was left).")]
        public bool setupFirst = true;

        [Tooltip("The setup time: any distribution, as the process time (its own random stream; an expression may read the item).")]
        public RandomProcessConfig time = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1 };

        [Tooltip("Optional: a data table with the columns from, to, time — the setup time per pair of the label's values (a " +
                 "number or a distribution such as Uniform~1~3; * = any value, an empty 'from' = the first setup). Pairs it " +
                 "does not list take the setup time above.")]
        public UnityDataTable matrix;

        [Tooltip("Who sets the station up: a team (its members travel to the station and show SETUP). Empty = the machine " +
                 "sets up alone, even when its process needs an operator.")]
        public SetupResourceUse setupBy = new SetupResourceUse();

        [Tooltip("The setup is done by the process's team (Setup By = the Process team): the members the process needs come " +
                 "before the setup, set up and stay for the process — one request, no new wait between setup and process.")]
        public bool keepForProcess;

        public bool IsUsed => when != SetupWhen.None;

        /// <summary>Writes the setup's own keys (rule, label, first item, time, keep); the crew's team needs its id
        /// (<see cref="ExportCrew"/>). Also with the rule None (a study may switch <c>setup.when</c> from there; the
        /// build ignores the other keys then).</summary>
        public void Export(ElementConfig ec)
        {
            ec.Set("setup.when", when.ToString());
            ec.Set("setup.label", (label ?? "").Trim());   // the matrix's pairs are values of the label too
            ec.Set("setup.setupFirst", setupFirst);
            if (time != null) ec.Set("setup.time", time.ToSettings());
            if (matrix != null) ec.Set("setup.matrix", matrix.TableName);
            ec.Set("setup.keepForProcess", keepForProcess);
        }

        /// <summary>Writes the crew's keys with the team's model id (nothing without a rule or a team).</summary>
        public void ExportCrew(ElementConfig ec, string teamId)
        {
            if (IsUsed && setupBy != null && setupBy.IsUsed) setupBy.Export(ec, teamId);
        }
    }
}
