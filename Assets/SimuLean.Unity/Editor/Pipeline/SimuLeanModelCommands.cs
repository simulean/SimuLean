using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Model management for agents: <c>simulean_models</c> lists, creates, opens, duplicates and renames the
    /// project's models and copies examples into new ones; <c>simulean_card</c> reads and writes a model's card (what it
    /// represents, its KPIs, the parameters worth varying, the assumptions). Both go through <see cref="SimuLeanModels"/>,
    /// as the SimuLean window's Models tab does. A command that would replace the open scene refuses while a scene has
    /// unsaved changes (there is no save prompt for an agent). <c>action=save</c> saves the
    /// open model only when it is one of the project's models <b>and</b> the commands created or opened it in this editor
    /// session (<see cref="AgentScenes"/>): the folder alone does not say whose the changes are — a user's own model edited by
    /// hand is in the same folder —, and an example, the neutral scene or any other scene is never saved by an agent.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        [CliCommand("simulean_models", "The project's models (one folder each under the models folder: scene, model card, data) " +
                                       "and the example scenes. action: list (default; with the examples), new (a folder of that name that holds no model yet, " +
                                       "e.g. its data, is used as it is), open, duplicate, " +
                                       "rename, copy_example (an example is never opened itself: it is copied into a new model, " +
                                       "whose card names it), save (the open model, when these commands created or opened it in this " +
                                       "editor session; never an example or another scene). Opening refuses while a scene has unsaved changes.", Tags = new[] { Tag })]
        public static object Models(
            [CliArg("action", "list | new | open | duplicate | rename | copy_example | save.")] string action = "list",
            [CliArg("model", "The model (new: its name; open / duplicate / rename: an existing model's name; save: empty = the open one, else it must be the open one).")] string model = "",
            [CliArg("newName", "duplicate / rename: the new name (duplicate: empty = a free numbered name).")] string newName = "",
            [CliArg("example", "copy_example: the example (its name or scene path, as list gives them).")] string example = "",
            [CliArg("open", "new / duplicate / copy_example: open the model afterwards.")] bool open = true,
            [CliArg("timeUnit", "new: the model's time unit — s (the default), min, h, d, or the seconds per unit (e.g. 60). Every time of the model (delays, horizon, warm-up) is in it.")] string timeUnit = "",
            [CliArg("horizon", "new: the run length (the clock's maxTime) in that unit (-1 = the clock's default; the answer gives it).")] double horizon = -1,
            [CliArg("seed", "new: a fixed model seed (the clock's seed mode Fixed with this seed: every run of the scene is reproducible; -1 = a new seed each run, the default).")] int seed = -1,
            [CliArg("warmup", "new: the warm-up in the model's time unit (statistics are reset then; -1 = none).")] double warmup = -1)
            => AgentReply.Of(() => ModelsOf((action ?? "list").Trim().ToLowerInvariant(), model, newName, example, open, timeUnit, horizon, seed, warmup));

        /// <summary>Seconds per model time unit from <c>s</c>, <c>min</c>, <c>h</c>, <c>d</c> (and their long forms) or a number of
        /// seconds; empty = 1 (a new model runs in seconds, which nothing else said).</summary>
        internal static double TimeUnitSeconds(string unit)
        {
            string u = (unit ?? "").Trim().ToLowerInvariant();
            switch (u)
            {
                case "": case "s": case "sec": case "second": case "seconds": return 1;
                case "min": case "m": case "minute": case "minutes": return 60;
                case "h": case "hr": case "hour": case "hours": return 3600;
                case "d": case "day": case "days": return 86400;
            }
            if (double.TryParse(u, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) && x > 0) return x;
            throw new ArgumentException($"timeUnit: '{unit}' is not s, min, h, d or a number of seconds per unit.");
        }

        /// <summary>The time unit (seconds per unit) and, with <paramref name="horizon"/> ≥ 0, the horizon of the clock of
        /// <paramref name="scene"/>; returns its horizon (0 without a clock).</summary>
        internal static double SetClock(Scene scene, double secondsPerUnit, double horizon)
        {
            UnitySimClock clock = null;
            foreach (var go in scene.GetRootGameObjects()) clock = clock ?? go.GetComponentInChildren<UnitySimClock>(true);
            return SetClock(clock, secondsPerUnit, horizon);
        }

        /// <summary>See <see cref="SetClock(Scene, double, double)"/>, on that clock.</summary>
        internal static double SetClock(UnitySimClock clock, double secondsPerUnit, double horizon)
        {
            if (clock == null) return 0;
            clock.TimeUnitSeconds = secondsPerUnit;
            if (horizon >= 0) clock.MaxTime = horizon;
            UnityEditor.EditorUtility.SetDirty(clock);
            return clock.MaxTime;
        }

        /// <summary>The seed and warm-up of a new model's clock:
        /// <paramref name="seed"/> ≥ 0 = Fixed mode with that seed, <paramref name="warmup"/> ≥ 0 = the warm-up; -1 = unchanged.</summary>
        internal static void SetClockRun(Scene scene, int seed, double warmup)
        {
            UnitySimClock clock = null;
            foreach (var go in scene.GetRootGameObjects()) clock = clock ?? go.GetComponentInChildren<UnitySimClock>(true);
            SetClockRun(clock, seed, warmup);
        }

        /// <summary>See <see cref="SetClockRun(Scene, int, double)"/>, on that clock.</summary>
        internal static void SetClockRun(UnitySimClock clock, int seed, double warmup)
        {
            if (clock == null) return;
            if (seed >= 0) clock.UseFixedSeed(seed);
            if (warmup >= 0) clock.WarmupTime = warmup;
            UnityEditor.EditorUtility.SetDirty(clock);
        }

        private static object ModelsOf(string action, string name, string newName, string example, bool open, string timeUnit = "", double horizon = -1,
                                       int seed = -1, double warmup = -1)
        {
            if (action != "new" && (!string.IsNullOrWhiteSpace(timeUnit) || horizon >= 0 || seed >= 0 || warmup >= 0))
                throw new ArgumentException("timeUnit / horizon / seed / warmup apply to action=new only (an existing model: simulean_set object=SimClock " +
                                            "key=timeUnitSeconds / maxTime / seed with seedMode = Fixed / warmupTime).");
            if (seed < -1) throw new ArgumentException($"seed: {seed} — a seed ≥ 0 (-1 = a new seed each run).");
            if (warmup < -1) throw new ArgumentException($"warmup: {warmup} — a time ≥ 0 (-1 = none).");
            switch (action)
            {
                case "":
                case "list":
                    return new
                    {
                        modelsFolder = SimuLeanModels.ModelsFolder,
                        open = SceneManager.GetActiveScene().path,
                        models = SimuLeanModels.List().Select(ModelSummary).ToList(),
                        examples = SimuLeanModels.Examples().Select(e => new
                        {
                            name = e.Name, scene = e.ScenePath, category = e.Category, displayName = e.DisplayName, description = e.Description,
                            copies = SimuLeanModels.CopiesOf(e.ScenePath).Select(m => m.Name).ToList(),
                        }).ToList(),
                    };
                case "new":
                {
                    if (open) RefuseUnsaved("new");
                    double unit = TimeUnitSeconds(timeUnit);
                    double maxTime = 0;
                    var made = SimuLeanModels.Create(name, open, "", scene =>
                    {
                        maxTime = SetClock(scene, unit, horizon);
                        SetClockRun(scene, seed, warmup);
                    });
                    RecordAgentScene(made.ScenePath);
                    return new
                    {
                        created = ModelSummary(made), opened = open,
                        settings = new
                        {
                            timeUnit = SimuLean.Results.ResultMetrics.TimeUnitName(unit), secondsPerTimeUnit = unit, horizon = maxTime,
                            seed = seed >= 0 ? (object)seed : "a new one each run in Play (seed=N fixes one); headless runs and studies use seeds 1..N (their seed= argument)",
                            warmup = warmup > 0 ? (object)warmup : "none (0: statistics from t = 0)",
                            note = "Every time of the model (delays, arrivals, horizon, warm-up) is in this unit; simulean_validate echoes the settings.",
                        },
                    };
                }
                case "open":
                {
                    var m = SimuLeanModels.Get(name);
                    RefuseUnsaved("open");
                    SimuLeanModels.Open(m);
                    RecordAgentScene(m.ScenePath);
                    return new { opened = ModelSummary(m) };
                }
                case "duplicate":
                {
                    var m = SimuLeanModels.Get(name);
                    if (open) RefuseUnsaved("duplicate");
                    var copy = SimuLeanModels.Duplicate(m, newName, open);
                    RecordAgentScene(copy.ScenePath);
                    return new { created = ModelSummary(copy), from = m.Name, opened = open };
                }
                case "rename":
                {
                    var m = SimuLeanModels.Get(name);
                    string before = m.ScenePath;
                    var renamed = SimuLeanModels.Rename(m, newName);
                    if (AgentScenes.Contains(before)) RecordAgentScene(renamed.ScenePath);
                    return new { renamed = ModelSummary(renamed), from = m.Name };
                }
                case "copy_example":
                {
                    var examples = SimuLeanModels.Examples();
                    string key = (example ?? "").Trim();
                    var e = examples.FirstOrDefault(x => x.ScenePath == key)
                            ?? examples.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException($"example: no example '{example}' (examples: {string.Join(", ", examples.Select(x => x.Name))}).");
                    if (open) RefuseUnsaved("copy_example");
                    var copies = SimuLeanModels.CopiesOf(e.ScenePath).Select(m => m.Name).ToList();
                    var made = SimuLeanModels.CopyExample(e.ScenePath, string.IsNullOrWhiteSpace(name) ? null : name, open);
                    RecordAgentScene(made.ScenePath);
                    return new
                    {
                        created = ModelSummary(made), example = e.ScenePath, opened = open,
                        warnings = copies.Count > 0
                            ? new[] { $"There were already copies of {e.Name} ({string.Join(", ", copies)}): open one with action=open instead of copying again." }
                            : new string[0],
                    };
                }
                case "save":
                    return SaveModel(name);
                default:
                    throw new ArgumentException($"action: '{action}' is not one of list, new, open, duplicate, rename, copy_example, save.");
            }
        }

        [CliCommand("simulean_card", "A model's card: what the model represents, the KPIs that matter, the parameters worth " +
                                     "varying, the assumptions, plus its name, description, author, dates and the example it was " +
                                     "copied from. No field = the whole card; field + value = set it (displayName, description, " +
                                     "author, represents, kpis, parameters, assumptions). Read it when you start on a model; " +
                                     "write what you learn. scripts (the open model): its choice rules (owner, setting, Why, reads, " +
                                     "parameters) and its decisions by rung of the ladder field / expression / query / reaction / rule.", Tags = new[] { Tag })]
        public static object Card(
            [CliArg("field", "The field to set (empty = read the card).")] string field = "",
            [CliArg("value", "Its new text (with field).")] string value = "",
            [CliArg("model", "The model (empty = the open scene's).")] string model = "")
            => AgentReply.Of(() =>
            {
                SimuLeanModelCard card;
                string where;
                if (!string.IsNullOrWhiteSpace(model))
                {
                    var m = SimuLeanModels.Get(model);
                    card = m.Card ?? throw new ArgumentException($"model: '{m.Name}' has no card ({m.CardPath}).");
                    where = m.ScenePath;
                }
                else
                {
                    where = SceneManager.GetActiveScene().path;
                    card = SimuLeanModels.CardOf(where);
                    if (card == null)
                        throw new ArgumentException($"The open scene ({(string.IsNullOrEmpty(where) ? "untitled" : where)}) has no model card: it is not one of " +
                                                    "the project's models. Make one with simulean_models action=new, or copy an example with action=copy_example.");
                }
                // the text an overwrite replaced, so it is never lost unseen (the card keeps no history)
                string previous = null;
                if (!string.IsNullOrWhiteSpace(field))
                {
                    string name = SimuLeanModels.WritableFields.FirstOrDefault(w => string.Equals(w, field.Trim(), StringComparison.OrdinalIgnoreCase));
                    previous = name != null ? card.Get(name) ?? "" : null;
                    SimuLeanModels.SetCardField(card, field, value);
                }
                return new
                {
                    scene = where, card = card.ToDictionary(), field = string.IsNullOrWhiteSpace(field) ? null : field.Trim(), previous,
                    scripts = CardScripts(where),
                };
            });

        /// <summary>
        /// The card's <c>scripts</c>: the model's choice rules — where each is used, its label, Why, reads and
        /// parameters — and how many of its decisions each rung of the escalation ladder makes (<see cref="SimuLean.Logic.LogicReport.Scripts"/>),
        /// read from the scene's model. Only for the open scene (another model's scene is not loaded): else a note. Also the
        /// card inspector's <i>Scripts / Logic</i> section (<see cref="SimuLeanModelCardEditor"/>); <paramref name="root"/> = the
        /// model's root (null = the scene's model).
        /// </summary>
        internal static object CardScripts(string scenePath, UnityEngine.GameObject root = null)
        {
            if (string.IsNullOrEmpty(scenePath) || scenePath != SceneManager.GetActiveScene().path)
                return new Dictionary<string, object> { ["note"] = "open the model (simulean_models action=open) to list its scripted logic" };
            SimuLean.Serialization.SimulationConfig model;
            try { model = SceneStudy.Of(root).Model; }
            catch (Exception e) { return new Dictionary<string, object> { ["note"] = "the scene's model could not be read: " + e.Message }; }
            return SimuLean.Logic.ModelLogic.Of(model).Scripts();
        }

        /// <summary>The <c>info</c> block of <c>simulean_model</c>: the open scene's model card, else null.</summary>
        internal static Dictionary<string, string> CardInfo()
        {
            var card = SimuLeanModels.CardOf(SceneManager.GetActiveScene().path);
            return card != null ? card.ToDictionary() : null;
        }

        private static object ModelSummary(SimuLeanModels.Model m) => new
        {
            name = m.Name, displayName = m.DisplayName, scene = m.ScenePath, description = m.Description,
            modified = m.Modified.ToString(SimuLeanModelCard.DateFormat), copiedFrom = m.CopiedFrom,
        };

        private const string AgentScenesKey = "SimuLean.AgentModelScenes";

        /// <summary>The model scenes <c>simulean_models</c> created, copied or opened in this editor session (kept across domain
        /// reloads, cleared when the editor closes): the ones <c>action=save</c> may save.</summary>
        internal static HashSet<string> AgentScenes
            => new HashSet<string>((UnityEditor.SessionState.GetString(AgentScenesKey, "") ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));

        internal static void RecordAgentScene(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return;
            var set = AgentScenes;
            if (set.Add(scenePath)) UnityEditor.SessionState.SetString(AgentScenesKey, string.Join("|", set));
        }

        /// <summary>Saves the open model (<c>simulean_models action=save</c>): only a project model (not an example, the
        /// neutral scene or another scene) that the commands created, copied or opened in this editor session — else its
        /// changes may be the user's, and the rule is never to save someone else's scene.</summary>
        private static object SaveModel(string name)
        {
            var scene = SceneManager.GetActiveScene();
            string path = scene.path;
            var m = SimuLeanModels.ModelOfScene(path);
            if (m == null)
                throw new InvalidOperationException($"save: the open scene ({(string.IsNullOrEmpty(path) ? "untitled" : path)}) is not one of the " +
                                                    $"project's models ({SimuLeanModels.ModelsFolder}/<Name>/<Name>.unity): an example, the neutral scene " +
                                                    "or another scene is never saved by an agent. Work in a model of your own: simulean_models action=new or copy_example.");
            if (!string.IsNullOrWhiteSpace(name) && SimuLeanModels.Find(name)?.ScenePath != path)
                throw new ArgumentException($"save: '{name}' is not the open model ({m.Name}); save saves the open one (open another with action=open).");
            if (!scene.isDirty) return new { saved = false, scene = path, model = m.Name, note = "No unsaved changes." };
            if (!AgentScenes.Contains(path))
                throw new InvalidOperationException($"save: model '{m.Name}' has unsaved changes, but it was not created or opened through " +
                                                    "simulean_models in this editor session, so they may be someone else's: ask the user to save it. " +
                                                    "(A model opened with simulean_models action=open, which needs it saved first, can then be saved.)");
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"save: Unity could not save {path}.");
            return new { saved = true, scene = path, model = m.Name };
        }

        private static void RefuseUnsaved(string action)
        {
            var dirty = SimuLeanTestRunner.ModifiedScenes();
            if (dirty.Count > 0)
                throw new InvalidOperationException($"{action}: these scenes have unsaved changes: {string.Join(", ", dirty)}. Save the ones you made " +
                                                    "(never save or discard someone else's), then call again; or pass open=false.");
        }
    }
}
