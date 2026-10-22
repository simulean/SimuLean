using System;
using System.Collections.Generic;
using UnityEngine;
using SimuLean.Serialization;
using SimuLean.Unity;

namespace SimuLean.Optimization
{
    /// <summary>What a parameter of the scene changes.</summary>
    public enum ParameterTarget
    {
        /// <summary>Any parameter of an element (its catalog: capacity, times, options, vectors, user properties…).</summary>
        Element,
        /// <summary>Any parameter of a resource team (members, speed, load times…).</summary>
        Team,
        /// <summary>The order of the jobs of a source's schedule table.</summary>
        ScheduleSequence,
        /// <summary>One yes / no decision per row of a source's schedule table, written to a column.</summary>
        ScheduleBinary,
        /// <summary>Any parameter of a downtime (times to failure / repair, mode…).</summary>
        Downtime,
        /// <summary>A reaction's parameters: enabled, its actions' numbers and stop durations.</summary>
        Reaction,
    }

    /// <summary>
    /// A parameter of the scene to optimise or vary (Optimizer, Experimenter): the scene object, which of its catalog
    /// parameters (<see cref="ModelParameterCatalog"/>: every key its config has, of any kind) and the range of a number.
    /// Turned into a <see cref="ModelParameter"/> over the extracted config, so it works the same in every run.
    /// </summary>
    [Serializable]
    public class ParameterSpec
    {
        [Tooltip("Name of the parameter in results and reports (empty = owner.key).")]
        public string name = "";
        public ParameterTarget target = ParameterTarget.Element;
        [Tooltip("The element (Element, Schedule…).")]
        public SElement element;
        [Tooltip("The resource team (Team).")]
        public UnityResourceTeam team;
        [Tooltip("The downtime (Downtime).")]
        public UnityDowntime downtime;
        [Tooltip("The reaction (Reaction).")]
        public UnityReaction reaction;
        [Tooltip("Which parameter: a key from the list.")]
        public string key = "";
        [Tooltip("Schedule binary: the column written (0 / 1 per row), a label the model reads.")]
        public string column = "";
        [Tooltip("Lowest value (numbers; every value of a vector).")]
        public double min = 1;
        [Tooltip("Highest value (numbers; every value of a vector).")]
        public double max = 10;

