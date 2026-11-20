using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Pipeline.Commands;
using SimuLean.Optimization;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// <c>simulean_vocabulary</c> : the one key vocabulary of the agent interface, read-only, built from the
    /// code — the engine's <see cref="Vocabulary"/> (scopes, path grammar, statistics, states, expressions, options, catalog
    /// keys) plus, by reflection over the <c>[CliCommand]</c> methods, every command argument under its concept with the
    /// commands that take it.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        [CliCommand("simulean_vocabulary", "The names the SimuLean commands use, from the code: every command argument grouped by " +
                                           "concept (object, factors, horizon…; retired names listed), the factor / metric / result " +
                                           "scopes, the path grammar with an example per form, the statistics per scope, the state " +
                                           "names (declared, and the scene model's), the expression roots / built-ins / functions, " +
                                           "the option enums and the catalog keys with their rule. Read-only. By default an index of its sections " +
                                           "(each one's size and what it holds: the whole is over what a tool answer holds); section= answers them.", Tags = new[] { Tag })]
        public static object VocabularyOf(
            [CliArg("root", "Model root GameObject whose built model adds its state names (empty = the scene's model, if any).")] string root = "",
            [CliArg("section", "The sections answered: one (arguments, retired, scopes, paths, statistics, states, expressions, options, catalog), several comma-separated, a part of one (arguments.study: a concept; expressions.functions) or all (the whole vocabulary, ~80 KB); empty = the index.")] string section = "")
            => AgentReply.Of(() =>
            {
                var whole = WholeVocabulary(root);
                return string.IsNullOrWhiteSpace(section) ? Vocabulary.Index(whole, MaxAnswer) : Vocabulary.Section(whole, section);
            });

        /// <summary>The whole vocabulary: the engine's (<see cref="Vocabulary.Describe"/>, with the scene model's states) and
        /// every command argument under its concept with the commands that take it.</summary>
        internal static Dictionary<string, object> WholeVocabulary(string root)
        {
            SimuLean.Serialization.SimulationConfig model = null;
            try { model = SceneStudy.Of(string.IsNullOrEmpty(root) ? null : FindObject(root)).Model; }
            catch (Exception) when (string.IsNullOrEmpty(root)) { }   // no model in the scene: the declared states only
            var d = Vocabulary.Describe(model != null && model.Elements.Count > 0 ? model : null);
            var commands = CommandArguments();
            d["arguments"] = Vocabulary.Concepts.Select(c => (object)new Dictionary<string, object>
            {
                ["concept"] = c.Name, ["meaning"] = c.Meaning,
                ["arguments"] = c.Arguments.Select(a => (object)new Dictionary<string, object>
                {
                    ["name"] = a,
                    ["commands"] = commands.Where(x => x.argument == a).Select(x => x.command).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(),
                }).ToList(),
            }).ToList();
            return d;
        }

        /// <summary>Every (command, argument) of the SimuLean commands, by reflection over their attributes.</summary>
        internal static List<(string command, string argument)> CommandArguments()
        {
            var list = new List<(string, string)>();
            foreach (var type in typeof(SimuLeanCommands).Assembly.GetTypes())
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var cmd = m.GetCustomAttribute<CliCommandAttribute>();
                    if (cmd == null || !cmd.Name.StartsWith("simulean_", StringComparison.Ordinal)) continue;
                    foreach (var p in m.GetParameters())
                    {
                        var arg = p.GetCustomAttribute<CliArgAttribute>();
                        if (arg != null) list.Add((cmd.Name, arg.Name));
                    }
                }
            return list;
        }
    }
}