        public string Label
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                string what = target == ParameterTarget.ScheduleSequence ? "sequence" : target == ParameterTarget.ScheduleBinary ? column : key;
                return (Owner ?? "?") + "." + what;
            }
        }

        /// <summary>The owner's name in the model: element / team name, downtime id (null when unset).</summary>
        public string Owner => target == ParameterTarget.Team ? team?.TeamName
                             : target == ParameterTarget.Downtime ? downtime?.ConfigId
                             : target == ParameterTarget.Reaction ? (reaction != null ? reaction.ConfigId : null)
                             : element != null ? element.name : null;

        private ParameterScope Scope => target == ParameterTarget.Team ? ParameterScope.Team
                                      : target == ParameterTarget.Downtime ? ParameterScope.Downtime
                                      : target == ParameterTarget.Reaction ? ParameterScope.Reaction : ParameterScope.Element;

        /// <summary>What the owner offers (the editor's list), from its own config; empty for schedule targets.</summary>
        public IReadOnlyList<ParameterInfo> Options()
        {
            switch (target)
            {
                case ParameterTarget.Element:
                    return element != null ? ModelParameterCatalog.ForElement(SceneExtractor.ExtractElement(element)) : new List<ParameterInfo>();
                case ParameterTarget.Team:
                    return team != null ? ModelParameterCatalog.ForTeam(team.ToConfig(team.TeamName)) : new List<ParameterInfo>();
                case ParameterTarget.Downtime:
                    return downtime != null ? ModelParameterCatalog.ForDowntime(downtime.ToConfig(se => se != null ? se.name : null, SimCalendar.Default))
                                            : new List<ParameterInfo>();
                case ParameterTarget.Reaction:
                    return reaction != null ? ModelParameterCatalog.ForReaction(reaction.ToConfig(se => se != null ? se.name : null))
                                            : new List<ParameterInfo>();
            }
            return new List<ParameterInfo>();
        }

        /// <summary>The catalog entry of this parameter, or null.</summary>
        public ParameterInfo Info()
        {
            foreach (var p in Options()) if (p.Key == key) return p;
            return null;
        }

        /// <summary>The model parameter over <paramref name="config"/> (the extracted scene). Throws when incomplete.</summary>
        public ModelParameter ToModelParameter(SimulationConfig config) => ToModelParameter(config, min, max);

        /// <summary>The same with another range (the Experimenter: the scenarios' values).</summary>
        public ModelParameter ToModelParameter(SimulationConfig config, double min, double max)
        {
            switch (target)
            {
                case ParameterTarget.Element:
                case ParameterTarget.Team:
                case ParameterTarget.Downtime:
                case ParameterTarget.Reaction:
                    if (Owner == null) throw new InvalidOperationException($"Parameter '{Label}': choose the {target.ToString().ToLowerInvariant()}.");
                    if (string.IsNullOrEmpty(key)) throw new InvalidOperationException($"Parameter '{Label}': choose which parameter.");
                    try { return ConfigParameter.Of(config, Scope, Owner, key, min, max, Label); }
                    catch (ArgumentException e) { throw new InvalidOperationException($"Parameter '{Label}': {e.Message}"); }
                case ParameterTarget.ScheduleSequence:
                case ParameterTarget.ScheduleBinary:
                {
                    if (element == null) throw new InvalidOperationException($"Parameter '{Label}': choose a source with a schedule table.");
                    var ec = config.Elements.Find(e => e.Name == element.name);
                    int rows = ModelParameterCatalog.ScheduleRows(ec);
                    if (rows == 0) throw new InvalidOperationException($"Parameter '{Label}': '{element.name}' has no schedule table.");
                    if (target == ParameterTarget.ScheduleSequence) return new ScheduleSequenceParameter(element.name, rows, null, Label);
                    if (string.IsNullOrWhiteSpace(column)) throw new InvalidOperationException($"Parameter '{Label}': name the column.");
                    return new ScheduleBinaryParameter(element.name, column.Trim(), rows, Label);
                }
            }
            throw new InvalidOperationException($"Parameter '{Label}': unknown target.");
        }
    }

    /// <summary>An observed value of the runs (Optimizer, Experimenter): a statistic of an element, a team or a list.</summary>
    [Serializable]
    public class MetricSpec
    {
        [Tooltip("Name of the metric: the variable of the fitness expression and the column of the reports (letters, digits, _).")]
        public string name = "throughput";
        public MetricScope scope = MetricScope.Element;
        public SElement element;
        public UnityResourceTeam team;
        public UnitySimList list;
        public UnityReaction reaction;
        [Tooltip("The statistic (from the list; state:NAME = share of time in a state; member:NAME = a public number of the engine element).")]
        public string statistic = "input";

        public ModelMetric ToModelMetric()
        {
            string target = scope == MetricScope.Model ? "model"
                          : scope == MetricScope.Element ? element?.name : scope == MetricScope.Team ? team?.TeamName
                          : scope == MetricScope.Reaction ? (reaction != null ? reaction.ConfigId : null) : list?.ListName;
            if (string.IsNullOrEmpty(target)) throw new InvalidOperationException($"Metric '{name}': choose the {scope.ToString().ToLowerInvariant()}.");
            return new ModelMetric(name, scope, target, statistic);
        }

        public static IReadOnlyList<string> StatisticsOf(MetricScope scope)
            => scope == MetricScope.Element ? MetricCatalog.ElementStatistics
             : scope == MetricScope.Team ? MetricCatalog.TeamStatistics
             : scope == MetricScope.Model ? MetricCatalog.ModelStatistics
             : scope == MetricScope.Reaction ? MetricCatalog.ReactionStatistics : MetricCatalog.ListStatistics;
    }

    /// <summary>
    /// The settings of an optimisation algorithm, shown in the Optimizer's inspector. <b>Plug-in point:</b> a new
    /// algorithm (an own metaheuristic, an adapter to a library) is a subclass that creates its
    /// <see cref="IOptimizationAlgorithm"/> — the inspector lists every subclass, nothing else to register.
    /// </summary>
    [Serializable]
    public abstract class AlgorithmSettings
    {
        public abstract string DisplayName { get; }
        public abstract IOptimizationAlgorithm Create();

        /// <summary>The settings as name → value (reports).</summary>
        public Dictionary<string, string> Describe()
        {
            var d = new Dictionary<string, string> { ["Algorithm"] = DisplayName };
            foreach (var f in GetType().GetFields())
                d[f.Name] = Convert.ToString(f.GetValue(this), System.Globalization.CultureInfo.InvariantCulture);
            return d;
        }
    }

    [Serializable]
    public class GeneticAlgorithmSettings : AlgorithmSettings
    {
        [Min(2)] public int populationSize = 30;
        [Min(1)] public int generations = 50;
        [Range(0, 1)] public float crossoverRate = 0.9f;
        [Range(0, 1)] public float mutationRate = 0.15f;
        [Tooltip("Best candidates copied unchanged to the next generation.")]
        [Min(0)] public int elites = 2;
        [Min(1)] public int tournamentSize = 3;

        public override string DisplayName => "Genetic algorithm";
        public override IOptimizationAlgorithm Create() => new GeneticOptimizer
        {
            PopulationSize = populationSize, Generations = generations, CrossoverRate = crossoverRate,
            MutationRate = mutationRate, Elites = elites, TournamentSize = tournamentSize,
        };
    }

    [Serializable]
    public class RandomSearchSettings : AlgorithmSettings
    {
        [Tooltip("Candidates to evaluate.")]
        [Min(1)] public int samples = 100;
        [Tooltip("Candidates per iteration (progress points).")]
        [Min(1)] public int batchSize = 10;

        public override string DisplayName => "Random search";
        public override IOptimizationAlgorithm Create() => new RandomSearch { Samples = samples, BatchSize = batchSize };
    }
}
